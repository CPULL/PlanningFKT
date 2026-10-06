using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // --- Shared helpers --------------------------------------------------------

  private static int GetSpanSlots(TherapyType type) {
    return (int)Math.Ceiling(type.Duration / 15.0);
  }

  // An existing slot's real length: its TherapyType's duration plus any extra time
  // attached to that single session (Isocinetica first/last +15 min).
  private static int GetSlotSpan(TherapySlot slot, TherapyType? type) {
    return (type != null ? GetSpanSlots(type) : 1) + Math.Max(0, slot.ExtraSlots);
  }

  private static string FormatSlotTime(int timeSlot) {
    return (timeSlot / 4).ToString("D2") + ":" + ((timeSlot % 4) * 15).ToString("D2");
  }

  private static string FormatShortDate(DateOnly date) {
    return date.ToString("dd/MM");
  }

  // "Patient (TherapyType)" for an existing slot - used in failure reasons.
  private string DescribeSlotOwner(TherapySlot slot) {
    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
    var therapy = part != null ? _db.Therapies.Find(part.TherapyId) : null;
    var patient = therapy != null ? _db.Patients.Find(therapy.PatientId) : null;
    return (patient?.Name ?? "?") + (type != null ? " (" + type.Name + ")" : "");
  }

  // --- Placement failure reasons (CPU: never fail silently) -----------------------
  // Each Describe* mirrors the matching boolean check exactly and returns null when
  // that check passes, or a short Italian reason when it doesn't.

  // Vacation + availability only (the part of IsTherapistFreeForSpan that makes
  // AutoFill skip a day, as opposed to a conflict that is placed and flagged).
  private string? DescribeTherapistAvailabilityBlock(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var therapist = _db.Therapists.Find(therapistId);
    var name = therapist?.Name ?? "Il terapista";

    if (IsBlockedByVacation(therapistId, date, timeSlot, spanSlots)) {
      return name + " è in ferie/assente";
    }

    if (!IsWithinAvailability(therapistId, date, timeSlot, spanSlots)) {
      var dayOfWeek = (int)date.DayOfWeek;
      var margin = therapist != null && therapist.OvertimeAllowed == 1 ? 4 : 0;
      var windows = _db.TherapistAvailabilities
        .Where(a => a.TherapistId == therapistId && a.DayOfWeek == dayOfWeek)
        .ToList();
      var containing = windows.FirstOrDefault(a => timeSlot >= a.StartTime - margin && timeSlot < a.EndTime + margin);

      if (containing != null) {
        return "la seduta dura " + (spanSlots * 15) + " minuti ma " + name + " è disponibile solo fino alle " + FormatSlotTime(containing.EndTime + margin);
      }

      return name + " non è disponibile alle " + FormatSlotTime(timeSlot);
    }

    return null;
  }

  // Full IsTherapistFreeForSpan equivalent: vacation, availability, other bookings.
  private string? DescribeTherapistBlock(int therapistId, DateOnly date, int timeSlot, int spanSlots, int? excludeSlotId = null) {
    var availabilityBlock = DescribeTherapistAvailabilityBlock(therapistId, date, timeSlot, spanSlots);

    if (availabilityBlock != null) {
      return availabilityBlock;
    }

    var conflictSlot = FindTherapistConflictSlot(therapistId, date, timeSlot, spanSlots, excludeSlotId);

    if (conflictSlot != null) {
      var name = _db.Therapists.Find(therapistId)?.Name ?? "Il terapista";
      return name + " ha già una seduta alle " + FormatSlotTime(conflictSlot.TimeSlot) + " con " + DescribeSlotOwner(conflictSlot);
    }

    return null;
  }

  // First saturated Reparto 15-min slot in [timeSlot, timeSlot + spanSlots), if any.
  private string? DescribeRepartoSaturation(DateOnly date, int timeSlot, int spanSlots, int repartoRate, int coveringRate, List<Therapist> relevantTherapists) {
    for (var s = timeSlot; s < timeSlot + spanSlots; s++) {
      if (IsRepartoSlotSaturated(date, s, repartoRate, coveringRate, relevantTherapists)) {
        return "capacità Reparto satura alle " + FormatSlotTime(s);
      }
    }

    return null;
  }

  private static string WithDate(DateOnly date, string reason) {
    return FormatShortDate(date) + ": " + reason;
  }

  // --- Isocinetica extra time ---------------------------------------------------
  //
  // CPU's rule: an Isocinetica therapy's FIRST and LAST sessions each get 15 extra
  // minutes after them; a renewal (the patient's previous therapy already had
  // Isocinetica) gets it only on the LAST one. Recomputed for the whole therapy at
  // every Accept; moving a slot afterwards is deliberately not handled.
  private const int IsocineticaTherapyTypeId = 7;
  private const int IsocineticaExtraSlots = 1;

  private bool IsIsocineticaRenewal(Therapy therapy) {
    var previousTherapy = _db.Therapies
      .Where(t => t.PatientId == therapy.PatientId && t.Id < therapy.Id && t.Status != TherapyStatus.Cancelled)
      .OrderByDescending(t => t.Id)
      .FirstOrDefault();

    return previousTherapy != null
      && _db.TherapyParts.Any(p => p.TherapyId == previousTherapy.Id && p.TherapyTypeId == IsocineticaTherapyTypeId);
  }

  // Extra slots the session with the given 1-based number should get, for a part of
  // the given type - used by AutoFill to size drafts before they're saved.
  private static int IsocineticaExtraFor(TherapyPart part, int sessionNumber, bool isRenewal) {
    if (part.TherapyTypeId != IsocineticaTherapyTypeId) {
      return 0;
    }

    var isFirst = sessionNumber == 1 && !isRenewal;
    var isLast = sessionNumber == part.SessionCount;
    return isFirst || isLast ? IsocineticaExtraSlots : 0;
  }

  // Why the 15 extra minutes right after this slot can't be added (null = free):
  // the assigned therapist and the patient must both be free for that quarter hour.
  private string? DescribeExtraTimeBlock(TherapySlot slot, TherapyType type, int patientId, int extraSlots) {
    var extraStart = slot.TimeSlot + GetSpanSlots(type);

    if (slot.TherapistId.HasValue) {
      var therapistBlock = DescribeTherapistBlock(slot.TherapistId.Value, slot.Date, extraStart, extraSlots);
      if (therapistBlock != null) {
        return therapistBlock;
      }
    }

    var partIds = _db.Therapies
      .Where(t => t.PatientId == patientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id)
      .ToList();

    var patientSlots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Date == slot.Date && s.Id != slot.Id && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    foreach (var other in patientSlots) {
      var otherPart = _db.TherapyParts.Find(other.TherapyPartId);
      var otherType = otherPart != null ? _db.TherapyTypes.Find(otherPart.TherapyTypeId) : null;
      var otherEnd = other.TimeSlot + GetSlotSpan(other, otherType);

      if (extraStart < otherEnd && extraStart + extraSlots > other.TimeSlot) {
        return "il paziente ha già " + (otherType?.Name ?? "una seduta") + " alle " + FormatSlotTime(other.TimeSlot);
      }
    }

    return null;
  }

  // Sets ExtraSlots on this therapy's Isocinetica sessions (first + last, or just
  // last for a renewal) and clears it from any to-be-done session that is no longer
  // first/last (e.g. the previous last one, after appending more sessions). Returns
  // one warning per session that couldn't be extended - caller must SaveChanges.
  private List<string> ApplyIsocineticaExtraSlots(Therapy therapy) {
    var warnings = new List<string>();
    var type = _db.TherapyTypes.Find(IsocineticaTherapyTypeId);

    if (type == null) {
      return warnings;
    }

    var partIds = _db.TherapyParts
      .Where(p => p.TherapyId == therapy.Id && p.TherapyTypeId == IsocineticaTherapyTypeId)
      .Select(p => p.Id)
      .ToList();

    if (partIds.Count == 0) {
      return warnings;
    }

    var slots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .ToList()
      .OrderBy(s => s.Date).ThenBy(s => s.TimeSlot)
      .ToList();

    if (slots.Count == 0) {
      return warnings;
    }

    var targetIds = new HashSet<int> { slots[slots.Count - 1].Id };

    if (!IsIsocineticaRenewal(therapy)) {
      targetIds.Add(slots[0].Id);
    }

    foreach (var s in slots) {
      if (!targetIds.Contains(s.Id)) {
        if (s.ExtraSlots != 0 && s.Status == TherapySlotStatus.ToBeDone) {
          s.ExtraSlots = 0;
        }
        continue;
      }

      if (s.ExtraSlots >= IsocineticaExtraSlots) {
        continue;
      }

      var reason = DescribeExtraTimeBlock(s, type, therapy.PatientId, IsocineticaExtraSlots);

      if (reason == null) {
        s.ExtraSlots = IsocineticaExtraSlots;
      } else {
        warnings.Add("Isocinetica del " + FormatShortDate(s.Date) + " alle " + FormatSlotTime(s.TimeSlot)
          + ": non è stato possibile aggiungere i 15 minuti extra (" + reason + ").");
      }
    }

    return warnings;
  }

  private int GetPlacedCount(int partId) {
    return _db.TherapySlots.Count(s => s.TherapyPartId == partId && s.Status != TherapySlotStatus.Rescheduled
      && !_replanExcludedSlotIds.Contains(s.Id));
  }

  // --- Ripianifica ---------------------------------------------------------------
  //
  // CPU: "Ripianifica" puts a planned therapy's not-yet-done sessions (from today
  // on, status Da fare) back into the planning screen as drafts, so they can be
  // moved, removed or re-planned. Done (Fatto) sessions stay fixed and nothing can
  // be moved into the past. While planning, the sessions being re-planned are
  // IGNORED by every count/conflict/capacity check (they're about to be replaced,
  // so their current position must not block anything); on Accept they're deleted
  // and the new ones created in a single transaction.
  private List<int> _replanExcludedSlotIds = new();

  private List<TherapySlot> GetReplaceableSlots(int therapyId, IEnumerable<int> slotIds) {
    var ids = slotIds.Distinct().ToList();
    if (ids.Count == 0) {
      return new List<TherapySlot>();
    }

    var today = DateOnly.FromDateTime(DateTime.Today);
    var partIds = _db.TherapyParts.Where(p => p.TherapyId == therapyId).Select(p => p.Id).ToList();

    return _db.TherapySlots
      .Where(s => ids.Contains(s.Id) && partIds.Contains(s.TherapyPartId)
        && s.Status == TherapySlotStatus.ToBeDone && s.Date >= today)
      .ToList();
  }

  // Only the therapy's own replaceable sessions are ever excluded, whatever the
  // client sends.
  private void SetReplanExclusion(int therapyId, IEnumerable<int>? slotIds) {
    _replanExcludedSlotIds = slotIds == null
      ? new List<int>()
      : GetReplaceableSlots(therapyId, slotIds).Select(s => s.Id).ToList();
  }

  private static List<int> ParseIdList(string? csv) {
    return (csv ?? string.Empty)
      .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(x => int.TryParse(x, out var id) ? id : 0)
      .Where(id => id > 0)
      .ToList();
  }

  // Accept side of Ripianifica: removes the sessions being replaced inside the
  // caller's open transaction, so validation and the new sessions see the days
  // without them - any failure afterwards rolls the removal back. Returns an error
  // message, or null when there's nothing to replace or it went fine.
  private string? RemoveReplacedSlots(int therapyId, List<int>? replaceSlotIds, IEnumerable<DateOnly> newDates) {
    if (replaceSlotIds == null || replaceSlotIds.Count == 0) {
      return null;
    }

    var today = DateOnly.FromDateTime(DateTime.Today);
    if (newDates.Any(d => d < today)) {
      return "Non si può ripianificare una seduta nel passato.";
    }

    var slots = GetReplaceableSlots(therapyId, replaceSlotIds);
    if (slots.Count != replaceSlotIds.Distinct().Count()) {
      return "Alcune sedute da ripianificare sono cambiate nel frattempo (fatte, spostate o eliminate). Riapri Ripianifica.";
    }

    DetachRescheduledReferences(slots.Select(s => s.Id).ToList());
    _db.TherapySlots.RemoveRange(slots);
    _db.SaveChanges();
    return null;
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

  // Same as IsWithinAvailability but WITHOUT the overtime margin - CPU's call:
  // fallback slots offered for reassignment must stay within a therapist's
  // actual declared hours, even if they personally have overtime allowed.
  private bool IsWithinDeclaredAvailability(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var dayOfWeek = (int)date.DayOfWeek;
    var newEnd = timeSlot + spanSlots;

    _availabilityByTherapistDayCache ??= new Dictionary<(int, int), List<TherapistAvailability>>();
    var key = (therapistId, dayOfWeek);

    if (!_availabilityByTherapistDayCache.TryGetValue(key, out var availability)) {
      availability = _db.TherapistAvailabilities
        .Where(a => a.TherapistId == therapistId && a.DayOfWeek == dayOfWeek)
        .ToList();
      _availabilityByTherapistDayCache[key] = availability;
    }

    return availability.Any(a => timeSlot >= a.StartTime && newEnd <= a.EndTime);
  }

  // A therapist's effective area at a specific moment - checks whether the
  // matching TherapistAvailability window (if any) has an override, falling
  // back to their main OperatingArea when it doesn't (or no window matches at
  // all, e.g. an overtime slot outside any declared window). CPU's call: a
  // therapist can be, say, mostly-Palestra-plus-aiuto in the morning and pure
  // Reparto in the afternoon, on a fixed weekly schedule.
  private int GetEffectiveOperatingArea(Therapist therapist, DateOnly date, int timeSlot) {
    var dayOfWeek = (int)date.DayOfWeek;

    _availabilityByTherapistDayCache ??= new Dictionary<(int, int), List<TherapistAvailability>>();
    var key = (therapist.Id, dayOfWeek);

    if (!_availabilityByTherapistDayCache.TryGetValue(key, out var availability)) {
      availability = _db.TherapistAvailabilities
        .Where(a => a.TherapistId == therapist.Id && a.DayOfWeek == dayOfWeek)
        .ToList();
      _availabilityByTherapistDayCache[key] = availability;
    }

    var matchingWindow = availability.FirstOrDefault(a => timeSlot >= a.StartTime && timeSlot < a.EndTime);
    if (matchingWindow != null && matchingWindow.OverrideOperatingArea.HasValue) {
      return matchingWindow.OverrideOperatingArea.Value;
    }

    return therapist.OperatingArea;
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
    return FindTherapistConflictSlot(therapistId, date, timeSlot, spanSlots) != null;
  }

  // Same overlap check as HasTherapistConflict, but returns the actual
  // conflicting slot - needed to build a friendly "Impegnato con X" reason.
  // excludeSlotId: the slot being moved itself, so a shift that overlaps its own
  // old position isn't reported as a conflict with itself.
  private TherapySlot? FindTherapistConflictSlot(int therapistId, DateOnly date, int timeSlot, int spanSlots, int? excludeSlotId = null) {
    var newEnd = timeSlot + spanSlots;

    var existingSlots = _db.TherapySlots
      .Where(s => s.TherapistId == therapistId && s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList()
      .Where(s => s.Id != excludeSlotId && !_replanExcludedSlotIds.Contains(s.Id))
      .ToList();

    foreach (var slot in existingSlots) {
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
      var existingSpan = GetSlotSpan(slot, type);
      var existingEnd = slot.TimeSlot + existingSpan;

      if (timeSlot < existingEnd && newEnd > slot.TimeSlot) {
        return slot;
      }
    }

    return null;
  }

  // Ginnastica Attiva only cares about REPARTO bookings for this therapist -
  // another Palestra patient at the same time is fine (no active intervention
  // needed, no capacity consumed - CPU's call). Deliberately does NOT reuse
  // HasTherapistConflict, which would incorrectly block on harmless Palestra
  // overlaps too.
  private bool HasRepartoConflictForGinnasticaAttiva(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var newEnd = timeSlot + spanSlots;

    var existingSlots = _db.TherapySlots
      .Where(s => s.TherapistId == therapistId && s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    foreach (var slot in existingSlots) {
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

      if (type == null || type.Category != TherapyCategory.Reparto) {
        continue;
      }

      var existingSpan = GetSlotSpan(slot, type);
      var existingEnd = slot.TimeSlot + existingSpan;

      if (timeSlot < existingEnd && newEnd > slot.TimeSlot) {
        return true;
      }
    }

    return false;
  }

  // Auto-attaches Ginnastica Attiva to a just-placed Rieducazione Motoria/
  // Isocinetica slot, if its TherapyPart requested it. Call AFTER the slot (and
  // any same-day siblings from the same placement batch) is already saved, so
  // the day's picture is complete when checking for Reparto conflicts.
  //
  // Placement rule (CPU's design): GA goes on whichever edge is free of Reparto
  // bookings for this therapist - if Reparto exists only before RM, GA goes
  // after; if only after, GA goes before; otherwise (neither or both), after is
  // tried first. Always prefers a full 30-minute reservation (2 slots) even
  // when only 15 min is requested, so a later resize never needs to shift
  // anything else; falls back to just the requested amount if 30 min doesn't
  // fit anywhere; skips attaching GA entirely if even that doesn't fit.
  private void AutoPlaceGinnasticaAttiva(TherapySlot rmSlot) {
    if (rmSlot.TherapistId == null) {
      return;
    }

    var part = _db.TherapyParts.Find(rmSlot.TherapyPartId);
    if (part == null || part.DefaultGinnasticaAttivaSlots <= 0) {
      return;
    }

    var type = _db.TherapyTypes.Find(part.TherapyTypeId);
    if (type == null || type.AllowsGinnasticaAttiva == 0) {
      return;
    }

    var therapistId = rmSlot.TherapistId.Value;
    var date = rmSlot.Date;
    var rmSpan = GetSlotSpan(rmSlot, type);
    var rmStart = rmSlot.TimeSlot;
    var rmEnd = rmStart + rmSpan;
    var requestedSlots = part.DefaultGinnasticaAttivaSlots;
    const int reserveSlots = 2; // 30 minutes - CPU's call, reserved regardless of the requested amount

    var daySlots = _db.TherapySlots
      .Where(s => s.TherapistId == therapistId && s.Date == date && s.Id != rmSlot.Id && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    var repartoBefore = false;
    var repartoAfter = false;

    foreach (var s in daySlots) {
      var sPart = _db.TherapyParts.Find(s.TherapyPartId);
      var sType = sPart != null ? _db.TherapyTypes.Find(sPart.TherapyTypeId) : null;

      if (sType == null || sType.Category != TherapyCategory.Reparto) {
        continue;
      }

      var sSpan = GetSlotSpan(s, sType);

      if (s.TimeSlot + sSpan <= rmStart) {
        repartoBefore = true;
      }
      if (s.TimeSlot >= rmEnd) {
        repartoAfter = true;
      }
    }

    var tryAfterFirst = !repartoAfter;

    int MaxFreeSlotsAtEdge(bool after) {
      var count = 0;
      while (count < reserveSlots) {
        var candidateStart = after ? rmEnd + count : rmStart - count - 1;
        if (candidateStart < 0) {
          break;
        }
        if (!IsWithinAvailability(therapistId, date, candidateStart, 1)
            || IsBlockedByVacation(therapistId, date, candidateStart, 1)
            || HasRepartoConflictForGinnasticaAttiva(therapistId, date, candidateStart, 1)) {
          break;
        }
        count++;
      }
      return count;
    }

    var afterRoom = MaxFreeSlotsAtEdge(true);
    var beforeRoom = MaxFreeSlotsAtEdge(false);

    bool TryPlace(int neededSlots, out bool placeAfterResult) {
      if (tryAfterFirst && afterRoom >= neededSlots) {
        placeAfterResult = true;
        return true;
      }
      if (!tryAfterFirst && beforeRoom >= neededSlots) {
        placeAfterResult = false;
        return true;
      }
      if (afterRoom >= neededSlots) {
        placeAfterResult = true;
        return true;
      }
      if (beforeRoom >= neededSlots) {
        placeAfterResult = false;
        return true;
      }
      placeAfterResult = false;
      return false;
    }

    bool placeAfter;
    if (!TryPlace(reserveSlots, out placeAfter) && !TryPlace(requestedSlots, out placeAfter)) {
      return; // Doesn't fit anywhere, even at the bare requested amount - skip.
    }

    rmSlot.GinnasticaAttivaSlots = placeAfter ? requestedSlots : -requestedSlots;
  }

  private bool HasPatientConflict(int patientId, DateOnly date, int timeSlot, int spanSlots, int therapyTypeId) {
    return DescribePatientConflict(patientId, date, timeSlot, spanSlots, therapyTypeId) != null;
  }

  // Same rules as HasPatientConflict, with the reason (null = no conflict).
  private string? DescribePatientConflict(int patientId, DateOnly date, int timeSlot, int spanSlots, int therapyTypeId) {
    var newEnd = timeSlot + spanSlots;

    var partIds = _db.Therapies
      .Where(t => t.PatientId == patientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id)
      .ToList();

    var existingSlots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Date == date && s.Status != TherapySlotStatus.Rescheduled
        && !_replanExcludedSlotIds.Contains(s.Id))
      .ToList();

    foreach (var slot in existingSlots) {
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
      var existingSpan = GetSlotSpan(slot, type);
      var existingEnd = slot.TimeSlot + existingSpan;
      var typeName = type?.Name ?? "una seduta";

      // Same TherapyType twice in one day is a conflict even if the times don't
      // overlap at all (spec rule: "a patient cannot do the same TherapyType twice
      // in one day") - checked in addition to the plain time-range overlap below.
      if (part != null && part.TherapyTypeId == therapyTypeId) {
        return "il paziente ha già " + typeName + " in questo giorno (alle " + FormatSlotTime(slot.TimeSlot) + ")";
      }

      if (timeSlot < existingEnd && newEnd > slot.TimeSlot) {
        return "il paziente ha già " + typeName + " alle " + FormatSlotTime(slot.TimeSlot);
      }
    }

    return null;
  }

  private class ExistingPatientSessionDto {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
    public int DurationSlots { get; set; }
    public int TherapyTypeId { get; set; }
  }

  // Every real, already-committed slot for this patient. (CPU's call: the
  // frontend's own draft-vs-draft conflict check was WRONGLY treating two new
  // items being placed together in the SAME batch as conflicting just for
  // sharing a type - that's never what HasPatientConflict does server-side,
  // which only ever compares against rows that already exist. This lets the
  // frontend mirror that correctly: check a draft item against THIS list, not
  // against its own siblings.)
  private List<ExistingPatientSessionDto> GetExistingPatientSessions(int patientId) {
    var partIds = _db.Therapies
      .Where(t => t.PatientId == patientId)
      .SelectMany(t => _db.TherapyParts.Where(p => p.TherapyId == t.Id))
      .Select(p => p.Id)
      .ToList();

    var typeByPartId = _db.TherapyParts
      .Where(p => partIds.Contains(p.Id))
      .ToDictionary(p => p.Id, p => p.TherapyTypeId);

    var typeCache = GetTypeCache();

    return _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled
        && !_replanExcludedSlotIds.Contains(s.Id))
      .ToList()
      .Select(s => {
        var therapyTypeId = typeByPartId.ContainsKey(s.TherapyPartId) ? typeByPartId[s.TherapyPartId] : 0;
        var type = typeCache.ContainsKey(therapyTypeId) ? typeCache[therapyTypeId] : null;

        return new ExistingPatientSessionDto {
          Date = s.Date,
          TimeSlot = s.TimeSlot,
          DurationSlots = GetSlotSpan(s, type),
          TherapyTypeId = therapyTypeId
        };
      })
      .ToList();
  }

  // --- Pianificazione ----------------------------------------------------

  [HttpGet("/Pianificazione/Info/{partId}")]
  public IActionResult PianificazioneInfo(int partId, string? exclude = null) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var part = _db.TherapyParts.Find(partId);

    if (part == null) {
      return NotFound();
    }

    SetReplanExclusion(part.TherapyId, ParseIdList(exclude));

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
    var existingSessions = GetExistingPatientSessions(patient.Id);

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
      remaining = part.SessionCount - placedCount,
      existingSessions
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
  public IActionResult PianificazioneWeekData(int therapistId, int partId, DateOnly weekStart, string? exclude = null) {
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

    var type = _db.TherapyTypes.Find(part.TherapyTypeId);
    var therapist = _db.Therapists.Find(therapistId);

    SetReplanExclusion(therapy.Id, ParseIdList(exclude));

    var weekEnd = weekStart.AddDays(4);

    // categoryMismatch per window (CPU's call): the therapist's EFFECTIVE area
    // for that window (its own override if set, else their main OperatingArea)
    // doesn't match the category currently being planned - same rule as the
    // reassign popup's red case, shown proactively here instead of only on click.
    var availability = _db.TherapistAvailabilities
      .Where(a => a.TherapistId == therapistId)
      .ToList()
      .Select(a => {
        var effectiveArea = a.OverrideOperatingArea ?? (therapist?.OperatingArea ?? 0);
        var effIsReparto = (effectiveArea & TherapistOperatingArea.Reparto) != 0;
        var effHelpsOtherArea = (effectiveArea & TherapistOperatingArea.HelpsOtherArea) != 0;
        var categoryMismatch = type != null && (type.Category == TherapyCategory.Palestra
          ? effectiveArea == TherapistOperatingArea.Reparto
          : !(effIsReparto || effHelpsOtherArea));

        return new { a.DayOfWeek, a.StartTime, a.EndTime, categoryMismatch };
      })
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

    var allSlotIds = therapistSlotIds.Union(patientSlotIds).Distinct().ToList()
      .Where(id => !_replanExcludedSlotIds.Contains(id))
      .ToList();
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
        durationSlots = GetSlotSpan(s, slotType),
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

    // Same overtime margin IsWithinAvailability applies server-side, so the
    // client-side placement check doesn't reject what the server would accept.
    var overtimeMargin = therapist != null && therapist.OvertimeAllowed == 1 ? 4 : 0;

    return Ok(new { availability, vacations, slots, overtimeMargin });
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
    // Ripianifica: sessions being re-planned (ignored by every check).
    public List<int>? ReplaceSlotIds { get; set; }
  }

  // Generates candidate dates for the 3 automatic modes. Bounded (not an infinite
  // generator) as a safety valve against a therapist with no matching availability.
  private List<DateOnly> GenerateCandidateDates(string mode, DateOnly startDate, int maxCandidates) {
    var candidates = new List<DateOnly>();
    var date = startDate;

    // Giorno singolo: exactly the clicked day - if it doesn't fit, the caller
    // reports why instead of silently sliding to the next working day.
    if (mode == "singolo") {
      candidates.Add(startDate);
      return candidates;
    }

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

    SetReplanExclusion(therapy.Id, request.ReplaceSlotIds);

    var placedCount = GetPlacedCount(part.Id);
    var trueRemaining = part.SessionCount - placedCount;

    if (trueRemaining <= 0) {
      return BadRequest(new { message = "Tutte le sedute di questa parte sono già pianificate." });
    }

    var targetCount = request.Count > 0 ? Math.Min(request.Count, trueRemaining) : trueRemaining;

    var spanSlots = GetSpanSlots(therapyType);
    var maxCandidates = Math.Min(500, targetCount * 20 + 50);
    var candidates = GenerateCandidateDates(request.Mode, request.StartDate, maxCandidates);

    // Isocinetica first/last sessions are 15 min longer - sized here already so the
    // conflict check (and the draft shown) covers the real length. Sessions already
    // in the client's draft (append mode) come before these ones.
    var isRenewal = part.TherapyTypeId == IsocineticaTherapyTypeId && IsIsocineticaRenewal(therapy);
    var draftCount = request.Count > 0 ? trueRemaining - targetCount : 0;

    var results = new List<object>();
    string? firstSkipReason = null;

    foreach (var date in candidates) {
      if (results.Count >= targetCount) {
        break;
      }

      // Vacation/unavailable days are passed over - they don't count toward the
      // session total at all (CONFIRMED behavior, applies to all modes). The first
      // reason is kept so a failure can say why (CPU: never fail silently).
      var skipReason = DescribeTherapistAvailabilityBlock(request.TherapistId, date, request.TimeSlot, spanSlots);
      if (skipReason != null) {
        firstSkipReason ??= WithDate(date, skipReason);
        continue;
      }

      var sessionNumber = placedCount + draftCount + results.Count + 1;
      var extraSlots = IsocineticaExtraFor(part, sessionNumber, isRenewal);

      // A scheduling conflict (therapist or patient already occupied) IS placed
      // and counted, just flagged - it does not get skipped (CONFIRMED).
      var conflictReason = DescribeTherapistBlock(request.TherapistId, date, request.TimeSlot, spanSlots + extraSlots)
        ?? DescribePatientConflict(therapy.PatientId, date, request.TimeSlot, spanSlots + extraSlots, part.TherapyTypeId);

      results.Add(new {
        date,
        timeSlot = request.TimeSlot,
        extraSlots,
        conflict = conflictReason != null,
        conflictReason = conflictReason != null ? WithDate(date, conflictReason) : null
      });
    }

    if (results.Count == 0) {
      return BadRequest(new { message = "Impossibile pianificare da qui - " + (firstSkipReason ?? "nessuna data valida trovata") + "." });
    }

    if (results.Count < targetCount) {
      return BadRequest(new { message = "Non è stato possibile trovare abbastanza date valide in un intervallo ragionevole."
        + (firstSkipReason != null ? " Primo motivo - " + firstSkipReason + "." : "") });
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
    // Ripianifica: sessions replaced by these placements (deleted on Accept).
    public List<int>? ReplaceSlotIds { get; set; }
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

    // Everything below (replaced sessions removed, new ones created) commits
    // together or not at all - any early return rolls back.
    using var transaction = _db.Database.BeginTransaction();

    var replaceError = RemoveReplacedSlots(therapy.Id, request.ReplaceSlotIds, request.Placements.Select(p => p.Date));
    if (replaceError != null) {
      return BadRequest(new { message = replaceError });
    }

    var placedCount = GetPlacedCount(part.Id);
    var remaining = part.SessionCount - placedCount;

    if (request.Placements.Count != remaining) {
      return BadRequest(new { message = "Il numero di sedute pianificate non corrisponde a quelle richieste." });
    }

    // Never trust client-side conflict flags - re-validate every placement from
    // scratch against real, current data before committing anything.
    foreach (var p in request.Placements) {
      var reason = DescribeTherapistBlock(request.TherapistId, p.Date, p.TimeSlot, spanSlots)
        ?? DescribePatientConflict(therapy.PatientId, p.Date, p.TimeSlot, spanSlots, part.TherapyTypeId);

      if (reason != null) {
        return BadRequest(new { message = "Seduta del " + FormatShortDate(p.Date) + " alle " + FormatSlotTime(p.TimeSlot) + " non più valida - " + reason + "." });
      }
    }

    var newSlots = new List<TherapySlot>();
    foreach (var p in request.Placements) {
      var newSlot = BuildTherapySlot(part.Id, p.Date, p.TimeSlot, request.TherapistId);
      newSlots.Add(newSlot);
      _db.TherapySlots.Add(newSlot);
    }

    therapy.Status = TherapyStatus.Scheduled;
    _db.SaveChanges();

    // Isocinetica extra time first, so Ginnastica Attiva is placed around the
    // session's real (extended) length.
    var warnings = ApplyIsocineticaExtraSlots(therapy);
    _db.SaveChanges();

    if (part.DefaultGinnasticaAttivaSlots > 0) {
      foreach (var newSlot in newSlots) {
        AutoPlaceGinnasticaAttiva(newSlot);
      }
      _db.SaveChanges();
    }

    transaction.Commit();

    warnings.AddRange(GetBudgetWarningsForTherapy(therapy, request.Placements.Select(p => p.Date)));

    return Ok(new { warnings });
  }
}
