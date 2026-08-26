using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // --- Shared Reparto capacity helpers ----------------------------------------

  private List<Therapist> GetRepartoCapableAndCoveringTherapists() {
    return _db.Therapists
      .Where(t =>
        t.IsActive == 1 &&
        (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0 &&
        ((t.OperatingArea & TherapistOperatingArea.Reparto) != 0 || (t.OperatingArea & TherapistOperatingArea.HelpsOtherArea) != 0))
      .ToList();
  }

  // "Reparto e aiuto a palestra" only (OperatingArea == 6) - Reparto-primary therapists
  // who might be pulled into a Palestra session, so their Palestra activities are
  // relevant background info when planning Reparto/Mixed.
  private List<int> GetRepartoHelpingPalestraTherapistIds() {
    return _db.Therapists
      .Where(t =>
        t.IsActive == 1 &&
        (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0 &&
        (t.OperatingArea & TherapistOperatingArea.Reparto) != 0 &&
        (t.OperatingArea & TherapistOperatingArea.HelpsOtherArea) != 0)
      .Select(t => t.Id)
      .ToList();
  }

  private (int repartoRate, int coveringRate) GetRepartoRates() {
    var repartoRate = _settingsCache.Get("RepartoTherapyStartingTime", 5);
    var coveringRate = _settingsCache.Get("PalestraCoveringRepartoStartingTime", 5);
    return (repartoRate <= 0 ? 5 : repartoRate, coveringRate <= 0 ? 5 : coveringRate);
  }

  // Percentage (0-100) of capacity occupied at which a Reparto slot is shown as
  // "near saturation" (yellow) instead of white - editable in Impostazioni.
  private int GetRepartoCapacityWarningThreshold() {
    var value = _settingsCache.Get("RepartoCapacityWarningThreshold", 75);
    return value <= 0 || value > 100 ? 75 : value;
  }

  private bool IsTherapistBusyWithActiveSession(int therapistId, DateOnly date, int timeSlot) {
    _therapistSlotsByDateCache ??= new Dictionary<(int, DateOnly), List<TherapySlot>>();
    var key = (therapistId, date);

    if (!_therapistSlotsByDateCache.TryGetValue(key, out var slots)) {
      slots = _db.TherapySlots
        .Where(s => s.TherapistId == therapistId && s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
        .ToList();
      _therapistSlotsByDateCache[key] = slots;
    }

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();

    foreach (var slot in slots) {
      var part = partCache.ContainsKey(slot.TherapyPartId) ? partCache[slot.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

      if (type == null || type.Type != TherapyExecutionType.Active) {
        continue;
      }

      var span = GetSpanSlots(type);
      if (timeSlot >= slot.TimeSlot && timeSlot < slot.TimeSlot + span) {
        return true;
      }
    }

    return false;
  }

  private int CountLightRepartoDemand(DateOnly date, int slot) {
    _allSlotsByDateCache ??= new Dictionary<DateOnly, List<TherapySlot>>();

    if (!_allSlotsByDateCache.TryGetValue(date, out var slotsOnDate)) {
      slotsOnDate = _db.TherapySlots.Where(s => s.Date == date && s.Status != TherapySlotStatus.Rescheduled).ToList();
      _allSlotsByDateCache[date] = slotsOnDate;
    }

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var count = 0;

    foreach (var s in slotsOnDate) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

      if (type == null || type.Category != TherapyCategory.Reparto || type.Type != TherapyExecutionType.Light) {
        continue;
      }

      var span = GetSpanSlots(type);
      if (slot >= s.TimeSlot && slot < s.TimeSlot + span) {
        count++;
      }
    }

    return count;
  }

  // Capacity = sum, across every free Reparto-capable/covering therapist, of how many
  // Light starts they could handle in this 15-minute window at their rate. "Free" means
  // available, not on vacation, and not tied up with an Active session at this exact slot.
  private (double capacity, int demand) ComputeRepartoCapacityAndDemand(
    DateOnly date, int slot, int repartoRate, int coveringRate, List<Therapist> relevantTherapists) {
    double capacity = 0;

    foreach (var t in relevantTherapists) {
      if (IsBlockedByVacation(t.Id, date, slot, 1)) {
        continue;
      }
      if (!IsWithinAvailability(t.Id, date, slot, 1)) {
        continue;
      }
      if (IsTherapistBusyWithActiveSession(t.Id, date, slot)) {
        continue;
      }

      var isRepartoCapable = (t.OperatingArea & TherapistOperatingArea.Reparto) != 0;
      var rate = isRepartoCapable ? repartoRate : coveringRate;
      capacity += 15.0 / rate;
    }

    var demand = CountLightRepartoDemand(date, slot);
    return (Math.Floor(capacity), demand);
  }

  private bool IsRepartoSlotSaturated(DateOnly date, int slot, int repartoRate, int coveringRate, List<Therapist> relevantTherapists) {
    var (capacity, demand) = ComputeRepartoCapacityAndDemand(date, slot, repartoRate, coveringRate, relevantTherapists);
    return demand >= capacity;
  }

  // --- Endpoints ---------------------------------------------------------------

  [HttpGet("/Pianificazione/RepartoInfo/{therapyId}")]
  public IActionResult PianificazioneRepartoInfo(int therapyId) {
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

    var parts = _db.TherapyParts
      .Where(p => p.TherapyId == therapyId)
      .OrderBy(p => p.Id)
      .ToList()
      .Select(p => {
        var type = typeCache.ContainsKey(p.TherapyTypeId) ? typeCache[p.TherapyTypeId] : null;
        var placedCount = GetPlacedCount(p.Id);

        return new {
          p.Id,
          p.TherapyTypeId,
          therapyTypeName = type != null ? type.Name : "?",
          duration = type != null ? type.Duration : 15,
          p.SessionCount,
          placedCount,
          remaining = p.SessionCount - placedCount
        };
      })
      .ToList();

    return Ok(new {
      therapyId = therapy.Id,
      patientId = patient.Id,
      patientName = patient.Name,
      parts
    });
  }

  [HttpGet("/Pianificazione/RepartoWeekData")]
  public IActionResult PianificazioneRepartoWeekData(int therapyId, DateOnly weekStart) {
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

    var typeCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);
    var partCache = _db.TherapyParts.ToDictionary(p => p.Id, p => p);
    var therapyCache = _db.Therapies.ToDictionary(t => t.Id, t => t);
    var patientCache = _db.Patients.ToDictionary(p => p.Id, p => p.Name);

    var weekSlotsRaw = _db.TherapySlots
      .Where(s => s.Date >= weekStart && s.Date <= weekEnd && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    // Both Light and Active Reparto sessions show up in the count/popup display -
    // capacity math (CountLightRepartoDemand) stays Light-only, unaffected by this.
    var repartoSlotIds = weekSlotsRaw.Where(s => {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      return type != null && type.Category == TherapyCategory.Reparto;
    }).Select(s => s.Id);

    var patientPartIds = _db.Therapies.Where(t => t.PatientId == therapy.PatientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id).ToList();

    var patientSlotIds = weekSlotsRaw.Where(s => patientPartIds.Contains(s.TherapyPartId)).Select(s => s.Id);

    var relevantSlotIds = repartoSlotIds.Union(patientSlotIds).Distinct().ToList();
    var relevantSlots = weekSlotsRaw.Where(s => relevantSlotIds.Contains(s.Id));

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
        therapyTypeId = part != null ? part.TherapyTypeId : (int?)null,
        therapyTypeName = type != null ? type.Name : "?",
        patientName = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : "?",
        isCurrentPatient
      };
    });

    // Palestra activities of "Reparto e aiuto a palestra" therapists - background info
    // so Accettazione can see who's already tied up before pulling them into Reparto.
    var repartoHelpingPalestraIds = GetRepartoHelpingPalestraTherapistIds();
    var therapistCache = _db.Therapists.ToDictionary(t => t.Id, t => t.Name);

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

    return Ok(new { dayStart, dayEnd, capacityGrid, capacityWarningThreshold, slots = slotsOutput, palestraHelperSlots });
  }

  public class RepartoRemainingOverride {
    public int PartId { get; set; }
    public int Remaining { get; set; }
  }

  public class RepartoAutoFillRequest {
    public int TherapyId { get; set; }
    // "singolo" | "giorniAlterni" | "settimanalmente" | "tuttiIGiorni"
    public string Mode { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public int TimeSlot { get; set; }
    // How many occurrences (days) to compute. 0 = "however many are needed for every
    // Part to finish" (Rimpiazza checked); a positive value requests just that many
    // additional occurrences (Rimpiazza unchecked / append).
    public int Count { get; set; }
    // Optional: current per-part remaining counts from the client's in-memory draft
    // (not yet reflected in the DB). When provided, used instead of recomputing
    // remaining from placedCount - needed for Rimpiazza-unchecked append.
    public List<RepartoRemainingOverride>? RemainingOverride { get; set; }
  }

  [HttpPost("/Pianificazione/RepartoAutoFill")]
  public IActionResult PianificazioneRepartoAutoFill([FromBody] RepartoAutoFillRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(request.TherapyId);

    if (therapy == null) {
      return NotFound();
    }

    var partsRaw = _db.TherapyParts.Where(p => p.TherapyId == request.TherapyId).OrderBy(p => p.Id).ToList();

    if (partsRaw.Count == 0) {
      return BadRequest(new { message = "Nessuna parte da pianificare." });
    }

    var typeCache = _db.TherapyTypes.ToDictionary(t => t.Id, t => t);

    var partsState = partsRaw.Select(p => {
      var type = typeCache.ContainsKey(p.TherapyTypeId) ? typeCache[p.TherapyTypeId] : null;
      var placedCount = GetPlacedCount(p.Id);
      return new {
        Part = p,
        Type = type,
        Span = type != null ? GetSpanSlots(type) : 1,
        Remaining = p.SessionCount - placedCount
      };
    }).ToList();

    if (partsState.Sum(p => p.Remaining) <= 0) {
      return BadRequest(new { message = "Tutte le sedute sono già pianificate." });
    }

    var runningRemaining = partsState.ToDictionary(p => p.Part.Id, p => p.Remaining);

    if (request.RemainingOverride != null) {
      foreach (var ov in request.RemainingOverride) {
        if (runningRemaining.ContainsKey(ov.PartId)) {
          runningRemaining[ov.PartId] = ov.Remaining;
        }
      }
    }

    var (repartoRate, coveringRate) = GetRepartoRates();
    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();

    var targetOccurrences = request.Count > 0 ? request.Count : int.MaxValue;
    var maxCandidates = Math.Min(1000, (request.Count > 0 ? request.Count : runningRemaining.Values.Sum()) * 20 + 50);
    var candidates = GenerateCandidateDates(request.Mode, request.StartDate, maxCandidates);

    var results = new List<object>();
    var occurrencesPlaced = 0;

    foreach (var date in candidates) {
      if (runningRemaining.Values.Sum() <= 0 || occurrencesPlaced >= targetOccurrences) {
        break;
      }

      var activeParts = partsState.Where(p => runningRemaining[p.Part.Id] > 0).ToList();

      if (activeParts.Count == 0) {
        break;
      }

      var segments = new List<(TherapyPart part, TherapyType? type, int span, int startSlot)>();
      var cursor = request.TimeSlot;

      foreach (var p in activeParts) {
        segments.Add((p.Part, p.Type, p.Span, cursor));
        cursor += p.Span;
      }

      // Hard block: any 15-min slot in the whole block's range being fully saturated
      // skips this date entirely (silently, doesn't count - same as vacation/unavailable
      // in the Palestra flow).
      var hardBlocked = false;
      for (var slot = request.TimeSlot; slot < cursor; slot++) {
        if (IsRepartoSlotSaturated(date, slot, repartoRate, coveringRate, relevantTherapists)) {
          hardBlocked = true;
          break;
        }
      }

      if (hardBlocked) {
        continue;
      }

      // Soft conflict (placed and counted, just flagged): same patient already has a
      // session overlapping, or the same TherapyType already that day, for any segment.
      var conflict = segments.Any(seg =>
        HasPatientConflict(therapy.PatientId, date, seg.startSlot, seg.span, seg.part.TherapyTypeId));

      var segmentsOutput = segments.Select(s => new {
        partId = s.part.Id,
        therapyTypeId = s.part.TherapyTypeId,
        therapyTypeName = s.type != null ? s.type.Name : "?",
        timeSlot = s.startSlot,
        durationSlots = s.span
      }).ToList();

      results.Add(new { date, segments = segmentsOutput, conflict });

      foreach (var p in activeParts) {
        runningRemaining[p.Part.Id]--;
      }

      occurrencesPlaced++;
    }

    if (runningRemaining.Values.Sum() > 0 && request.Count <= 0) {
      return BadRequest(new { message = "Non è stato possibile trovare abbastanza date valide in un intervallo ragionevole." });
    }

    return Ok(results);
  }

  public class RepartoAcceptSegment {
    public int PartId { get; set; }
    public int TimeSlot { get; set; }
  }

  public class RepartoAcceptPlacement {
    public DateOnly Date { get; set; }
    public List<RepartoAcceptSegment> Segments { get; set; } = new();
  }

  public class RepartoAcceptRequest {
    public int TherapyId { get; set; }
    public List<RepartoAcceptPlacement> Placements { get; set; } = new();
  }

  [HttpPost("/Pianificazione/RepartoAccept")]
  public IActionResult PianificazioneRepartoAccept([FromBody] RepartoAcceptRequest request) {
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
      foreach (var seg in placement.Segments) {
        if (!partsRaw.ContainsKey(seg.PartId)) {
          return BadRequest(new { message = "Parte non valida." });
        }

        var part = partsRaw[seg.PartId];
        var type = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var span = type != null ? GetSpanSlots(type) : 1;

        for (var slot = seg.TimeSlot; slot < seg.TimeSlot + span; slot++) {
          if (IsRepartoSlotSaturated(placement.Date, slot, repartoRate, coveringRate, relevantTherapists)) {
            return BadRequest(new { message = "Alcuni slot non sono più validi (capacità Reparto satura). Ricontrolla la pianificazione." });
          }
        }

        if (HasPatientConflict(therapy.PatientId, placement.Date, seg.TimeSlot, span, part.TherapyTypeId)) {
          return BadRequest(new { message = "Sono presenti conflitti non risolti. Ricontrolla la pianificazione." });
        }
      }
    }

    foreach (var placement in request.Placements) {
      foreach (var seg in placement.Segments) {
        _db.TherapySlots.Add(BuildTherapySlot(seg.PartId, placement.Date, seg.TimeSlot, null));
      }
    }

    therapy.Status = TherapyStatus.Scheduled;
    _db.SaveChanges();

    return Ok();
  }
}
