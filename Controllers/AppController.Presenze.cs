using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // "Presenze" - a quick daily attendance page for therapists: mark who showed up
  // and who didn't. Palestra-only therapists see just their own assigned slots that
  // day; anyone with a Reparto side (pure Reparto, or "aiuto") also sees every
  // Reparto-category slot that day, since Reparto is a shared pool. There's also a
  // Reparto Uomini/Donne mode (no specific therapist) showing every Reparto slot
  // for that day split by patient sex, same idea as Giorno's own Reparto view.
  // Deliberately NOT Accettazione-gated - every therapist needs this for their own day.

  private class PresenzeSlotDto {
    public int Id { get; set; }
    public int TimeSlot { get; set; }
    public int DurationSlots { get; set; }
    public int? TherapyTypeId { get; set; }
    public string TherapyTypeLabel { get; set; } = "?"; // Abbreviazione, falls back to Name
    public int? TherapyTypeColor { get; set; }
    public string PatientName { get; set; } = "?";
    public int Status { get; set; }
    public string? ModUserName { get; set; }
    public DateTime? ModDate { get; set; }
    // Assigned to the therapist whose Presenze page this is (vs. a shared Reparto
    // pool session shown in one of their free/absent time slots).
    public bool IsOwn { get; set; }
    public int? PatientId { get; set; }
    // Patient has a "specialistica" PDF - Presenze shows a quick open button.
    public bool HasDocument { get; set; }
    // Moved Assente session (see TherapySlot.WasAbsent) - read-only here.
    public bool MovedAbsent { get; set; }
    public string? MovedToDate { get; set; }
  }

  private PresenzeSlotDto BuildPresenzeSlotDto(TherapySlot s, Dictionary<int, TherapyPart> partCache,
      Dictionary<int, TherapyType> typeCache, Dictionary<int, Therapy> therapyCache,
      Dictionary<int, Patient> patientCache, Dictionary<int, string> therapistNameCache) {
    var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
    var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
    var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
    var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId)
      ? patientCache[slotTherapy.PatientId]
      : null;
    var patientName = patient != null ? patient.Name : "?";
    var hasDocument = patient != null && !string.IsNullOrEmpty(patient.DocumentFileName)
      && System.IO.File.Exists(System.IO.Path.Combine(GetPatientDocumentFolder(), patient.DocumentFileName));

    return new PresenzeSlotDto {
      Id = s.Id,
      TimeSlot = s.TimeSlot,
      DurationSlots = GetSlotSpan(s, type),
      TherapyTypeId = part?.TherapyTypeId,
      TherapyTypeLabel = type != null ? (string.IsNullOrEmpty(type.Abbreviazione) ? type.Name : type.Abbreviazione) : "?",
      TherapyTypeColor = type?.Color,
      PatientName = patientName,
      Status = s.Status,
      ModUserName = s.ModUser.HasValue && therapistNameCache.ContainsKey(s.ModUser.Value) ? therapistNameCache[s.ModUser.Value] : null,
      ModDate = s.ModDate,
      PatientId = patient?.Id,
      HasDocument = hasDocument
    };
  }

  [HttpGet("/Presenze/TherapistData")]
  public IActionResult PresenzeTherapistData(int therapistId, DateOnly date) {
    var therapist = _db.Therapists.Find(therapistId);

    if (therapist == null || therapist.IsActive != 1 || (therapist.OperatingArea & TherapistOperatingArea.Accettazione) != 0) {
      return NotFound();
    }

    var availability = GetTherapistDayAvailability(therapistId, date);
    var hasAvailability = availability.Count > 0;
    var gridStart = hasAvailability ? availability.Min(a => a.StartTime) : 0;
    var gridEnd = hasAvailability ? availability.Max(a => a.EndTime) : 0;

    var isPurePalestra = therapist.OperatingArea == 0; // no Reparto bit, no HelpsOtherArea bit

    // Reparto slots (own or the shared pool) aren't bounded by this specific
    // therapist's own availability window - widen the grid to the clinic's full
    // hours whenever Reparto is in play, or a Reparto/other-therapist slot outside
    // this person's own hours would fall off the grid entirely and never render.
    if (!isPurePalestra) {
      var clinicStart = GetClinicHoursStart();
      var clinicEnd = GetClinicHoursEnd();
      gridStart = hasAvailability ? Math.Min(gridStart, clinicStart) : clinicStart;
      gridEnd = hasAvailability ? Math.Max(gridEnd, clinicEnd) : clinicEnd;
      hasAvailability = true;
    }

    var vacations = GetTherapistVacations(therapistId);

    var dates = new List<DateOnly> { date };
    var slotsInRange = GetSlotsInDateRange(dates).Where(s => s.Date == date).ToList();

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    TherapyType? TypeOf(TherapySlot s) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      return part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
    }

    // CPU's rule: a therapist sees ONLY their own sessions - never sessions
    // assigned to someone else (Palestra or Reparto). The shared Reparto pool
    // (Reparto sessions with no therapist assigned) is shown only to therapists
    // with a Reparto side, and only in time slots where they're free: no own
    // session there, or their own patient there is marked Absent.
    var ownSlots = slotsInRange.Where(s => s.TherapistId == therapistId).ToList();

    var busyTimeSlots = new HashSet<int>();
    foreach (var s in ownSlots) {
      if (s.Status == TherapySlotStatus.PatientAbsent) {
        continue;
      }
      var type = TypeOf(s);
      var span = GetSlotSpan(s, type);
      for (var t = s.TimeSlot; t < s.TimeSlot + span; t++) {
        busyTimeSlots.Add(t);
      }
    }

    var poolSlots = isPurePalestra
      ? new List<TherapySlot>()
      : slotsInRange
        .Where(s => {
          var type = TypeOf(s);
          return !s.TherapistId.HasValue
            && type != null && type.Category == TherapyCategory.Reparto
            && !busyTimeSlots.Contains(s.TimeSlot);
        })
        .ToList();

    var slots = ownSlots
      .Select(s => {
        var dto = BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache);
        dto.IsOwn = true;
        return dto;
      })
      .Concat(poolSlots.Select(s => BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache)))
      .OrderBy(d => d.TimeSlot)
      .ToList();

    // Moved Assente sessions of this therapist (read-only, never block anything).
    var moved = GetMovedAbsentSlotsInDateRange(dates).Where(s => s.TherapistId == therapistId).ToList();
    var movedTo = GetMovedToDates(moved);
    slots.AddRange(moved.Select(s => {
      var dto = BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache);
      dto.IsOwn = true;
      dto.MovedAbsent = true;
      dto.MovedToDate = movedTo.GetValueOrDefault(s.Id);
      return dto;
    }));

    var genericRepartoTimeSlots = _db.GenericRepartoWorks
      .Where(g => g.TherapistId == therapistId && g.Date == date)
      .Select(g => g.TimeSlot)
      .ToList();

    return Ok(new {
      requestDate = date.ToString("yyyy-MM-dd"),
      requestTherapistId = therapistId,
      name = therapist.Name,
      hasAvailability,
      gridStart,
      gridEnd,
      availability,
      vacations,
      slots,
      genericRepartoTimeSlots
    });
  }

  [HttpGet("/Presenze/RepartoData")]
  public IActionResult PresenzeRepartoData(int sex, DateOnly date) {
    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();
    var vacations = GetGlobalVacations();

    var dates = new List<DateOnly> { date };
    var slotsInRange = GetSlotsInDateRange(dates).Where(s => s.Date == date).ToList();

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    var slots = slotsInRange
      .Where(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : null;

        return type != null && type.Category == TherapyCategory.Reparto && patient != null && patient.Sex == sex;
      })
      .Select(s => BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache))
      .ToList();

    // Moved Assente Reparto sessions of this sex (read-only).
    var moved = GetMovedAbsentSlotsInDateRange(dates).Where(s => {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : null;
      return type != null && type.Category == TherapyCategory.Reparto && patient != null && patient.Sex == sex;
    }).ToList();
    var movedTo = GetMovedToDates(moved);
    slots.AddRange(moved.Select(s => {
      var dto = BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache);
      dto.MovedAbsent = true;
      dto.MovedToDate = movedTo.GetValueOrDefault(s.Id);
      return dto;
    }));

    return Ok(new {
      requestDate = date.ToString("yyyy-MM-dd"),
      requestSex = sex,
      name = sex == PatientSex.Female ? "Reparto Donne" : "Reparto Uomini",
      hasAvailability = true,
      gridStart = clinicStart,
      gridEnd = clinicEnd,
      availability = new List<object>(),
      vacations,
      slots
    });
  }

  [HttpPost("/Presenze/Slot/{id}/CycleStatus")]
  public IActionResult PresenzeCycleStatus(int id) {
    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    // A moved session (incl. a moved Assente one) is read-only.
    if (slot.Status == TherapySlotStatus.Rescheduled) {
      return BadRequest(new { message = "Seduta spostata: non si può modificare." });
    }

    var wasDone = slot.Status == TherapySlotStatus.Done;

    if (slot.Status == TherapySlotStatus.ToBeDone) {
      slot.Status = TherapySlotStatus.Done;
    } else if (slot.Status == TherapySlotStatus.Done) {
      slot.Status = TherapySlotStatus.PatientAbsent;
    } else {
      slot.Status = TherapySlotStatus.ToBeDone;
    }

    slot.ModDate = DateTime.Now;
    slot.ModUser = GetCurrentTherapistId();

    _db.SaveChanges();
    RecomputeTherapyCompletion(slot.TherapyPartId, true, wasDone && slot.Status != TherapySlotStatus.Done);

    var therapistNameCache = GetTherapistNameCache();

    return Ok(new {
      status = slot.Status,
      modUserName = slot.ModUser.HasValue && therapistNameCache.ContainsKey(slot.ModUser.Value) ? therapistNameCache[slot.ModUser.Value] : null,
      modDate = slot.ModDate
    });
  }

  public class ToggleGenericRepartoRequest {
    public int TherapistId { get; set; }
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
  }

  // Spontaneous "I'm helping Reparto right now" marker (CPU's call) - clicking a
  // slot in Presenze toggles it. Only allowed when that slot is free or its
  // patient is marked Absent; blocked if a patient there is Done. No effect on
  // capacity or scheduling - purely logged for the monthly report.
  [HttpPost("/Presenze/ToggleGenericReparto")]
  public IActionResult ToggleGenericReparto([FromBody] ToggleGenericRepartoRequest request) {
    var currentTherapistId = GetCurrentTherapistId();

    if (currentTherapistId == null) {
      return Forbid();
    }

    // Any logged-in therapist can mark it for ANY therapist (CPU: one shared
    // tablet for ~20 people, nobody logs out/in between patients).

    // Blocked only when one of this therapist's OWN sessions covering this time
    // slot is Done (CPU's rule) - absent, to-be-done or other people's sessions
    // never block it.
    var typeCache = GetTypeCache();
    var partCache = GetPartCache();
    var hasDoneOwnSession = _db.TherapySlots
      .Where(s => s.TherapistId == request.TherapistId && s.Date == request.Date && s.Status == TherapySlotStatus.Done)
      .ToList()
      .Any(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var span = GetSlotSpan(s, type);
        return request.TimeSlot >= s.TimeSlot && request.TimeSlot < s.TimeSlot + span;
      });

    var existingMarker = _db.GenericRepartoWorks.FirstOrDefault(g =>
      g.TherapistId == request.TherapistId && g.Date == request.Date && g.TimeSlot == request.TimeSlot);

    // Removing an existing marker is always allowed; only adding a new one is blocked.
    if (hasDoneOwnSession && existingMarker == null) {
      return BadRequest(new { message = "Questo slot ha già una seduta completata." });
    }

    var existing = existingMarker;

    bool marked;
    if (existing != null) {
      _db.GenericRepartoWorks.Remove(existing);
      marked = false;
    } else {
      _db.GenericRepartoWorks.Add(new GenericRepartoWork {
        TherapistId = request.TherapistId,
        Date = request.Date,
        TimeSlot = request.TimeSlot
      });
      marked = true;
    }

    _db.SaveChanges();

    return Ok(new { marked });
  }

  private class GenericRepartoReportRowDto {
    public int TherapistId { get; set; }
    public string TherapistName { get; set; } = "?";
    public int PatientSlotsDone { get; set; }
    public int GenericRepartoSlots { get; set; }
  }

  // Monthly report (CPU's call): per therapist, total patient timeslots marked
  // Done and total generic-Reparto timeslots marked, for the given month.
  [HttpGet("/Presenze/GenericRepartoReport")]
  public IActionResult GenericRepartoReport(int year, int month) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var monthStart = new DateOnly(year, month, 1);
    var monthEnd = monthStart.AddMonths(1).AddDays(-1);

    var therapists = _db.Therapists.Where(t => t.IsActive == 1).ToList();

    var doneCountByTherapist = _db.TherapySlots
      .Where(s => s.TherapistId != null && s.Date >= monthStart && s.Date <= monthEnd && s.Status == TherapySlotStatus.Done)
      .GroupBy(s => s.TherapistId!.Value)
      .Select(g => new { TherapistId = g.Key, Count = g.Count() })
      .ToDictionary(x => x.TherapistId, x => x.Count);

    var genericCountByTherapist = _db.GenericRepartoWorks
      .Where(g => g.Date >= monthStart && g.Date <= monthEnd)
      .GroupBy(g => g.TherapistId)
      .Select(g => new { TherapistId = g.Key, Count = g.Count() })
      .ToDictionary(x => x.TherapistId, x => x.Count);

    var rows = therapists
      .Select(t => new GenericRepartoReportRowDto {
        TherapistId = t.Id,
        TherapistName = t.Name,
        PatientSlotsDone = doneCountByTherapist.ContainsKey(t.Id) ? doneCountByTherapist[t.Id] : 0,
        GenericRepartoSlots = genericCountByTherapist.ContainsKey(t.Id) ? genericCountByTherapist[t.Id] : 0
      })
      .Where(r => r.PatientSlotsDone > 0 || r.GenericRepartoSlots > 0)
      .OrderBy(r => r.TherapistName)
      .ToList();

    return Ok(rows);
  }
}
