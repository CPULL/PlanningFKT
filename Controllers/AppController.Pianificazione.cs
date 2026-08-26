using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // --- Shared helpers --------------------------------------------------------

  private static int GetSpanSlots(TherapyType type) {
    return (int)Math.Ceiling(type.Duration / 15.0);
  }

  private int GetPlacedCount(int partId) {
    return _db.TherapySlots.Count(s => s.TherapyPartId == partId && s.Status != TherapySlotStatus.Rescheduled);
  }

  private TherapySlot BuildTherapySlot(int partId, DateOnly date, int timeSlot, int? therapistId) {
    return new TherapySlot {
      TherapyPartId = partId,
      Date = date,
      TimeSlot = timeSlot,
      TherapistId = therapistId,
      Status = TherapySlotStatus.ToBeDone
    };
  }

  // Palestra-only/Mixed dropdown filter (excludes only pure Reparto) - shared by
  // the Palestra-only flow's own TherapistsFor endpoint and the Mixed flow.
  private List<Therapist> GetPalestraDropdownTherapists() {
    return _db.Therapists
      .Where(t =>
        t.IsActive == 1 &&
        (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0 &&
        t.OperatingArea != TherapistOperatingArea.Reparto)
      .OrderBy(t => t.Name)
      .ToList();
  }

  // "Straordinari consentiti": a therapist with OvertimeAllowed can be scheduled
  // up to 4 slots (1 hour) before their normal start or after their normal end -
  // not unlimited, just enough to cover a short overrun (CPU's call). Cached per
  // request like availability itself, to avoid a per-slot Therapists lookup.
  private bool IsWithinAvailability(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var dayOfWeek = (int)date.DayOfWeek; // Sunday=0...Saturday=6, matches our DayOfWeek convention
    var newEnd = timeSlot + spanSlots;

    _availabilityByTherapistDayCache ??= new Dictionary<(int, int), List<TherapistAvailability>>();
    var key = (therapistId, dayOfWeek);

    if (!_availabilityByTherapistDayCache.TryGetValue(key, out var availability)) {
      availability = _db.TherapistAvailabilities
        .Where(a => a.TherapistId == therapistId && a.DayOfWeek == dayOfWeek)
        .ToList();
      _availabilityByTherapistDayCache[key] = availability;
    }

    _overtimeAllowedCache ??= new Dictionary<int, bool>();
    if (!_overtimeAllowedCache.TryGetValue(therapistId, out var overtimeAllowed)) {
      overtimeAllowed = _db.Therapists.Where(t => t.Id == therapistId).Select(t => t.OvertimeAllowed).FirstOrDefault() == 1;
      _overtimeAllowedCache[therapistId] = overtimeAllowed;
    }

    const int overtimeSlots = 4;
    var margin = overtimeAllowed ? overtimeSlots : 0;

    return availability.Any(a => timeSlot >= a.StartTime - margin && newEnd <= a.EndTime + margin);
  }

  // Mirrors the frontend's vacationCoverage split-at-13:00 logic, server-side.
  private bool IsBlockedByVacation(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var noonSlot = 52; // 13:00 in 15-min slots from midnight
    var newEnd = timeSlot + spanSlots;

    _vacationsByTherapistCache ??= new Dictionary<int, List<Vacation>>();

    if (!_vacationsByTherapistCache.TryGetValue(therapistId, out var vacations)) {
      vacations = _db.Vacations.Where(v => v.TherapistId == therapistId || v.TherapistId == null).ToList();
      _vacationsByTherapistCache[therapistId] = vacations;
    }

    foreach (var v in vacations) {
      bool matches;

      if (v.IsYearIndependent == 1 && v.Month.HasValue && v.Day.HasValue) {
        matches = date.Month == v.Month.Value && date.Day == v.Day.Value;
      } else if (v.StartDate.HasValue && v.EndDate.HasValue) {
        matches = date >= v.StartDate.Value && date <= v.EndDate.Value;
      } else {
        matches = false;
      }

      if (!matches) {
        continue;
      }

      var isSingleDay = v.StartDate.HasValue && v.EndDate.HasValue && v.StartDate.Value == v.EndDate.Value;
      var coverStart = 0;
      var coverEnd = 96;

      if (isSingleDay && v.AMPM == 0) {
        coverEnd = noonSlot;
      } else if (isSingleDay && v.AMPM == 1) {
        coverStart = noonSlot;
      }

      if (timeSlot < coverEnd && newEnd > coverStart) {
        return true;
      }
    }

    return false;
  }

  private bool HasTherapistConflict(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var newEnd = timeSlot + spanSlots;

    var existingSlots = _db.TherapySlots
      .Where(s => s.TherapistId == therapistId && s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    foreach (var slot in existingSlots) {
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
      var existingSpan = type != null ? GetSpanSlots(type) : 1;
      var existingEnd = slot.TimeSlot + existingSpan;

      if (timeSlot < existingEnd && newEnd > slot.TimeSlot) {
        return true;
      }
    }

    return false;
  }

  private bool HasPatientConflict(int patientId, DateOnly date, int timeSlot, int spanSlots, int therapyTypeId) {
    var newEnd = timeSlot + spanSlots;

    var partIds = _db.Therapies
      .Where(t => t.PatientId == patientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id)
      .ToList();

    var existingSlots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    foreach (var slot in existingSlots) {
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
      var existingSpan = type != null ? GetSpanSlots(type) : 1;
      var existingEnd = slot.TimeSlot + existingSpan;

      // Same TherapyType twice in one day is a conflict even if the times don't
      // overlap at all (spec rule: "a patient cannot do the same TherapyType twice
      // in one day") - checked in addition to the plain time-range overlap below.
      if (part != null && part.TherapyTypeId == therapyTypeId) {
        return true;
      }

      if (timeSlot < existingEnd && newEnd > slot.TimeSlot) {
        return true;
      }
    }

    return false;
  }

  // --- Pianificazione ----------------------------------------------------

  [HttpGet("/Pianificazione/Info/{partId}")]
  public IActionResult PianificazioneInfo(int partId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var part = _db.TherapyParts.Find(partId);

    if (part == null) {
      return NotFound();
    }

    var therapyType = _db.TherapyTypes.Find(part.TherapyTypeId);
    var therapy = _db.Therapies.Find(part.TherapyId);

    if (therapyType == null || therapy == null) {
      return NotFound();
    }

    var patient = _db.Patients.Find(therapy.PatientId);

    if (patient == null) {
      return NotFound();
    }

    var placedCount = GetPlacedCount(part.Id);

    return Ok(new {
      partId = part.Id,
      therapyId = therapy.Id,
      patientId = patient.Id,
      patientName = patient.Name,
      therapyTypeId = therapyType.Id,
      therapyTypeName = therapyType.Name,
      duration = therapyType.Duration,
      sessionCount = part.SessionCount,
      placedCount,
      remaining = part.SessionCount - placedCount
    });
  }

  [HttpGet("/Pianificazione/TherapistsFor/{partId}")]
  public IActionResult PianificazioneTherapistsFor(int partId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    // Scoped to Palestra-only therapies for now (single-part Palestra is all this
    // build supports) - excludes only pure Reparto. The Reparto-side filter will be
    // added when that case is built.
    var rows = GetPalestraDropdownTherapists()
      .Select(t => new { t.Id, t.Name })
      .ToList();

    return Ok(rows);
  }

  [HttpGet("/Pianificazione/WeekData")]
  public IActionResult PianificazioneWeekData(int therapistId, int partId, DateOnly weekStart) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var part = _db.TherapyParts.Find(partId);

    if (part == null) {
      return NotFound();
    }

    var therapy = _db.Therapies.Find(part.TherapyId);

    if (therapy == null) {
      return NotFound();
    }

    var weekEnd = weekStart.AddDays(4);

    var availability = _db.TherapistAvailabilities
      .Where(a => a.TherapistId == therapistId)
      .Select(a => new { a.DayOfWeek, a.StartTime, a.EndTime })
      .ToList();

    var vacations = _db.Vacations
      .Where(v => v.TherapistId == therapistId || v.TherapistId == null)
      .Select(v => new { v.TherapistId, v.Name, v.AMPM, v.IsYearIndependent, v.Month, v.Day, v.StartDate, v.EndDate })
      .ToList();

    var patientPartIds = _db.Therapies
      .Where(t => t.PatientId == therapy.PatientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id)
      .ToList();

    var therapistSlotIds = _db.TherapySlots
      .Where(s => s.TherapistId == therapistId && s.Date >= weekStart && s.Date <= weekEnd && s.Status != TherapySlotStatus.Rescheduled)
      .Select(s => s.Id);

    var patientSlotIds = _db.TherapySlots
      .Where(s => patientPartIds.Contains(s.TherapyPartId) && s.Date >= weekStart && s.Date <= weekEnd && s.Status != TherapySlotStatus.Rescheduled)
      .Select(s => s.Id);

    var allSlotIds = therapistSlotIds.Union(patientSlotIds).Distinct().ToList();
    var allSlots = _db.TherapySlots.Where(s => allSlotIds.Contains(s.Id)).ToList();

    var partsCache = _db.TherapyParts.ToDictionary(p => p.Id, p => p);
    var typesCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);
    var therapiesCache = _db.Therapies.ToDictionary(t => t.Id, t => t);
    var patientsCache = _db.Patients.ToDictionary(p => p.Id, p => p.Name);
    var therapistsCache = _db.Therapists.ToDictionary(t => t.Id, t => t.Name);

    var slots = allSlots.Select(s => {
      var slotPart = partsCache.ContainsKey(s.TherapyPartId) ? partsCache[s.TherapyPartId] : null;
      var slotType = slotPart != null && typesCache.ContainsKey(slotPart.TherapyTypeId) ? typesCache[slotPart.TherapyTypeId] : null;
      var slotTherapy = slotPart != null && therapiesCache.ContainsKey(slotPart.TherapyId) ? therapiesCache[slotPart.TherapyId] : null;
      var isCurrentPatient = slotTherapy != null && slotTherapy.PatientId == therapy.PatientId;

      return new {
        s.Id,
        s.Date,
        s.TimeSlot,
        durationSlots = slotType != null ? GetSpanSlots(slotType) : 1,
        s.TherapistId,
        therapistName = s.TherapistId.HasValue && therapistsCache.ContainsKey(s.TherapistId.Value)
          ? therapistsCache[s.TherapistId.Value]
          : "Reparto",
        therapyTypeId = slotPart != null ? slotPart.TherapyTypeId : (int?)null,
        therapyTypeName = slotType != null ? slotType.Name : "?",
        patientName = slotTherapy != null && patientsCache.ContainsKey(slotTherapy.PatientId)
          ? patientsCache[slotTherapy.PatientId]
          : "?",
        isCurrentPatient
      };
    });

    return Ok(new { availability, vacations, slots });
  }

  public class AutoFillRequest {
    public int TherapyPartId { get; set; }
    public int TherapistId { get; set; }
    // "singolo" | "giorniAlterni" | "settimanalmente" | "tuttiIGiorni"
    public string Mode { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public int TimeSlot { get; set; }
    // How many placements to compute. 0 (or omitted) means "however many are still
    // needed overall" (Rimpiazza checked / full replace); a positive value requests
    // just that many additional ones (Rimpiazza unchecked / append).
    public int Count { get; set; }
  }

  // Generates candidate dates for the 3 automatic modes. Bounded (not an infinite
  // generator) as a safety valve against a therapist with no matching availability.
  private List<DateOnly> GenerateCandidateDates(string mode, DateOnly startDate, int maxCandidates) {
    var candidates = new List<DateOnly>();
    var date = startDate;

    if (mode == "giorniAlterni") {
      var startDow = (int)startDate.DayOfWeek;
      var track = (startDow == 2 || startDow == 4) ? new HashSet<int> { 2, 4 } : new HashSet<int> { 1, 3, 5 };

      while (candidates.Count < maxCandidates) {
        if (track.Contains((int)date.DayOfWeek)) {
          candidates.Add(date);
        }
        date = date.AddDays(1);
      }
    } else if (mode == "settimanalmente") {
      while (candidates.Count < maxCandidates) {
        candidates.Add(date);
        date = date.AddDays(7);
      }
    } else {
      // tuttiIGiorni: every next working day
      while (candidates.Count < maxCandidates) {
        var dow = (int)date.DayOfWeek;
        if (dow >= 1 && dow <= 5) {
          candidates.Add(date);
        }
        date = date.AddDays(1);
      }
    }

    return candidates;
  }

  [HttpPost("/Pianificazione/AutoFill")]
  public IActionResult PianificazioneAutoFill([FromBody] AutoFillRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var part = _db.TherapyParts.Find(request.TherapyPartId);

    if (part == null) {
      return NotFound();
    }

    var therapyType = _db.TherapyTypes.Find(part.TherapyTypeId);
    var therapy = _db.Therapies.Find(part.TherapyId);

    if (therapyType == null || therapy == null) {
      return NotFound();
    }

    var placedCount = GetPlacedCount(part.Id);
    var trueRemaining = part.SessionCount - placedCount;

    if (trueRemaining <= 0) {
      return BadRequest(new { message = "Tutte le sedute di questa parte sono già pianificate." });
    }

    var targetCount = request.Count > 0 ? Math.Min(request.Count, trueRemaining) : trueRemaining;

    var spanSlots = GetSpanSlots(therapyType);
    var maxCandidates = Math.Min(500, targetCount * 20 + 50);
    var candidates = GenerateCandidateDates(request.Mode, request.StartDate, maxCandidates);

    var results = new List<object>();

    foreach (var date in candidates) {
      if (results.Count >= targetCount) {
        break;
      }

      // Vacation/unavailable days are silently passed over - they don't count
      // toward the session total at all (CONFIRMED behavior, applies to all modes).
      if (IsBlockedByVacation(request.TherapistId, date, request.TimeSlot, spanSlots) ||
          !IsWithinAvailability(request.TherapistId, date, request.TimeSlot, spanSlots)) {
        continue;
      }

      // A scheduling conflict (therapist or patient already occupied) IS placed
      // and counted, just flagged - it does not get skipped (CONFIRMED).
      var conflict = HasTherapistConflict(request.TherapistId, date, request.TimeSlot, spanSlots) ||
                     HasPatientConflict(therapy.PatientId, date, request.TimeSlot, spanSlots, part.TherapyTypeId);

      results.Add(new { date, timeSlot = request.TimeSlot, conflict });
    }

    if (results.Count < targetCount) {
      return BadRequest(new { message = "Non è stato possibile trovare abbastanza date valide in un intervallo ragionevole." });
    }

    return Ok(results);
  }

  public class AcceptPlacement {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
  }

  public class AcceptRequest {
    public int TherapyPartId { get; set; }
    public int TherapistId { get; set; }
    public List<AcceptPlacement> Placements { get; set; } = new();
  }

  [HttpPost("/Pianificazione/Accept")]
  public IActionResult PianificazioneAccept([FromBody] AcceptRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var part = _db.TherapyParts.Find(request.TherapyPartId);

    if (part == null) {
      return NotFound();
    }

    var therapyType = _db.TherapyTypes.Find(part.TherapyTypeId);
    var therapy = _db.Therapies.Find(part.TherapyId);

    if (therapyType == null || therapy == null) {
      return NotFound();
    }

    var spanSlots = GetSpanSlots(therapyType);

    var placedCount = GetPlacedCount(part.Id);
    var remaining = part.SessionCount - placedCount;

    if (request.Placements.Count != remaining) {
      return BadRequest(new { message = "Il numero di sedute pianificate non corrisponde a quelle richieste." });
    }

    // Never trust client-side conflict flags - re-validate every placement from
    // scratch against real, current data before committing anything.
    foreach (var p in request.Placements) {
      if (IsBlockedByVacation(request.TherapistId, p.Date, p.TimeSlot, spanSlots) ||
          !IsWithinAvailability(request.TherapistId, p.Date, p.TimeSlot, spanSlots) ||
          HasTherapistConflict(request.TherapistId, p.Date, p.TimeSlot, spanSlots) ||
          HasPatientConflict(therapy.PatientId, p.Date, p.TimeSlot, spanSlots, part.TherapyTypeId)) {
        return BadRequest(new { message = "Alcuni slot non sono più validi. Ricontrolla la pianificazione." });
      }
    }

    foreach (var p in request.Placements) {
      _db.TherapySlots.Add(BuildTherapySlot(part.Id, p.Date, p.TimeSlot, request.TherapistId));
    }

    therapy.Status = TherapyStatus.Scheduled;
    _db.SaveChanges();

    return Ok();
  }
}
