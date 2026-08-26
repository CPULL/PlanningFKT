using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // --- Mixed (Heavy + Light) Pianificazione -----------------------------------
  //
  // "Heavy" = Palestra Active and/or Reparto Active parts (any count, any mix of
  // the two categories). "Light" = Reparto Light parts. One placement action
  // places every still-needed Heavy part back-to-back (summed duration), then
  // auto-places every still-needed Light part adjacent to that block (before or
  // after, whichever side has room and fewer existing sessions).

  private static bool IsHeavyPart(TherapyType type) {
    return type.Category == TherapyCategory.Palestra || (type.Category == TherapyCategory.Reparto && type.Type == TherapyExecutionType.Active);
  }

  private static bool IsLightPart(TherapyType type) {
    return type.Category == TherapyCategory.Reparto && type.Type == TherapyExecutionType.Light;
  }

  // Pure-Reparto-capable therapists first (they use the faster RepartoTherapyStartingTime
  // rate and are the qualification-preferred choice), then "aiuto" helpers - matches the
  // preference order for both the dropdown display and the auto-pick search.
  private List<Therapist> GetOrderedRepartoTherapists() {
    return GetRepartoCapableAndCoveringTherapists()
      .OrderByDescending(t => (t.OperatingArea & TherapistOperatingArea.Reparto) != 0)
      .ThenBy(t => t.Name)
      .ToList();
  }

  private int CountAllRepartoSessions(DateOnly date, int slot) {
    var slotsOnDate = _db.TherapySlots.Where(s => s.Date == date && s.Status != TherapySlotStatus.Rescheduled).ToList();
    var count = 0;

    foreach (var s in slotsOnDate) {
      var part = _db.TherapyParts.Find(s.TherapyPartId);
      var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

      if (type == null || type.Category != TherapyCategory.Reparto) {
        continue;
      }

      var span = GetSpanSlots(type);
      if (slot >= s.TimeSlot && slot < s.TimeSlot + span) {
        count++;
      }
    }

    return count;
  }

  private bool IsTherapistFreeForSpan(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    return !IsBlockedByVacation(therapistId, date, timeSlot, spanSlots) &&
           IsWithinAvailability(therapistId, date, timeSlot, spanSlots) &&
           !HasTherapistConflict(therapistId, date, timeSlot, spanSlots);
  }

  [HttpGet("/Pianificazione/MixedInfo/{therapyId}")]
  public IActionResult PianificazioneMixedInfo(int therapyId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(therapyId);

    if (therapy == null) {
      return NotFound();
    }

    var patient = _db.Patients.Find(therapy.PatientId);

    if (patient == null) {
      return NotFound();
    }

    var typeCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);

    var partsRaw = _db.TherapyParts.Where(p => p.TherapyId == therapyId).OrderBy(p => p.Id).ToList();

    var partInfos = partsRaw.Select(p => {
      var type = typeCache.ContainsKey(p.TherapyTypeId) ? typeCache[p.TherapyTypeId] : null;
      var placedCount = GetPlacedCount(p.Id);

      return new {
        p.Id,
        p.TherapyTypeId,
        therapyTypeName = type != null ? type.Name : "?",
        category = type != null ? type.Category : -1,
        executionType = type != null ? type.Type : -1,
        duration = type != null ? type.Duration : 15,
        p.SessionCount,
        placedCount,
        remaining = p.SessionCount - placedCount,
        isHeavy = type != null && IsHeavyPart(type),
        isLight = type != null && IsLightPart(type)
      };
    }).ToList();

    var heavyParts = partInfos.Where(p => p.isHeavy).ToList();
    var lightParts = partInfos.Where(p => p.isLight).ToList();

    return Ok(new {
      therapyId = therapy.Id,
      patientId = patient.Id,
      patientName = patient.Name,
      heavyParts,
      lightParts,
      hasPalestraHeavy = heavyParts.Any(p => p.category == TherapyCategory.Palestra),
      hasRepartoHeavy = heavyParts.Any(p => p.category == TherapyCategory.Reparto)
    });
  }

  [HttpGet("/Pianificazione/MixedTherapistsFor/{therapyId}")]
  public IActionResult PianificazioneMixedTherapistsFor(int therapyId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var palestraTherapists = GetPalestraDropdownTherapists().Select(t => new { t.Id, t.Name }).ToList();

    var repartoTherapists = GetOrderedRepartoTherapists().Select(t => new {
      t.Id,
      t.Name,
      isHelper = (t.OperatingArea & TherapistOperatingArea.Reparto) == 0
    }).ToList();

    return Ok(new { palestraTherapists, repartoTherapists });
  }

  [HttpGet("/Pianificazione/MixedWeekData")]
  public IActionResult PianificazioneMixedWeekData(int therapyId, DateOnly weekStart, int? palestraTherapistId, int? repartoTherapistId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(therapyId);

    if (therapy == null) {
      return NotFound();
    }

    var weekEnd = weekStart.AddDays(4);

    var dayStart = GetClinicHoursStart();
    var dayEnd = GetClinicHoursEnd();

    var (repartoRate, coveringRate) = GetRepartoRates();
    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();
    var capacityWarningThreshold = GetRepartoCapacityWarningThreshold();

    var capacityGrid = new List<object>();
    for (var d = weekStart; d <= weekEnd; d = d.AddDays(1)) {
      for (var slot = dayStart; slot < dayEnd; slot++) {
        var (capacity, demand) = ComputeRepartoCapacityAndDemand(d, slot, repartoRate, coveringRate, relevantTherapists);
        capacityGrid.Add(new { date = d, timeSlot = slot, capacity, demand });
      }
    }

    var palestraAvailability = new List<object>();
    if (palestraTherapistId.HasValue) {
      palestraAvailability = _db.TherapistAvailabilities.Where(a => a.TherapistId == palestraTherapistId.Value)
        .Select(a => new { a.DayOfWeek, a.StartTime, a.EndTime })
        .ToList()
        .Select(a => (object)a)
        .ToList();
    }

    var vacations = _db.Vacations
      .Select(v => new { v.TherapistId, v.Name, v.AMPM, v.IsYearIndependent, v.Month, v.Day, v.StartDate, v.EndDate })
      .ToList();

    var typeCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);
    var partCache = _db.TherapyParts.ToDictionary(p => p.Id, p => p);
    var therapyCache = _db.Therapies.ToDictionary(t => t.Id, t => t);
    var patientCache = _db.Patients.ToDictionary(p => p.Id, p => p.Name);
    var therapistCache = _db.Therapists.ToDictionary(t => t.Id, t => t.Name);

    var weekSlotsRaw = _db.TherapySlots
      .Where(s => s.Date >= weekStart && s.Date <= weekEnd && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    var patientPartIds = _db.Therapies.Where(t => t.PatientId == therapy.PatientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id).ToList();

    // Relevant slots: this patient's own (any part, any therapist - cross-therapist
    // visibility), the selected Palestra therapist's own, the selected/explicit Reparto
    // therapist's own, and every Reparto-category slot (needed for the background count
    // badge and for drag re-validation of auto-picked Reparto-Active therapists).
    var relevantSlots = weekSlotsRaw.Where(s => {
      if (patientPartIds.Contains(s.TherapyPartId)) {
        return true;
      }
      if (palestraTherapistId.HasValue && s.TherapistId == palestraTherapistId.Value) {
        return true;
      }
      if (repartoTherapistId.HasValue && s.TherapistId == repartoTherapistId.Value) {
        return true;
      }
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      return type != null && type.Category == TherapyCategory.Reparto;
    });

    var slotsOutput = relevantSlots.Select(s => {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      var isCurrentPatient = slotTherapy != null && slotTherapy.PatientId == therapy.PatientId;

      return new {
        s.Id,
        s.Date,
        s.TimeSlot,
        durationSlots = type != null ? GetSpanSlots(type) : 1,
        s.TherapistId,
        therapistName = s.TherapistId.HasValue && therapistCache.ContainsKey(s.TherapistId.Value)
          ? therapistCache[s.TherapistId.Value]
          : "Reparto",
        therapyTypeId = part != null ? part.TherapyTypeId : (int?)null,
        therapyTypeName = type != null ? type.Name : "?",
        patientName = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : "?",
        isCurrentPatient
      };
    });

    // Palestra activities of "Reparto e aiuto a palestra" therapists - same background
    // info as the Reparto-only flow, relevant here too since Mixed also has a Reparto side.
    var repartoHelpingPalestraIds = GetRepartoHelpingPalestraTherapistIds();

    var palestraHelperSlots = weekSlotsRaw.Where(s => {
      if (!s.TherapistId.HasValue || !repartoHelpingPalestraIds.Contains(s.TherapistId.Value)) {
        return false;
      }
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      return type != null && type.Category == TherapyCategory.Palestra;
    }).Select(s => {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;

      return new {
        s.Id,
        s.Date,
        s.TimeSlot,
        durationSlots = type != null ? GetSpanSlots(type) : 1,
        therapyTypeName = type != null ? type.Name : "?",
        therapistId = s.TherapistId,
        therapistName = s.TherapistId.HasValue && therapistCache.ContainsKey(s.TherapistId.Value) ? therapistCache[s.TherapistId.Value] : "?",
        patientName = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : "?"
      };
    });

    return Ok(new { dayStart, dayEnd, capacityGrid, capacityWarningThreshold, palestraAvailability, vacations, slots = slotsOutput, palestraHelperSlots });
  }

  // Used when dragging a single already-placed Heavy segment to a new slot - re-checks
  // just that one therapist (fixed, whether manually picked or previously auto-picked)
  // rather than re-running the whole occurrence's placement/auto-pick logic.
  [HttpGet("/Pianificazione/MixedTherapistCheck")]
  public IActionResult PianificazioneMixedTherapistCheck(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    return Ok(new { ok = IsTherapistFreeForSpan(therapistId, date, timeSlot, spanSlots) });
  }

  private class HeavySegmentPlan {
    public TherapyPart Part = null!;
    public TherapyType Type = null!;
    public int Span;
    public int StartSlot;
    public int? TherapistId;
    public string TherapistName = "";
  }

  // Finds the best-fit assignment for the Reparto-Active heavy segments of one occurrence:
  // prefers one single therapist covering the whole combined span, falls back to picking
  // (possibly different) therapists per segment. Returns false if any segment is unfillable.
  private bool AssignRepartoActiveTherapists(List<HeavySegmentPlan> repartoActiveSegments, DateOnly date, int? forcedTherapistId, List<Therapist> orderedRepartoTherapists) {
    if (repartoActiveSegments.Count == 0) {
      return true;
    }

    if (forcedTherapistId.HasValue) {
      foreach (var seg in repartoActiveSegments) {
        if (!IsTherapistFreeForSpan(forcedTherapistId.Value, date, seg.StartSlot, seg.Span)) {
          return false;
        }
      }
      var forcedName = _db.Therapists.Find(forcedTherapistId.Value)?.Name ?? "?";
      foreach (var seg in repartoActiveSegments) {
        seg.TherapistId = forcedTherapistId.Value;
        seg.TherapistName = forcedName;
      }
      return true;
    }

    var combinedStart = repartoActiveSegments.Min(s => s.StartSlot);
    var combinedEnd = repartoActiveSegments.Max(s => s.StartSlot + s.Span);

    foreach (var t in orderedRepartoTherapists) {
      if (IsTherapistFreeForSpan(t.Id, date, combinedStart, combinedEnd - combinedStart)) {
        foreach (var seg in repartoActiveSegments) {
          seg.TherapistId = t.Id;
          seg.TherapistName = t.Name;
        }
        return true;
      }
    }

    // No single therapist covers the whole span - fall back to per-segment assignment.
    foreach (var seg in repartoActiveSegments) {
      var found = orderedRepartoTherapists.FirstOrDefault(t => IsTherapistFreeForSpan(t.Id, date, seg.StartSlot, seg.Span));
      if (found == null) {
        return false;
      }
      seg.TherapistId = found.Id;
      seg.TherapistName = found.Name;
    }

    return true;
  }

  public class MixedRemainingOverride {
    public int PartId { get; set; }
    public int Remaining { get; set; }
  }

  public class MixedAutoFillRequest {
    public int TherapyId { get; set; }
    public int? PalestraTherapistId { get; set; }
    // Optional - if set, forces this therapist for every Reparto-Active heavy segment;
    // if null, auto-picked (best-fit, see AssignRepartoActiveTherapists).
    public int? RepartoTherapistId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public int TimeSlot { get; set; }
    public int Count { get; set; }
    public List<MixedRemainingOverride>? RemainingOverride { get; set; }
  }

  [HttpPost("/Pianificazione/MixedAutoFill")]
  public IActionResult PianificazioneMixedAutoFill([FromBody] MixedAutoFillRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(request.TherapyId);

    if (therapy == null) {
      return NotFound();
    }

    var typeCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);
    var partsRaw = _db.TherapyParts.Where(p => p.TherapyId == request.TherapyId).OrderBy(p => p.Id).ToList();

    var heavyPartsState = partsRaw
      .Where(p => typeCache.ContainsKey(p.TherapyTypeId) && IsHeavyPart(typeCache[p.TherapyTypeId]))
      .Select(p => new { Part = p, Type = typeCache[p.TherapyTypeId], Span = GetSpanSlots(typeCache[p.TherapyTypeId]) })
      .ToList();

    var lightPartsState = partsRaw
      .Where(p => typeCache.ContainsKey(p.TherapyTypeId) && IsLightPart(typeCache[p.TherapyTypeId]))
      .Select(p => new { Part = p, Type = typeCache[p.TherapyTypeId], Span = GetSpanSlots(typeCache[p.TherapyTypeId]) })
      .ToList();

    if (heavyPartsState.Count == 0) {
      return BadRequest(new { message = "Nessuna parte pesante da pianificare." });
    }

    var needsPalestraTherapist = heavyPartsState.Any(p => p.Type.Category == TherapyCategory.Palestra);

    if (needsPalestraTherapist && !request.PalestraTherapistId.HasValue) {
      return BadRequest(new { message = "Seleziona un terapista per la Palestra." });
    }

    var remaining = partsRaw.ToDictionary(
      p => p.Id,
      p => p.SessionCount - GetPlacedCount(p.Id));

    if (request.RemainingOverride != null) {
      foreach (var ov in request.RemainingOverride) {
        if (remaining.ContainsKey(ov.PartId)) {
          remaining[ov.PartId] = ov.Remaining;
        }
      }
    }

    if (heavyPartsState.Sum(p => remaining[p.Part.Id]) <= 0) {
      return BadRequest(new { message = "Tutte le sedute pesanti sono già pianificate." });
    }

    var orderedRepartoTherapists = GetOrderedRepartoTherapists();
    var (repartoRate, coveringRate) = GetRepartoRates();
    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();

    var targetOccurrences = request.Count > 0 ? request.Count : int.MaxValue;
    var maxCandidates = Math.Min(1000, (request.Count > 0 ? request.Count : remaining.Values.Sum()) * 20 + 50);
    var candidates = GenerateCandidateDates(request.Mode, request.StartDate, maxCandidates);

    var results = new List<object>();
    var occurrencesPlaced = 0;

    foreach (var date in candidates) {
      if (heavyPartsState.Sum(p => remaining[p.Part.Id]) <= 0 || occurrencesPlaced >= targetOccurrences) {
        break;
      }

      var activeHeavy = heavyPartsState.Where(p => remaining[p.Part.Id] > 0).ToList();

      if (activeHeavy.Count == 0) {
        break;
      }

      // Build heavy segments back-to-back from the clicked start slot, in Part order.
      var heavySegments = new List<HeavySegmentPlan>();
      var cursor = request.TimeSlot;

      foreach (var p in activeHeavy) {
        heavySegments.Add(new HeavySegmentPlan { Part = p.Part, Type = p.Type, Span = p.Span, StartSlot = cursor });
        cursor += p.Span;
      }

      var heavyEndSlot = cursor;

      // Palestra segments: fixed, manually-chosen therapist, must be free for the whole day.
      var palestraSegments = heavySegments.Where(s => s.Type.Category == TherapyCategory.Palestra).ToList();
      var palestraOk = true;

      if (palestraSegments.Count > 0) {
        var palestraName = _db.Therapists.Find(request.PalestraTherapistId!.Value)?.Name ?? "?";
        foreach (var seg in palestraSegments) {
          if (!IsTherapistFreeForSpan(request.PalestraTherapistId!.Value, date, seg.StartSlot, seg.Span)) {
            palestraOk = false;
            break;
          }
          seg.TherapistId = request.PalestraTherapistId!.Value;
          seg.TherapistName = palestraName;
        }
      }

      if (!palestraOk) {
        continue; // silently skip this day, same as the Palestra-only flow
      }

      // Reparto-Active segments: best-fit auto-pick (or forced, if explicitly chosen).
      var repartoActiveSegments = heavySegments.Where(s => s.Type.Category == TherapyCategory.Reparto).ToList();

      if (!AssignRepartoActiveTherapists(repartoActiveSegments, date, request.RepartoTherapistId, orderedRepartoTherapists)) {
        continue; // no valid therapist assignment exists for this day - silently skip
      }

      // Light segments: auto-placed adjacent (before or after), stacked back-to-back.
      var activeLight = lightPartsState.Where(p => remaining[p.Part.Id] > 0).ToList();
      var lightSpanTotal = activeLight.Sum(p => p.Span);
      var lightSegments = new List<object>();

      if (lightSpanTotal > 0) {
        var windowBeforeStart = request.TimeSlot - lightSpanTotal;
        var windowBeforeValid = windowBeforeStart >= 0 &&
          Enumerable.Range(windowBeforeStart, lightSpanTotal).All(s => !IsRepartoSlotSaturated(date, s, repartoRate, coveringRate, relevantTherapists));

        var windowAfterStart = heavyEndSlot;
        var windowAfterValid = windowAfterStart + lightSpanTotal <= 96 &&
          Enumerable.Range(windowAfterStart, lightSpanTotal).All(s => !IsRepartoSlotSaturated(date, s, repartoRate, coveringRate, relevantTherapists));

        if (!windowBeforeValid && !windowAfterValid) {
          continue; // no room for the Light parts on either side - hard block, skip this day
        }

        int chosenStart;
        if (windowBeforeValid && windowAfterValid) {
          var beforeCount = Enumerable.Range(windowBeforeStart, lightSpanTotal).Sum(s => CountAllRepartoSessions(date, s));
          var afterCount = Enumerable.Range(windowAfterStart, lightSpanTotal).Sum(s => CountAllRepartoSessions(date, s));
          chosenStart = beforeCount < afterCount ? windowBeforeStart : windowAfterStart;
        } else {
          chosenStart = windowBeforeValid ? windowBeforeStart : windowAfterStart;
        }

        var lightCursor = chosenStart;
        foreach (var p in activeLight) {
          lightSegments.Add(new {
            partId = p.Part.Id,
            therapyTypeId = p.Part.TherapyTypeId,
            therapyTypeName = p.Type.Name,
            timeSlot = lightCursor,
            durationSlots = p.Span
          });
          lightCursor += p.Span;
        }
      }

      // Soft conflict (placed and flagged, not skipped): same patient already has an
      // overlapping session, or the same TherapyType already that day, on any segment.
      var conflict = heavySegments.Any(s => HasPatientConflict(therapy.PatientId, date, s.StartSlot, s.Span, s.Part.TherapyTypeId));

      if (!conflict && lightSpanTotal > 0) {
        var cursorSlot = ((dynamic)lightSegments[0]).timeSlot;
        foreach (var p in activeLight) {
          if (HasPatientConflict(therapy.PatientId, date, (int)cursorSlot, p.Span, p.Part.TherapyTypeId)) {
            conflict = true;
            break;
          }
          cursorSlot += p.Span;
        }
      }

      var heavySegmentsOutput = heavySegments.Select(s => new {
        partId = s.Part.Id,
        therapyTypeId = s.Part.TherapyTypeId,
        therapyTypeName = s.Type.Name,
        timeSlot = s.StartSlot,
        durationSlots = s.Span,
        therapistId = s.TherapistId,
        therapistName = s.TherapistName
      }).ToList();

      // TEMPORARY diagnostic - remove once the before/after picking issue is confirmed fixed.
      object? debug = null;
      if (lightSpanTotal > 0) {
        var dbgWindowBeforeStart = request.TimeSlot - lightSpanTotal;
        var dbgWindowBeforeValid = dbgWindowBeforeStart >= 0 &&
          Enumerable.Range(dbgWindowBeforeStart, lightSpanTotal).All(s => !IsRepartoSlotSaturated(date, s, repartoRate, coveringRate, relevantTherapists));
        var dbgWindowAfterStart = heavyEndSlot;
        var dbgWindowAfterValid = dbgWindowAfterStart + lightSpanTotal <= 96 &&
          Enumerable.Range(dbgWindowAfterStart, lightSpanTotal).All(s => !IsRepartoSlotSaturated(date, s, repartoRate, coveringRate, relevantTherapists));

        debug = new {
          windowBeforeStart = dbgWindowBeforeStart,
          windowBeforeValid = dbgWindowBeforeValid,
          windowBeforeSlotsDetail = Enumerable.Range(dbgWindowBeforeStart >= 0 ? dbgWindowBeforeStart : 0, dbgWindowBeforeStart >= 0 ? lightSpanTotal : 0)
            .Select(s => {
              var (cap, dem) = ComputeRepartoCapacityAndDemand(date, s, repartoRate, coveringRate, relevantTherapists);
              return new { slot = s, capacity = cap, demand = dem, existingCount = CountAllRepartoSessions(date, s) };
            }).ToList(),
          windowAfterStart = dbgWindowAfterStart,
          windowAfterValid = dbgWindowAfterValid,
          windowAfterSlotsDetail = Enumerable.Range(dbgWindowAfterStart, lightSpanTotal)
            .Select(s => {
              var (cap, dem) = ComputeRepartoCapacityAndDemand(date, s, repartoRate, coveringRate, relevantTherapists);
              return new { slot = s, capacity = cap, demand = dem, existingCount = CountAllRepartoSessions(date, s) };
            }).ToList()
        };
      }

      results.Add(new { date, heavySegments = heavySegmentsOutput, lightSegments, conflict, debug });

      foreach (var s in heavySegments) {
        remaining[s.Part.Id]--;
      }
      foreach (var p in activeLight) {
        remaining[p.Part.Id]--;
      }

      occurrencesPlaced++;
    }

    if (heavyPartsState.Sum(p => remaining[p.Part.Id]) > 0 && request.Count <= 0) {
      return BadRequest(new { message = "Non è stato possibile trovare abbastanza date valide in un intervallo ragionevole." });
    }

    return Ok(results);
  }

  public class MixedAcceptSegment {
    public int PartId { get; set; }
    public int TimeSlot { get; set; }
    public int? TherapistId { get; set; }
  }

  public class MixedAcceptPlacement {
    public DateOnly Date { get; set; }
    public List<MixedAcceptSegment> HeavySegments { get; set; } = new();
    public List<MixedAcceptSegment> LightSegments { get; set; } = new();
  }

  public class MixedAcceptRequest {
    public int TherapyId { get; set; }
    public List<MixedAcceptPlacement> Placements { get; set; } = new();
  }

  [HttpPost("/Pianificazione/MixedAccept")]
  public IActionResult PianificazioneMixedAccept([FromBody] MixedAcceptRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(request.TherapyId);

    if (therapy == null) {
      return NotFound();
    }

    var partsRaw = _db.TherapyParts.Where(p => p.TherapyId == request.TherapyId).ToDictionary(p => p.Id, p => p);
    var typeCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);

    var (repartoRate, coveringRate) = GetRepartoRates();
    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();

    // Never trust client-side conflict flags - re-validate every segment from scratch.
    foreach (var placement in request.Placements) {
      foreach (var seg in placement.HeavySegments) {
        if (!partsRaw.ContainsKey(seg.PartId)) {
          return BadRequest(new { message = "Parte non valida." });
        }

        var part = partsRaw[seg.PartId];
        var type = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var span = type != null ? GetSpanSlots(type) : 1;

        if (!seg.TherapistId.HasValue || !IsTherapistFreeForSpan(seg.TherapistId.Value, placement.Date, seg.TimeSlot, span)) {
          return BadRequest(new { message = "Alcuni slot pesanti non sono più validi. Ricontrolla la pianificazione." });
        }

        if (type != null && HasPatientConflict(therapy.PatientId, placement.Date, seg.TimeSlot, span, part.TherapyTypeId)) {
          return BadRequest(new { message = "Sono presenti conflitti non risolti. Ricontrolla la pianificazione." });
        }
      }

      foreach (var seg in placement.LightSegments) {
        if (!partsRaw.ContainsKey(seg.PartId)) {
          return BadRequest(new { message = "Parte non valida." });
        }

        var part = partsRaw[seg.PartId];
        var type = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var span = type != null ? GetSpanSlots(type) : 1;

        for (var slot = seg.TimeSlot; slot < seg.TimeSlot + span; slot++) {
          if (IsRepartoSlotSaturated(placement.Date, slot, repartoRate, coveringRate, relevantTherapists)) {
            return BadRequest(new { message = "Alcuni slot leggeri non sono più validi (capacità Reparto satura). Ricontrolla la pianificazione." });
          }
        }

        if (type != null && HasPatientConflict(therapy.PatientId, placement.Date, seg.TimeSlot, span, part.TherapyTypeId)) {
          return BadRequest(new { message = "Sono presenti conflitti non risolti. Ricontrolla la pianificazione." });
        }
      }
    }

    foreach (var placement in request.Placements) {
      foreach (var seg in placement.HeavySegments) {
        _db.TherapySlots.Add(BuildTherapySlot(seg.PartId, placement.Date, seg.TimeSlot, seg.TherapistId));
      }
      foreach (var seg in placement.LightSegments) {
        _db.TherapySlots.Add(BuildTherapySlot(seg.PartId, placement.Date, seg.TimeSlot, null));
      }
    }

    therapy.Status = TherapyStatus.Scheduled;
    _db.SaveChanges();

    return Ok();
  }
}
