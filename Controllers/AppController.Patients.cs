using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

  [HttpGet("/Patients/List")]
  public IActionResult PatientsList(string? filter, int page = 1, string sortBy = "created", string sortDir = "desc") {
    var query = _db.Patients.AsQueryable();

    if (!string.IsNullOrEmpty(filter) && filter.Length >= 3) {
      query = query.Where(p => p.Name.Contains(filter) || p.Phone.Contains(filter));
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

  // "Current" = not yet Completed/Cancelled. Only one such Therapy can exist per
  // patient at a time (enforced on Create), so the most recent one is unambiguous.
  private object? GetCurrentTherapyInfo(int patientId) {
    var therapy = _db.Therapies
      .Where(t => t.PatientId == patientId && t.Status != TherapyStatus.Completed && t.Status != TherapyStatus.Cancelled)
      .OrderByDescending(t => t.Id)
      .FirstOrDefault();

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
          placedCount,
          remaining = p.SessionCount - placedCount
        };
      });

    return new {
      therapy.Id,
      therapy.Name,
      therapy.Status,
      statusLabel = TherapyStatus.ToLabel(therapy.Status),
      parts
    };
  }

  private class PatientPlanSlotDto {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
    public string TherapyTypeName { get; set; } = "?";
    public string TherapistName { get; set; } = "Reparto";
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

        return new PatientPlanSlotDto {
          Date = s.Date,
          TimeSlot = s.TimeSlot,
          TherapyTypeName = type != null ? type.Name : "?",
          TherapistName = s.TherapistId.HasValue && therapistNameCache.ContainsKey(s.TherapistId.Value)
            ? therapistNameCache[s.TherapistId.Value]
            : "Reparto"
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
