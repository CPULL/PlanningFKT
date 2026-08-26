using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class DismissAlertRequest {
    public string Key { get; set; } = string.Empty;
  }

  // 64-bit FNV-1a over a type-specific string built from whichever of
  // date/patientId/therapistId/type actually identify "the same issue" for that
  // alert type - see each alert block below for its exact key ingredients.
  private static long ComputeAlertKey(string input) {
    const ulong fnvOffset = 14695981039346656037;
    const ulong fnvPrime = 1099511628211;
    var hash = fnvOffset;

    foreach (var b in System.Text.Encoding.UTF8.GetBytes(input)) {
      hash ^= b;
      hash *= fnvPrime;
    }

    return unchecked((long)hash);
  }

  [HttpPost("/Alerts/Dismiss")]
  public IActionResult AlertsDismiss([FromBody] DismissAlertRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (!long.TryParse(request.Key, out var key)) {
      return BadRequest();
    }

    if (!_db.AlertDismissals.Any(d => d.Key == key)) {
      _db.AlertDismissals.Add(new AlertDismissal { Key = key, DismissedAt = DateTime.Now });
      _db.SaveChanges();
    }

    return Ok();
  }

  public class MarkImportantAlertRequest {
    public string Key { get; set; } = string.Empty;
  }

  [HttpPost("/Alerts/MarkImportant")]
  public IActionResult AlertsMarkImportant([FromBody] MarkImportantAlertRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (!long.TryParse(request.Key, out var key)) {
      return BadRequest();
    }

    if (!_db.AlertMarkedImportants.Any(m => m.Key == key)) {
      _db.AlertMarkedImportants.Add(new AlertMarkedImportant { Key = key, MarkedAt = DateTime.Now });
      _db.SaveChanges();
    }

    return Ok();
  }

  [HttpPost("/Alerts/Gestito")]
  public IActionResult AlertsGestito([FromBody] MarkImportantAlertRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (!long.TryParse(request.Key, out var key)) {
      return BadRequest();
    }

    var row = _db.AlertMarkedImportants.FirstOrDefault(m => m.Key == key);
    if (row != null) {
      _db.AlertMarkedImportants.Remove(row);
      _db.SaveChanges();
    }

    return Ok();
  }

  [HttpGet("/Alerts/List")]
  public IActionResult AlertsList() {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    // Dismissals older than a month are pruned here rather than on a schedule -
    // this endpoint is hit constantly, so "as soon as alerts are generated" (CPU's
    // call) is effectively continuous cleanup with no extra moving parts.
    var dismissalCutoff = DateTime.Now.AddMonths(-1);
    var staleDismissals = _db.AlertDismissals.Where(d => d.DismissedAt < dismissalCutoff);
    _db.AlertDismissals.RemoveRange(staleDismissals);

    // Important-marks get the same rolling cleanup as dismissals - if "Gestito"
    // never gets clicked, it shouldn't live forever either.
    var importantCutoff = DateTime.Now.AddMonths(-2);
    var staleImportant = _db.AlertMarkedImportants.Where(m => m.MarkedAt < importantCutoff);
    _db.AlertMarkedImportants.RemoveRange(staleImportant);
    _db.SaveChanges();

    var dismissedKeys = _db.AlertDismissals.Select(d => d.Key).ToHashSet();
    var importantKeys = _db.AlertMarkedImportants.Select(m => m.Key).ToHashSet();

    var alerts = new List<(long Key, object Data)>();
    var today = DateOnly.FromDateTime(DateTime.Today);
    // CPU: every alert type is bounded to the same window - 1 week back, 2 weeks
    // forward - both for relevance (old/far-future alerts aren't useful) and
    // performance now that real historical data is loaded. therapyRenewal's
    // session-count math still uses full history for accuracy; only the decision
    // to surface it is bounded (see below).
    var pastCutoff = today.AddDays(-7);
    var futureCutoff = today.AddDays(14);
    var pastCutoffDateTime = pastCutoff.ToDateTime(TimeOnly.MinValue);

    var patientNames = _db.Patients.AsNoTracking().ToDictionary(p => p.Id, p => p.Name);
    var patients = _db.Patients.AsNoTracking().ToDictionary(p => p.Id, p => p);
    var typeCache = _db.TherapyTypes.AsNoTracking().ToDictionary(t => t.Id, t => t);
    var partCache = _db.TherapyParts.AsNoTracking().ToDictionary(p => p.Id, p => p);
    var therapyCache = _db.Therapies.AsNoTracking().ToDictionary(t => t.Id, t => t);
    var therapistCache = _db.Therapists.AsNoTracking().ToDictionary(t => t.Id, t => t);

    // Alert: Therapy defined but not yet scheduled - one card per patient. Key: a
    // patient can only ever have one Therapy awaiting scheduling at a time, so
    // patientId + type alone is enough (CPU's call). Bounded by ModDate so a
    // therapy nobody has touched in 2+ weeks stops cluttering the list - CPU:
    // "all avvisi should be bounded by dates".
    var toBeScheduled = _db.Therapies
      .Where(t => t.Status == TherapyStatus.ToBeScheduled && t.ModDate >= pastCutoffDateTime)
      .ToList();

    foreach (var therapy in toBeScheduled) {
      var key = ComputeAlertKey("therapyToBeScheduled|" + therapy.PatientId);
      if (dismissedKeys.Contains(key)) {
        continue;
      }

      var patientName = patientNames.ContainsKey(therapy.PatientId) ? patientNames[therapy.PatientId] : "?";
      var therapyParts = partCache.Values.Where(p => p.TherapyId == therapy.Id).ToList();
      var partNames = therapyParts
        .Select(p => typeCache.ContainsKey(p.TherapyTypeId) ? typeCache[p.TherapyTypeId] : null)
        .Where(t => t != null)
        .Select(t => string.IsNullOrEmpty(t!.Abbreviazione) ? t.Name : t.Abbreviazione)
        .ToList();
      var therapiesLabel = string.Join(", ", partNames);

      var partsDetail = string.Join("; ", therapyParts.Select(p => {
        var t = typeCache.ContainsKey(p.TherapyTypeId) ? typeCache[p.TherapyTypeId] : null;
        return (t?.Name ?? "?") + ", " + p.SessionCount + " sedute";
      }));

      alerts.Add((key, new {
        type = "therapyToBeScheduled",
        title = "Terapia da pianificare",
        icon = "📅",
        color = "#1966E2",
        dismissKey = key.ToString(),
        line1 = patientName,
        line2 = therapiesLabel,
        partsDetail,
        detail = patientName + " ha una terapia (" + therapiesLabel + ") da pianificare.",
        targetView = "pazienti",
        targetId = (int?)therapy.PatientId,
        targetTherapistId = (int?)null,
        targetDate = (string?)null
      }));
    }

    // Alert 1a/1b: Repeated no-shows - total count of PatientAbsent slots
    // (Date <= today, non-Rescheduled) per patient. Exactly 2 -> "contact the
    // patient". 3 or more -> "remove from the calendar" (replaces the 2-count
    // alert entirely, not in addition to it). Key includes lastAbsentDate so a
    // NEW absence (which moves that date) counts as a fresh alert, not the same
    // dismissed one (CPU's call).
    var absentSlots = _db.TherapySlots
      .Where(s => s.Date <= today && s.Date >= pastCutoff && s.Status == TherapySlotStatus.PatientAbsent)
      .ToList();

    var absentSlotsByPatient = absentSlots
      .Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var therapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        return new { Slot = s, PatientId = therapy?.PatientId };
      })
      .Where(x => x.PatientId.HasValue)
      .GroupBy(x => x.PatientId!.Value);

    var doneSlotsByPatient = _db.TherapySlots
      .Where(s => s.Status == TherapySlotStatus.Done)
      .ToList()
      .Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var therapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        return new { Slot = s, PatientId = therapy?.PatientId };
      })
      .Where(x => x.PatientId.HasValue)
      .GroupBy(x => x.PatientId!.Value)
      .ToDictionary(g => g.Key, g => g.Max(x => (DateOnly?)x.Slot.Date));

    foreach (var group in absentSlotsByPatient) {
      var count = group.Count();

      if (count < 2) {
        continue;
      }

      var patientName = patientNames.ContainsKey(group.Key) ? patientNames[group.Key] : "?";
      var patient = patients.ContainsKey(group.Key) ? patients[group.Key] : null;

      var mostRecent = group.Select(x => x.Slot).OrderByDescending(s => s.Date).First();
      var mostRecentPart = partCache.ContainsKey(mostRecent.TherapyPartId) ? partCache[mostRecent.TherapyPartId] : null;
      var mostRecentType = mostRecentPart != null && typeCache.ContainsKey(mostRecentPart.TherapyTypeId) ? typeCache[mostRecentPart.TherapyTypeId] : null;

      var lastPresence = doneSlotsByPatient.ContainsKey(group.Key) ? doneSlotsByPatient[group.Key] : null;

      if (count >= 3) {
        var key = ComputeAlertKey("repeatedNoShow3|" + group.Key + "|" + mostRecent.Date);
        if (!dismissedKeys.Contains(key)) {
          var lastPresenceLabel = lastPresence.HasValue ? lastPresence.Value.ToString("dd/MM/yyyy") : "mai";

          alerts.Add((key, new {
            type = "repeatedNoShow3",
            title = "Paziente da rimuovere",
            icon = "🚫",
            color = "#C62828",
            dismissKey = key.ToString(),
            line1 = patientName,
            line2 = count + " assenze - ultima presenza: " + lastPresenceLabel,
            detail = patientName + " non si è presentato a " + count + " o più sedute (ultima presenza: " + lastPresenceLabel + "). Deve essere rimosso dal calendario.",
            targetView = "pazienti",
            targetId = (int?)group.Key,
            targetTherapistId = (int?)null,
            targetDate = (string?)null,
            patientPhone = patient?.Phone,
            patientSex = patient?.Sex,
            absentCount = count,
            lastPresenceDate = lastPresence.HasValue ? lastPresence.Value.ToString("yyyy-MM-dd") : null
          }));
        }
      } else {
        var key = ComputeAlertKey("repeatedNoShow2|" + group.Key + "|" + mostRecent.Date);
        if (!dismissedKeys.Contains(key)) {
          var absentDates = group.Select(x => x.Slot.Date).OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToList();

          alerts.Add((key, new {
            type = "repeatedNoShow2",
            title = "Contattare paziente assente",
            icon = "📞",
            color = "#F57C00",
            dismissKey = key.ToString(),
            line1 = patientName,
            line2 = count + " assenze",
            detail = patientName + " non si è presentato a 2 sedute. Contattarlo per conferma.",
            targetView = "pazienti",
            targetId = (int?)group.Key,
            targetTherapistId = (int?)null,
            targetDate = (string?)null,
            patientPhone = patient?.Phone,
            patientSex = patient?.Sex,
            absentCount = count,
            absentDates,
            lastAbsentTherapyTypeName = mostRecentType?.Name,
            lastAbsentDate = mostRecent.Date.ToString("yyyy-MM-dd")
          }));
        }
      }
    }

    // Alert: Conflitto per Assenza - a future, still-ToBeDone slot whose assigned
    // therapist is now on vacation/Malattia for that date, or has gone inactive.
    // Key uses the vacation's own StartDate (or the slot's date, for the
    // inactive-therapist case which has no vacation row) so it's tied to the
    // absence itself, not to any one conflicting slot.
    var futureAssignedSlots = _db.TherapySlots
      .Where(s => s.Date >= today && s.Date <= futureCutoff && s.Status == TherapySlotStatus.ToBeDone && s.TherapistId != null)
      .ToList();

    // A patient with recurring sessions during the same vacation would otherwise
    // get one identical card per future slot - dismissedKeys only blocks repeats
    // across requests, not within this one pass, so this needs its own local
    // dedup (CPU: "the system is not working" - 6 duplicate cards for one conflict).
    var emittedVacationConflictKeys = new HashSet<long>();

    foreach (var slot in futureAssignedSlots) {
      var part = partCache.ContainsKey(slot.TherapyPartId) ? partCache[slot.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      var therapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      var therapist = therapistCache.ContainsKey(slot.TherapistId!.Value) ? therapistCache[slot.TherapistId.Value] : null;

      if (therapy == null || therapist == null || type == null) {
        continue;
      }

      var span = GetSpanSlots(type);
      var isInactive = therapist.IsActive == 0;
      var blockingVacation = FindBlockingVacation(therapist.Id, slot.Date, slot.TimeSlot, span);

      if (!isInactive && blockingVacation == null) {
        continue;
      }

      var patientName = patientNames.ContainsKey(therapy.PatientId) ? patientNames[therapy.PatientId] : "?";
      var reason = isInactive ? "terapista disattivato" : "terapista in ferie/malattia";

      var vacStart = blockingVacation?.StartDate;
      var vacEnd = blockingVacation?.EndDate;
      var keyDate = vacStart ?? slot.Date;
      var key = ComputeAlertKey("vacationConflict|" + therapy.PatientId + "|" + therapist.Id + "|" + keyDate);
      if (dismissedKeys.Contains(key) || !emittedVacationConflictKeys.Add(key)) {
        continue;
      }

      var dateLabel = vacStart.HasValue
        ? (vacEnd.HasValue && vacEnd.Value != vacStart.Value
          ? vacStart.Value.ToString("dd/MM/yyyy") + " - " + vacEnd.Value.ToString("dd/MM/yyyy")
          : vacStart.Value.ToString("dd/MM/yyyy"))
        : slot.Date.ToString("dd/MM/yyyy");

      alerts.Add((key, new {
        type = "vacationConflict",
        title = "Conflitto per Assenza",
        icon = "⚠️",
        color = "#7B1FA2",
        dismissKey = key.ToString(),
        line1 = therapist.Name,
        line2 = dateLabel,
        detail = therapist.Name + " è assente (" + reason + ") dal " + dateLabel + ", ma ha una seduta pianificata con " + patientName + " il " + slot.Date.ToString("dd/MM/yyyy") + ".",
        targetView = "settimana",
        targetId = (int?)null,
        targetTherapistId = (int?)therapist.Id,
        targetDate = (string?)slot.Date.ToString("yyyy-MM-dd"),
        conflictPatientId = (int?)therapy.PatientId,
        conflictPatientName = patientName
      }));
    }

    // Alert: Terapia in scadenza - a Scheduled Therapy with few sessions left
    // across all its Parts. Key uses the date of the therapy's last placed slot.
    var renewalThreshold = _settingsCache.Get("AlertForTherapyRenewal", 3);
    if (renewalThreshold <= 0) {
      renewalThreshold = 3;
    }

    var scheduledTherapies = _db.Therapies.AsNoTracking().Where(t => t.Status == TherapyStatus.Scheduled).ToList();
    var allParts = _db.TherapyParts.AsNoTracking().ToList();
    var nonRescheduledSlots = _db.TherapySlots.AsNoTracking().Where(s => s.Status != TherapySlotStatus.Rescheduled).ToList();
    var placedCountByPart = nonRescheduledSlots
      .GroupBy(s => s.TherapyPartId)
      .ToDictionary(g => g.Key, g => g.Count());
    var lastSlotDateByPart = nonRescheduledSlots
      .GroupBy(s => s.TherapyPartId)
      .ToDictionary(g => g.Key, g => g.Max(s => s.Date));

    foreach (var therapy in scheduledTherapies) {
      var parts = allParts.Where(p => p.TherapyId == therapy.Id).ToList();
      if (parts.Count == 0) {
        continue;
      }

      // remaining is computed from the FULL history (accuracy matters here) - only
      // the decision to surface the alert is bounded by recency, so a therapy
      // nobody has touched in months doesn't keep showing up (CPU: "all avvisi
      // should be bounded by dates" / "terapie in scadenza of 4 months ago").
      var remaining = parts.Sum(p => {
        var placed = placedCountByPart.ContainsKey(p.Id) ? placedCountByPart[p.Id] : 0;
        return Math.Max(0, p.SessionCount - placed);
      });

      if (remaining > 0 && remaining <= renewalThreshold) {
        var lastSlotDate = parts
          .Where(p => lastSlotDateByPart.ContainsKey(p.Id))
          .Select(p => lastSlotDateByPart[p.Id])
          .DefaultIfEmpty()
          .Max();

        if (lastSlotDate < pastCutoff) {
          continue;
        }

        var key = ComputeAlertKey("therapyRenewal|" + therapy.PatientId + "|" + lastSlotDate);
        if (dismissedKeys.Contains(key)) {
          continue;
        }

        var patientName = patientNames.ContainsKey(therapy.PatientId) ? patientNames[therapy.PatientId] : "?";
        var lastSlotLabel = lastSlotDate != default ? lastSlotDate.ToString("dd/MM/yyyy") : "n/d";

        alerts.Add((key, new {
          type = "therapyRenewal",
          title = "Terapia in scadenza",
          icon = "⏳",
          color = "#00A97B",
          dismissKey = key.ToString(),
          line1 = patientName,
          line2 = lastSlotLabel,
          detail = "La terapia di " + patientName + " è in scadenza: " + remaining + " sedut" + (remaining == 1 ? "a" : "e") + " rimaste (ultima seduta pianificata: " + lastSlotLabel + ").",
          targetView = "pazienti",
          targetId = (int?)therapy.PatientId,
          targetTherapistId = (int?)null,
          targetDate = (string?)null
        }));
      }
    }

    // Alert: Terapista sotto-pianificato - a run of `threshold` consecutive
    // weekdays, starting today, where the therapist is available but has zero
    // assigned slots. Key uses the run's start date.
    var freeThreshold = _settingsCache.Get("AlertForFreeTherapistDays", 3);
    if (freeThreshold <= 0) {
      freeThreshold = 3;
    }

    // Pure Reparto therapists (no Palestra-helping bit) work a shared pool, not
    // individually-assigned slots - "zero assigned slots for N days" isn't a
    // meaningful signal for them the way it is for everyone else (CPU's call).
    var activeTherapists = _db.Therapists
      .AsNoTracking()
      .Where(t => t.IsActive == 1
        && (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0
        && t.OperatingArea != TherapistOperatingArea.Reparto)
      .ToList();

    var scanEnd = futureCutoff;
    var windowSlots = _db.TherapySlots
      .AsNoTracking()
      .Where(s => s.Date >= today && s.Date <= scanEnd && s.TherapistId != null && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    var availabilityByTherapist = _db.TherapistAvailabilities.AsNoTracking().ToList()
      .GroupBy(a => a.TherapistId)
      .ToDictionary(g => g.Key, g => g.ToList());

    foreach (var therapist in activeTherapists) {
      if (!availabilityByTherapist.ContainsKey(therapist.Id)) {
        continue;
      }

      var avail = availabilityByTherapist[therapist.Id];
      var assignedDates = windowSlots.Where(s => s.TherapistId == therapist.Id).Select(s => s.Date).ToHashSet();

      var consecutiveFree = 0;
      DateOnly? runStart = null;

      for (var d = today; d <= scanEnd; d = d.AddDays(1)) {
        var dow = (int)d.DayOfWeek;
        var availableThatDay = avail.Any(a => a.DayOfWeek == dow);

        if (!availableThatDay) {
          continue; // not a working day for this therapist - doesn't break the streak
        }

        var onVacation = FindBlockingVacation(therapist.Id, d, 0, 1) != null;
        var hasAssignment = assignedDates.Contains(d);

        if (!onVacation && !hasAssignment) {
          if (consecutiveFree == 0) {
            runStart = d;
          }
          consecutiveFree++;

          if (consecutiveFree >= freeThreshold) {
            // Keyed by therapistId alone - runStart is found by scanning forward
            // from "today", so it shifts to a new value every day for what's
            // really the same ongoing situation. A date-based key here meant
            // dismiss/Importante never actually stuck day to day (CPU's bug
            // report). "Under-scheduled" is one continuous condition per
            // therapist, same convention as every other patient/therapist-keyed
            // alert type.
            var key = ComputeAlertKey("therapistUnderScheduled|" + therapist.Id);
            if (!dismissedKeys.Contains(key)) {
              alerts.Add((key, new {
                type = "therapistUnderScheduled",
                title = "Terapista sotto-pianificato",
                icon = "📉",
                color = "#FBC02D",
                dismissKey = key.ToString(),
                line1 = therapist.Name,
                line2 = "A partire dal " + runStart!.Value.ToString("dd/MM"),
                detail = therapist.Name + " non ha sedute pianificate da " + consecutiveFree + " giorni consecutivi, a partire dal " + runStart!.Value.ToString("dd/MM/yyyy") + ".",
                targetView = "settimana",
                targetId = (int?)null,
                targetTherapistId = (int?)therapist.Id,
                targetDate = (string?)runStart!.Value.ToString("yyyy-MM-dd")
              }));
            }
            break;
          }
        } else {
          consecutiveFree = 0;
          runStart = null;
        }
      }
    }

    // Alert: Festività da aggiornare - a non-recurring holiday (e.g. Pasqua) whose
    // date has passed. Key uses the holiday's own row id (stable per occurrence).
    var pastHolidays = _db.Vacations
      .AsNoTracking()
      .Where(v => v.TherapistId == null && v.IsYearIndependent != 1 && v.EndDate != null && v.EndDate < today && v.EndDate >= pastCutoff)
      .ToList();

    foreach (var holiday in pastHolidays) {
      var key = ComputeAlertKey("pastHolidayToUpdate|" + holiday.Id);
      if (dismissedKeys.Contains(key)) {
        continue;
      }

      var dateLabel = holiday.StartDate != holiday.EndDate
        ? holiday.StartDate!.Value.ToString("dd/MM/yyyy") + " - " + holiday.EndDate!.Value.ToString("dd/MM/yyyy")
        : holiday.EndDate!.Value.ToString("dd/MM/yyyy");

      alerts.Add((key, new {
        type = "pastHolidayToUpdate",
        title = "Festività da aggiornare",
        icon = "🗓️",
        color = "#2989AB",
        dismissKey = key.ToString(),
        line1 = holiday.Name ?? "?",
        line2 = (string?)null,
        detail = "La festività '" + (holiday.Name ?? "?") + "' (" + dateLabel + ") è passata e va aggiornata per l'anno prossimo.",
        targetView = "assenze",
        targetId = (int?)holiday.Id,
        targetTherapistId = (int?)null,
        targetDate = (string?)null
      }));
    }

    // Split by the same key used for dismissal - an alert marked "Importante!"
    // moves entirely into its own list instead of appearing in both (CPU: "I
    // prefer to not see it twice"). If the underlying condition resolves on its
    // own, it simply stops being computed above and disappears from here too.
    // Uses the Key tracked alongside each alert (not a dynamic cast on the
    // anonymous object) - that was the actual reason "important" never showed.
    var normalAlerts = new List<object>();
    var importantAlerts = new List<object>();

    foreach (var entry in alerts) {
      if (importantKeys.Contains(entry.Key)) {
        importantAlerts.Add(entry.Data);
      } else {
        normalAlerts.Add(entry.Data);
      }
    }

    return Ok(new { alerts = normalAlerts, importantAlerts });
  }

  // Like IsBlockedByVacation, but returns the actual matching row (so callers can
  // read its real StartDate/EndDate) instead of just a bool.
  private Vacation? FindBlockingVacation(int therapistId, DateOnly date, int timeSlot, int spanSlots) {
    var noonSlot = 52; // 13:00 in 15-min slots from midnight
    var newEnd = timeSlot + spanSlots;

    var vacations = _db.Vacations.AsNoTracking().Where(v => v.TherapistId == therapistId || v.TherapistId == null).ToList();

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
        return v;
      }
    }

    return null;
  }
}
