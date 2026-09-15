using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  private const int PatientsPageSize = 20;

  public class PatientSaveRequest {
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public int Sex { get; set; }
  }

  // Patients with at least one TherapySlot for this therapist in the last 6
  // months - raw SQL per CPU's call, not an EF join.
  private HashSet<int> GetPatientIdsForTherapistRecentSlots(int therapistId) {
    var results = new HashSet<int>();
    var connection = _db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;
    if (shouldClose) {
      connection.Open();
    }

    try {
      using var command = connection.CreateCommand();
      command.CommandText = @"
        SELECT DISTINCT therapies.PatientId AS patient_id
        FROM therapyslots
        INNER JOIN therapyparts ON therapyparts.Id = therapyslots.TherapyPartId
        INNER JOIN therapies ON therapies.Id = therapyparts.TherapyId
        WHERE therapyslots.TherapistId = @therapistId
          AND therapyslots.Date >= @sixMonthsAgo";

      var therapistParam = command.CreateParameter();
      therapistParam.ParameterName = "@therapistId";
      therapistParam.Value = therapistId;
      command.Parameters.Add(therapistParam);

      var dateParam = command.CreateParameter();
      dateParam.ParameterName = "@sixMonthsAgo";
      dateParam.Value = DateTime.Today.AddMonths(-6);
      command.Parameters.Add(dateParam);

      using var reader = command.ExecuteReader();
      while (reader.Read()) {
        results.Add(reader.GetInt32(reader.GetOrdinal("patient_id")));
      }
    } finally {
      if (shouldClose) {
        connection.Close();
      }
    }

    return results;
  }

  [HttpGet("/Patients/List")]
  public IActionResult PatientsList(string? filter, int page = 1, string sortBy = "created", string sortDir = "desc", bool soloMieiPazienti = false) {
    var query = _db.Patients.AsQueryable();

    if (!string.IsNullOrEmpty(filter) && filter.Length >= 3) {
      query = query.Where(p => p.Name.Contains(filter) || p.Phone.Contains(filter));
    }

    // "Solo i miei pazienti" (therapists only) - CPU's call to use raw SQL here
    // rather than an EF join.
    if (soloMieiPazienti) {
      var currentTherapistId = GetCurrentTherapistId();
      if (currentTherapistId == null) {
        return Forbid();
      }

      var myPatientIds = GetPatientIdsForTherapistRecentSlots(currentTherapistId.Value);
      query = query.Where(p => myPatientIds.Contains(p.Id));
    }

    // Duplicate-name detection runs against the whole filtered set (not just the
    // current page) so birthdate shows for every matching row regardless of paging.
    var duplicateNames = query
      .GroupBy(p => p.Name)
      .Where(g => g.Count() > 1)
      .Select(g => g.Key)
      .ToList();

    query = sortBy == "name"
      ? (sortDir == "desc" ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name))
      : (sortDir == "asc" ? query.OrderBy(p => p.ModDate) : query.OrderByDescending(p => p.ModDate));

    var totalCount = query.Count();

    var rows = query
      .Skip((page - 1) * PatientsPageSize)
      .Take(PatientsPageSize)
      .ToList();

    var pageIds = rows.Select(p => p.Id).ToList();

    // Recap: current (not Completed/Cancelled) Therapy's Parts' TherapyType
    // abbreviations, falling back to the full Name when Abbreviazione isn't set.
    var currentTherapies = _db.Therapies
      .Where(t => pageIds.Contains(t.PatientId) && t.Status != TherapyStatus.Completed && t.Status != TherapyStatus.Cancelled)
      .ToList();

    var therapyIds = currentTherapies.Select(t => t.Id).ToList();
    var parts = _db.TherapyParts.Where(p => therapyIds.Contains(p.TherapyId)).ToList();
    var typeIds = parts.Select(p => p.TherapyTypeId).Distinct().ToList();
    var typesCache = _db.TherapyTypes.Where(t => typeIds.Contains(t.Id)).ToDictionary(t => t.Id, t => t);

    var recapByPatient = new Dictionary<int, string>();
    foreach (var therapy in currentTherapies) {
      var therapyParts = parts.Where(p => p.TherapyId == therapy.Id);
      var labels = therapyParts
        .Select(p => typesCache.ContainsKey(p.TherapyTypeId) ? typesCache[p.TherapyTypeId] : null)
        .Where(t => t != null)
        .Select(t => string.IsNullOrEmpty(t!.Abbreviazione) ? t.Name : t.Abbreviazione)
        .ToList();

      if (labels.Count > 0) {
        recapByPatient[therapy.PatientId] = string.Join(", ", labels);
      }
    }

    var resultRows = rows.Select(p => new {
      p.Id,
      p.Name,
      p.Phone,
      p.DateOfBirth,
      p.Sex,
      p.ModDate,
      isDuplicateName = duplicateNames.Contains(p.Name),
      therapyRecap = recapByPatient.ContainsKey(p.Id) ? recapByPatient[p.Id] : ""
    });

    return Ok(new { rows = resultRows, totalCount, page, pageSize = PatientsPageSize });
  }

  [HttpGet("/Patients/CheckDuplicateName")]
  public IActionResult PatientsCheckDuplicateName(string name, int? excludeId) {
    var matches = _db.Patients
      .Where(p => p.Name == name && (!excludeId.HasValue || p.Id != excludeId.Value))
      .Select(p => new { p.Id, p.Phone, p.DateOfBirth })
      .ToList();

    return Ok(matches);
  }

  [HttpGet("/Patients/Get/{id}")]
  public IActionResult PatientsGet(int id) {
    var patient = _db.Patients.Find(id);

    if (patient == null) {
      return NotFound();
    }

    return Ok(new {
      patient.Id,
      patient.Name,
      patient.Phone,
      patient.DateOfBirth,
      patient.Sex,
      patient.DocumentFileName,
      currentTherapy = GetCurrentTherapyInfo(id)
    });
  }

  // A therapy whose sessions are all done (Status == Completed) still needs to
  // stay "current" on the patient page until its Foglio Firma work is ALSO fully
  // wrapped up - otherwise the row needed to advance it would disappear the
  // moment the last session is marked Done. Meaningless for Privata (Foglio
  // Firma never applies there), so those are "fully done" as soon as Status is.
  private static bool IsTherapyFullyDone(Therapy t) {
    if (t.Status != TherapyStatus.Completed) {
      return false;
    }
    return t.BillingCategory == TherapyBillingCategory.Privata || t.FoglioFirmaStatus == FoglioFirmaStatus.Completed;
  }

  // "Current" = not yet Completed/Cancelled. Only one such Therapy can exist per
  // patient at a time (enforced on Create), so the most recent one is unambiguous.
  // "Current" = not yet Cancelled; prefers an actually-active one, falling back
  // to a Completed one still awaiting Foglio Firma finalization. Shared by
  // GetCurrentTherapyInfo (patient page) and PatientsStatus (Patient Status
  // page) so both agree on which therapy is "current."
  private Therapy? GetCurrentTherapy(int patientId) {
    var candidates = _db.Therapies
      .Where(t => t.PatientId == patientId && t.Status != TherapyStatus.Cancelled)
      .OrderByDescending(t => t.Id)
      .ToList();

    return candidates.FirstOrDefault(t => t.Status != TherapyStatus.Completed)
      ?? candidates.FirstOrDefault(t => !IsTherapyFullyDone(t));
  }

  private object? GetCurrentTherapyInfo(int patientId) {
    var therapy = GetCurrentTherapy(patientId);

    if (therapy == null) {
      return null;
    }

    var therapyTypeNames = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Name);
    var therapyTypeCategories = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Category);
    var therapyTypeDurations = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Duration);
    var therapyTypeExecutionTypes = _db.TherapyTypes.ToDictionary(t => t.Id, t => t.Type);

    var parts = _db.TherapyParts
      .Where(p => p.TherapyId == therapy.Id)
      .OrderBy(p => p.Id)
      .ToList()
      .Select(p => {
        var placedCount = _db.TherapySlots.Count(s => s.TherapyPartId == p.Id && s.Status != TherapySlotStatus.Rescheduled);

        return new {
          p.Id,
          p.TherapyTypeId,
          therapyTypeName = therapyTypeNames.ContainsKey(p.TherapyTypeId) ? therapyTypeNames[p.TherapyTypeId] : null,
          therapyTypeCategory = therapyTypeCategories.ContainsKey(p.TherapyTypeId) ? therapyTypeCategories[p.TherapyTypeId] : (int?)null,
          therapyTypeExecutionType = therapyTypeExecutionTypes.ContainsKey(p.TherapyTypeId) ? therapyTypeExecutionTypes[p.TherapyTypeId] : (int?)null,
          therapyTypeDuration = therapyTypeDurations.ContainsKey(p.TherapyTypeId) ? therapyTypeDurations[p.TherapyTypeId] : (int?)null,
          p.SessionCount,
          p.DefaultGinnasticaAttivaSlots,
          placedCount,
          remaining = p.SessionCount - placedCount
        };
      });

    return new {
      therapy.Id,
      therapy.Name,
      therapy.Status,
      statusLabel = TherapyStatus.ToLabel(therapy.Status),
      therapy.BillingCategory,
      billingCategoryLabel = TherapyBillingCategory.ToLabel(therapy.BillingCategory),
      isPrivate = therapy.BillingCategory == TherapyBillingCategory.Privata,
      therapy.FoglioFirmaStatus,
      foglioFirmaStatusLabel = FoglioFirmaStatus.ToLabel(therapy.FoglioFirmaStatus),
      canAdvanceFoglioFirma = therapy.FoglioFirmaStatus == FoglioFirmaStatus.ToBeCreated
        || therapy.FoglioFirmaStatus == FoglioFirmaStatus.ToBeFinalized,
      parts
    };
  }

  private class PatientPlanSlotDto {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
    public string TherapyTypeName { get; set; } = "?";
    public string TherapistName { get; set; } = "Reparto";
    // Small extra detail shown next to the line, e.g. "+15 min prima" - empty
    // when this slot has no Ginnastica Attiva attached.
    public string GinnasticaAttivaLabel { get; set; } = "";
  }

  // All slots for the patient's current therapy (see GetCurrentTherapyInfo above for
  // "current" - not yet Completed/Cancelled), sorted by date/time - used by the
  // "Genera piano" button on the patient form. Rescheduled slots are excluded, same
  // convention as everywhere else a slot list is shown (a Rescheduled slot was moved
  // to a different slot, which appears in this same list on its own).
  [HttpGet("/Patients/PlanData/{id}")]
  public IActionResult PatientPlanData(int id) {
    var patient = _db.Patients.Find(id);

    if (patient == null) {
      return NotFound();
    }

    var therapy = _db.Therapies
      .Where(t => t.PatientId == id && t.Status != TherapyStatus.Completed && t.Status != TherapyStatus.Cancelled)
      .OrderByDescending(t => t.Id)
      .FirstOrDefault();

    if (therapy == null) {
      return Ok(new { patientName = patient.Name, therapyName = (string?)null, slots = new List<PatientPlanSlotDto>() });
    }

    var partIds = _db.TherapyParts.Where(p => p.TherapyId == therapy.Id).Select(p => p.Id).ToList();

    var typeCache = GetTypeCache();
    var partCache = GetPartCache();
    var therapistNameCache = GetTherapistNameCache();

    var slots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .ToList()
      .Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

        var gaLabel = "";
        if (s.GinnasticaAttivaSlots != 0) {
          var minutes = Math.Abs(s.GinnasticaAttivaSlots) * 15;
          gaLabel = "+" + minutes + " min " + (s.GinnasticaAttivaSlots < 0 ? "prima" : "dopo");
        }

        return new PatientPlanSlotDto {
          Date = s.Date,
          TimeSlot = s.TimeSlot,
          TherapyTypeName = type != null ? type.Name : "?",
          TherapistName = s.TherapistId.HasValue && therapistNameCache.ContainsKey(s.TherapistId.Value)
            ? therapistNameCache[s.TherapistId.Value]
            : "Reparto",
          GinnasticaAttivaLabel = gaLabel
        };
      })
      .OrderBy(s => s.Date)
      .ThenBy(s => s.TimeSlot)
      .ToList();

    return Ok(new {
      patientName = patient.Name,
      therapyName = therapy.Name,
      slots
    });
  }

  [HttpPost("/Patients/Create")]
  public IActionResult PatientsCreate([FromBody] PatientSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var patient = new Patient {
      Name = request.Name,
      Phone = request.Phone,
      DateOfBirth = request.DateOfBirth,
      Sex = request.Sex,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    _db.Patients.Add(patient);
    _db.SaveChanges();

    return Ok(new { patient.Id });
  }

  [HttpPut("/Patients/Update/{id}")]
  public IActionResult PatientsUpdate(int id, [FromBody] PatientSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var patient = _db.Patients.Find(id);

    if (patient == null) {
      return NotFound();
    }

    patient.Name = request.Name;
    patient.Phone = request.Phone;
    patient.DateOfBirth = request.DateOfBirth;
    patient.Sex = request.Sex;
    patient.ModDate = DateTime.Now;
    patient.ModUser = currentUserId.Value;

    _db.SaveChanges();

    return Ok();
  }

  public class PatientRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  [HttpPost("/Patients/Remove/{id}")]
  public IActionResult PatientsRemove(int id, [FromBody] PatientRemoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var patient = _db.Patients.Find(id);

    if (patient == null) {
      return NotFound();
    }

    if (!string.Equals(request.ConfirmName, patient.Name, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest();
    }

    // No age gate at all - a brand-new patient with no data is deletable immediately.
    // The only guard is recent scheduling activity, checked via the Patient -> Therapy
    // -> TherapyPart -> TherapySlot chain (no navigation properties on these models,
    // so this is done as explicit nested queries).
    var threeMonthsAgo = DateOnly.FromDateTime(DateTime.Now.AddMonths(-3));

    var hasRecentSlot = _db.Therapies
      .Where(t => t.PatientId == id)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .SelectMany(p => _db.TherapySlots.Where(s => s.TherapyPartId == p.Id))
      .Any(s => s.Date >= threeMonthsAgo);

    if (hasRecentSlot) {
      return BadRequest(new { message = "Il paziente ha attività pianificata negli ultimi 3 mesi e non può essere eliminato." });
    }

    _db.Patients.Remove(patient);
    _db.SaveChanges();

    return Ok();
  }

  private const long PatientDocumentMaxBytes = 20 * 1024 * 1024; // 20 MB

  private string GetPatientDocumentFolder() {
    return _config["DocumentStoragePath"] ?? string.Empty;
  }

  [HttpPost("/Patients/UploadDocument/{id}")]
  public IActionResult PatientsUploadDocument(int id, IFormFile file) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var patient = _db.Patients.Find(id);

    if (patient == null) {
      return NotFound();
    }

    if (file == null || file.Length == 0) {
      return BadRequest(new { message = "Nessun file ricevuto." });
    }

    if (file.Length > PatientDocumentMaxBytes) {
      return BadRequest(new { message = "Il file supera la dimensione massima di 20 MB." });
    }

    var extension = Path.GetExtension(file.FileName);
    if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)) {
      return BadRequest(new { message = "È consentito solo il caricamento di file PDF." });
    }

    var folder = GetPatientDocumentFolder();
    Directory.CreateDirectory(folder);

    // Physically replace: delete the old file for this patient before writing the new one.
    if (!string.IsNullOrEmpty(patient.DocumentFileName)) {
      var oldPath = Path.Combine(folder, patient.DocumentFileName);
      if (System.IO.File.Exists(oldPath)) {
        System.IO.File.Delete(oldPath);
      }
    }

    var fileName = DateTime.Now.ToString("yyyyMMdd") + "_" + id + ".pdf";
    var fullPath = Path.Combine(folder, fileName);

    using (var stream = new FileStream(fullPath, FileMode.Create)) {
      file.CopyTo(stream);
    }

    patient.DocumentFileName = fileName;
    patient.DocumentAttachedDate = DateTime.Now;
    _db.SaveChanges();

    return Ok(new { fileName });
  }

  [HttpGet("/Patients/DocumentStatus/{id}")]
  public IActionResult PatientsDocumentStatus(int id) {
    var patient = _db.Patients.Find(id);

    if (patient == null || string.IsNullOrEmpty(patient.DocumentFileName)) {
      return Ok(new { exists = false });
    }

    var fullPath = Path.Combine(GetPatientDocumentFolder(), patient.DocumentFileName);
    return Ok(new { exists = System.IO.File.Exists(fullPath) });
  }

  private class PatientStatusSlotDto {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
    public string TherapyTypeName { get; set; } = "?";
    public string TherapistName { get; set; } = "Reparto";
    public int Status { get; set; }
    public string StatusLabel { get; set; } = "?";
  }

  // Quick-glance page (CPU's call): name, phone, current therapy, sessions
  // remaining, next 3 upcoming slots, most recent past slot with its
  // done/not-done status, and whether there's a document to view. Reachable
  // from a button next to the patient's name both on the patient page and in
  // the slot detail popup - both open this same page.
  [HttpGet("/Patients/Status/{id}")]
  public IActionResult PatientsStatus(int id) {
    if (GetCurrentTherapistId() == null) {
      return Forbid();
    }

    var patient = _db.Patients.Find(id);

    if (patient == null) {
      return NotFound();
    }

    var hasDocument = !string.IsNullOrEmpty(patient.DocumentFileName)
      && System.IO.File.Exists(Path.Combine(GetPatientDocumentFolder(), patient.DocumentFileName));

    var currentTherapy = GetCurrentTherapy(id);
    var currentTherapyInfo = currentTherapy != null ? GetCurrentTherapyInfo(id) : null;

    var today = DateOnly.FromDateTime(DateTime.Today);
    var typeCache = GetTypeCache();
    var partCache = GetPartCache();
    var therapistNameCache = GetTherapistNameCache();

    // Every part belonging to the current therapy (if any) - the patient may
    // have older (Completed) therapies too, but "next"/"last" slots should only
    // ever reflect the current one in practice, so scoping here avoids pulling
    // in history from long-finished plans.
    var partIds = currentTherapy == null
      ? new List<int>()
      : _db.TherapyParts.Where(p => p.TherapyId == currentTherapy.Id).Select(p => p.Id).ToList();

    List<PatientStatusSlotDto> BuildSlotDtos(IEnumerable<TherapySlot> slots) {
      return slots.Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

        return new PatientStatusSlotDto {
          Date = s.Date,
          TimeSlot = s.TimeSlot,
          TherapyTypeName = type != null ? type.Name : "?",
          TherapistName = s.TherapistId.HasValue && therapistNameCache.ContainsKey(s.TherapistId.Value)
            ? therapistNameCache[s.TherapistId.Value]
            : "Reparto",
          Status = s.Status,
          StatusLabel = TherapySlotStatus.ToLabel(s.Status)
        };
      }).ToList();
    }

    var nextSlots = new List<PatientStatusSlotDto>();
    var lastPastSlot = (PatientStatusSlotDto?)null;

    if (partIds.Count > 0) {
      var upcoming = _db.TherapySlots
        .Where(s => partIds.Contains(s.TherapyPartId) && s.Date >= today && s.Status == TherapySlotStatus.ToBeDone)
        .OrderBy(s => s.Date)
        .ThenBy(s => s.TimeSlot)
        .Take(3)
        .ToList();
      nextSlots = BuildSlotDtos(upcoming);

      var past = _db.TherapySlots
        .Where(s => partIds.Contains(s.TherapyPartId) && s.Date < today && s.Status != TherapySlotStatus.Rescheduled)
        .OrderByDescending(s => s.Date)
        .ThenByDescending(s => s.TimeSlot)
        .FirstOrDefault();
      lastPastSlot = past != null ? BuildSlotDtos(new[] { past }).First() : null;
    }

    return Ok(new {
      patientName = patient.Name,
      patientPhone = patient.Phone,
      hasDocument,
      currentTherapy = currentTherapyInfo,
      nextSlots,
      lastPastSlot
    });
  }

  [HttpGet("/Patients/Document/{id}")]
  public IActionResult PatientsDocument(int id) {
    var patient = _db.Patients.Find(id);

    if (patient == null || string.IsNullOrEmpty(patient.DocumentFileName)) {
      return NotFound();
    }

    var fullPath = Path.Combine(GetPatientDocumentFolder(), patient.DocumentFileName);

    if (!System.IO.File.Exists(fullPath)) {
      return NotFound();
    }

    return PhysicalFile(fullPath, "application/pdf");
  }
}
