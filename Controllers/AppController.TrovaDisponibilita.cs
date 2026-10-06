using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // ---- "Trova disponibilità" (CPU) ----------------------------------------------
  //
  // Patient page -> for a therapy defined but not started (renewals excluded),
  // finds the first start day within a month from today where EVERY session of
  // every part fits with the chosen cadence (Continuo / Giorni alterni /
  // Settimanale), inside the chosen time range, with the usual planning rules:
  // - Palestra parts back to back with one therapist (the preferred one, or the
  //   first free one), Reparto-Active parts after them with a Reparto therapist,
  //   Reparto-Light parts right before or after (least crowded side), exactly
  //   like Pianifica Mixed; a Reparto-only therapy goes at the least crowded time;
  // - the same time every session, at most 2 sessions shifted by 15 minutes;
  // - days the therapist doesn't work / is absent, and holidays, are skipped
  //   (like Pianifica's automatic modes), anything else busy means no fit.
  // The result is handed to the normal planning screen as drafts - nothing is
  // saved until "Accetta pianificazione" there.

  private const int TrovaSearchDays = 30;
  private const int TrovaMaxShifts = 2;
  private static readonly TimeSpan TrovaTimeLimit = TimeSpan.FromSeconds(25);

  public class TrovaDisponibilitaRequest {
    public int TherapyId { get; set; }
    // "continuo" | "alterni" | "settimanale"
    public string Cadence { get; set; } = "continuo";
    // "" (Nessuna) | "inizioMattina" | "mattina" | "fineMattina" | "inizioPomeriggio" | "pomeriggio" | "finePomeriggio"
    public string? TimePreference { get; set; }
    public int? TherapistId { get; set; }
  }

  private (int Start, int End) GetTrovaTimeRange(string? preference) {
    return preference switch {
      "inizioMattina" => (32, 40),    // 8-10
      "mattina" => (32, 52),          // 8-13
      "fineMattina" => (44, 52),      // 11-13
      "inizioPomeriggio" => (56, 64), // 14-16
      "pomeriggio" => (56, 72),       // 14-18
      "finePomeriggio" => (64, 72),   // 16-18
      _ => (GetClinicHoursStart(), GetClinicHoursEnd())
    };
  }

  private static string TrovaModeFor(string cadence) {
    return cadence switch {
      "alterni" => "giorniAlterni",
      "settimanale" => "settimanalmente",
      _ => "tuttiIGiorni"
    };
  }

  // Not started (no session at all) and not a renewal running next to another
  // therapy of the patient that already has sessions.
  private List<Therapy> GetTrovaEligibleTherapies(int patientId) {
    var current = GetCurrentTherapies(patientId);
    var withSlots = current
      .Where(t => {
        var partIds = _db.TherapyParts.Where(p => p.TherapyId == t.Id).Select(p => p.Id).ToList();
        return _db.TherapySlots.Any(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled);
      })
      .Select(t => t.Id)
      .ToHashSet();

    if (withSlots.Count > 0) {
      return new List<Therapy>();
    }

    return current.Where(t => t.Status == TherapyStatus.ToBeScheduled).ToList();
  }

  [HttpGet("/TrovaDisponibilita/Options/{patientId}")]
  public IActionResult TrovaDisponibilitaOptions(int patientId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var patient = _db.Patients.Find(patientId);
    if (patient == null) {
      return NotFound();
    }

    var typeCache = GetTypeCache();
    var therapies = GetTrovaEligibleTherapies(patientId).Select(t => {
      var parts = _db.TherapyParts.Where(p => p.TherapyId == t.Id).OrderBy(p => p.Id).ToList();
      return new {
        id = t.Id,
        label = (string.IsNullOrEmpty(t.Name) ? "" : t.Name + ": ") + string.Join(", ", parts.Select(p =>
          p.SessionCount + " × " + (typeCache.ContainsKey(p.TherapyTypeId) ? typeCache[p.TherapyTypeId].Name : "?"))),
        hasPalestra = parts.Any(p => typeCache.ContainsKey(p.TherapyTypeId) && typeCache[p.TherapyTypeId].Category == TherapyCategory.Palestra)
      };
    }).ToList();

    var therapists = GetPalestraDropdownTherapists().Select(t => new { id = t.Id, name = t.Name }).ToList();

    return Ok(new { patientName = patient.Name, therapies, therapists });
  }

  // ---- search internals --------------------------------------------------------

  private enum TrovaOutcome { Ok, Skip, Busy }

  private class TrovaSegment {
    public TherapyPart Part = null!;
    public TherapyType Type = null!;
    public int StartSlot;
    public int Span;      // real length incl. Isocinetica extra (checks)
    public int BaseSpan;  // type length (drafts)
    public int? TherapistId;
    public string TherapistName = "Reparto";
  }

  private class TrovaOccurrence {
    public DateOnly Date;
    public List<TrovaSegment> Segments = new();
    public int RepartoCount;
  }

  private Dictionary<DateOnly, bool>? _trovaHolidayCache;

  // Every therapist's booked ranges per day, loaded once for the whole search.
  private Dictionary<(int TherapistId, DateOnly Date), List<(int Start, int End)>>? _trovaOccupancy;

  private void LoadTrovaOccupancy(DateOnly from, DateOnly to) {
    var typeCache = GetTypeCache();
    var partCache = GetPartCache();

    _trovaOccupancy = _db.TherapySlots.AsNoTracking()
      .Where(s => s.Date >= from && s.Date <= to && s.TherapistId != null && s.Status != TherapySlotStatus.Rescheduled)
      .ToList()
      .GroupBy(s => (s.TherapistId!.Value, s.Date))
      .ToDictionary(g => g.Key, g => g.Select(s => {
        var part = partCache.GetValueOrDefault(s.TherapyPartId);
        var type = part != null ? typeCache.GetValueOrDefault(part.TherapyTypeId) : null;
        return (s.TimeSlot, s.TimeSlot + GetSlotSpan(s, type));
      }).ToList());
  }

  private bool IsTrovaTherapistBusy(int therapistId, DateOnly date, int start, int span) {
    if (_trovaOccupancy == null || !_trovaOccupancy.TryGetValue((therapistId, date), out var ranges)) {
      return false;
    }
    return ranges.Any(r => start < r.End && start + span > r.Start);
  }

  private TrovaOutcome TryTrovaOccurrence(Therapy therapy, List<(TherapyPart Part, TherapyType Type)> parts,
      Dictionary<int, int> remaining, bool isIsoRenewal, DateOnly date, int x, int? palestraTherapistId,
      (int Start, int End) range, List<Therapist> orderedRepartoTherapists, int repartoRate, int coveringRate,
      List<Therapist> relevantTherapists, out TrovaOccurrence? occurrence) {
    occurrence = null;
    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();

    if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) {
      return TrovaOutcome.Skip;
    }

    // Holiday for everyone (memoized - checked for every candidate).
    _trovaHolidayCache ??= new Dictionary<DateOnly, bool>();
    if (!_trovaHolidayCache.TryGetValue(date, out var isHoliday)) {
      isHoliday = FindBlockingVacation(0, date, 0, 96) != null;
      _trovaHolidayCache[date] = isHoliday;
    }
    if (isHoliday) {
      return TrovaOutcome.Skip;
    }

    TrovaSegment MakeSegment((TherapyPart Part, TherapyType Type) p, int start) {
      var sessionNumber = p.Part.SessionCount - remaining[p.Part.Id] + 1;
      var baseSpan = GetSpanSlots(p.Type);
      return new TrovaSegment {
        Part = p.Part, Type = p.Type, StartSlot = start, BaseSpan = baseSpan,
        Span = baseSpan + IsocineticaExtraFor(p.Part, sessionNumber, isIsoRenewal)
      };
    }

    var active = parts.Where(p => remaining[p.Part.Id] > 0).ToList();
    var heavyParts = active.Where(p => IsHeavyPart(p.Type)).ToList();
    var lightParts = active.Where(p => IsLightPart(p.Type)).ToList();
    var segments = new List<TrovaSegment>();

    // Heavy block back to back from x, part order (as in Pianifica Mixed).
    var cursor = x;
    foreach (var p in heavyParts) {
      var seg = MakeSegment(p, cursor);
      segments.Add(seg);
      cursor += seg.Span;
    }
    var heavyEnd = cursor;

    if (heavyParts.Count > 0 && (x < range.Start || heavyEnd > range.End)) {
      return TrovaOutcome.Busy;
    }

    // Palestra: one therapist for every Palestra part.
    foreach (var seg in segments.Where(s => s.Type.Category == TherapyCategory.Palestra)) {
      if (!palestraTherapistId.HasValue) {
        return TrovaOutcome.Busy;
      }
      if (IsBlockedByVacation(palestraTherapistId.Value, date, seg.StartSlot, seg.Span)
          || !IsWithinAvailability(palestraTherapistId.Value, date, seg.StartSlot, seg.Span)) {
        return TrovaOutcome.Skip;
      }
      if (IsTrovaTherapistBusy(palestraTherapistId.Value, date, seg.StartSlot, seg.Span)) {
        return TrovaOutcome.Busy;
      }
      seg.TherapistId = palestraTherapistId.Value;
      seg.TherapistName = _db.Therapists.Find(palestraTherapistId.Value)?.Name ?? "?";
    }

    // Reparto-Active: best-fit Reparto therapist (same helper as Pianifica Mixed).
    var repartoActive = segments.Where(s => s.Type.Category == TherapyCategory.Reparto).ToList();
    if (repartoActive.Count > 0) {
      var plans = repartoActive.Select(s => new HeavySegmentPlan { Part = s.Part, Type = s.Type, Span = s.Span, StartSlot = s.StartSlot }).ToList();
      if (!AssignRepartoActiveTherapists(plans, date, null, orderedRepartoTherapists, out _)) {
        return TrovaOutcome.Busy;
      }
      for (var i = 0; i < repartoActive.Count; i++) {
        repartoActive[i].TherapistId = plans[i].TherapistId;
        repartoActive[i].TherapistName = plans[i].TherapistName;
      }
    }

    // Reparto-Light: right before or after the heavy block (least crowded side),
    // or at x itself for a Light-only therapy.
    var lightSpan = lightParts.Sum(p => GetSpanSlots(p.Type));
    var repartoCount = 0;

    if (lightSpan > 0) {
      int CountWindow(int start) => Enumerable.Range(start, lightSpan).Sum(s => CountAllRepartoSessions(date, s));
      bool PatientClash(int start) {
        var c = start;
        foreach (var p in lightParts) {
          var span = GetSpanSlots(p.Type);
          if (HasPatientConflict(therapy.PatientId, date, c, span, p.Part.TherapyTypeId)) {
            return true;
          }
          c += span;
        }
        return false;
      }
      bool WindowFree(int start) => start >= clinicStart && start + lightSpan <= clinicEnd
        && DescribeRepartoSaturation(date, start, lightSpan, repartoRate, coveringRate, relevantTherapists) == null
        && !PatientClash(start);

      int? lightStart = null;

      if (heavyParts.Count == 0) {
        if (x < range.Start || x + lightSpan > range.End || !WindowFree(x)) {
          return TrovaOutcome.Busy;
        }
        lightStart = x;
      } else {
        var before = x - lightSpan;
        var after = heavyEnd;
        var beforeOk = WindowFree(before);
        var afterOk = WindowFree(after);

        if (!beforeOk && !afterOk) {
          return TrovaOutcome.Busy;
        }

        lightStart = beforeOk && afterOk
          ? (CountWindow(before) < CountWindow(after) ? before : after)
          : (beforeOk ? before : after);
      }

      repartoCount = CountWindow(lightStart.Value);
      var lightCursor = lightStart.Value;
      foreach (var p in lightParts) {
        var seg = MakeSegment(p, lightCursor);
        segments.Add(seg);
        lightCursor += seg.BaseSpan;
      }
    }

    // The patient must be free for every heavy session too.
    foreach (var seg in segments.Where(s => IsHeavyPart(s.Type))) {
      if (HasPatientConflict(therapy.PatientId, date, seg.StartSlot, seg.Span, seg.Part.TherapyTypeId)) {
        return TrovaOutcome.Busy;
      }
    }

    occurrence = new TrovaOccurrence { Date = date, Segments = segments, RepartoCount = repartoCount };
    return TrovaOutcome.Ok;
  }

  // The whole plan from (start, x, therapist), or null.
  private List<TrovaOccurrence>? TryTrovaPlan(Therapy therapy, List<(TherapyPart Part, TherapyType Type)> parts,
      bool isIsoRenewal, string mode, DateOnly start, int x, int? palestraTherapistId, (int Start, int End) range,
      List<Therapist> orderedRepartoTherapists, int repartoRate, int coveringRate, List<Therapist> relevantTherapists) {
    var remaining = parts.ToDictionary(p => p.Part.Id, p => p.Part.SessionCount);
    var plan = new List<TrovaOccurrence>();
    var shifts = 0;
    var totalSessions = remaining.Values.Sum();
    var candidates = GenerateCandidateDates(mode, start, Math.Min(1000, totalSessions * 10 + 50));

    foreach (var date in candidates) {
      if (remaining.Values.All(r => r <= 0)) {
        break;
      }

      var isFirst = plan.Count == 0;
      var outcome = TryTrovaOccurrence(therapy, parts, remaining, isIsoRenewal, date, x, palestraTherapistId, range,
        orderedRepartoTherapists, repartoRate, coveringRate, relevantTherapists, out var occurrence);

      if (isFirst && (date != start || outcome != TrovaOutcome.Ok)) {
        return null; // the plan must start exactly there, at that time
      }

      if (outcome == TrovaOutcome.Skip) {
        continue;
      }

      if (outcome == TrovaOutcome.Busy) {
        // Same time 90% of the time: at most 2 sessions 15 minutes earlier/later.
        if (shifts >= TrovaMaxShifts) {
          return null;
        }

        foreach (var shifted in new[] { x - 1, x + 1 }) {
          var shiftedOutcome = TryTrovaOccurrence(therapy, parts, remaining, isIsoRenewal, date, shifted, palestraTherapistId, range,
            orderedRepartoTherapists, repartoRate, coveringRate, relevantTherapists, out occurrence);
          if (shiftedOutcome == TrovaOutcome.Ok) {
            break;
          }
          occurrence = null;
        }

        if (occurrence == null) {
          return null;
        }
        shifts++;
      }

      plan.Add(occurrence!);
      foreach (var seg in occurrence!.Segments) {
        remaining[seg.Part.Id]--;
      }
    }

    return remaining.Values.All(r => r <= 0) ? plan : null;
  }

  [HttpPost("/TrovaDisponibilita/Search")]
  public IActionResult TrovaDisponibilitaSearch([FromBody] TrovaDisponibilitaRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(request.TherapyId);
    if (therapy == null) {
      return NotFound();
    }

    if (!GetTrovaEligibleTherapies(therapy.PatientId).Any(t => t.Id == therapy.Id)) {
      return BadRequest(new { message = "La terapia è già iniziata: usa Pianifica." });
    }

    var typeCache = GetTypeCache();
    var parts = _db.TherapyParts.Where(p => p.TherapyId == therapy.Id).OrderBy(p => p.Id).ToList()
      .Where(p => typeCache.ContainsKey(p.TherapyTypeId) && p.SessionCount > 0)
      .Select(p => (Part: p, Type: typeCache[p.TherapyTypeId]))
      .ToList();

    if (parts.Count == 0) {
      return BadRequest(new { message = "La terapia non ha sedute da pianificare." });
    }

    var hasPalestra = parts.Any(p => p.Type.Category == TherapyCategory.Palestra);
    var range = GetTrovaTimeRange(request.TimePreference);
    var mode = TrovaModeFor(request.Cadence);
    var isIsoRenewal = parts.Any(p => p.Part.TherapyTypeId == IsocineticaTherapyTypeId) && IsIsocineticaRenewal(therapy);

    var palestraTherapists = !hasPalestra
      ? new List<int?> { null }
      : request.TherapistId.HasValue
        ? new List<int?> { request.TherapistId.Value }
        : GetPalestraDropdownTherapists().Select(t => (int?)t.Id).ToList();

    var orderedRepartoTherapists = GetOrderedRepartoTherapists();
    var (repartoRate, coveringRate) = GetRepartoRates();
    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();

    var today = DateOnly.FromDateTime(DateTime.Today);
    LoadTrovaOccupancy(today, today.AddDays(400));

    var watch = System.Diagnostics.Stopwatch.StartNew();
    List<TrovaOccurrence>? found = null;

    for (var start = today.AddDays(1); start <= today.AddDays(TrovaSearchDays) && found == null; start = start.AddDays(1)) {
      if (start.DayOfWeek == DayOfWeek.Saturday || start.DayOfWeek == DayOfWeek.Sunday) {
        continue;
      }

      if (!hasPalestra) {
        // Reparto only: the least crowded time that day where everything fits.
        List<TrovaOccurrence>? best = null;
        for (var x = range.Start; x < range.End; x++) {
          var plan = TryTrovaPlan(therapy, parts, isIsoRenewal, mode, start, x, null, range,
            orderedRepartoTherapists, repartoRate, coveringRate, relevantTherapists);
          if (plan != null && (best == null || plan[0].RepartoCount < best[0].RepartoCount)) {
            best = plan;
          }
          if (watch.Elapsed > TrovaTimeLimit) {
            break;
          }
        }
        found = best;
      } else {
        for (var x = range.Start; x < range.End && found == null; x++) {
          foreach (var therapistId in palestraTherapists) {
            found = TryTrovaPlan(therapy, parts, isIsoRenewal, mode, start, x, therapistId, range,
              orderedRepartoTherapists, repartoRate, coveringRate, relevantTherapists);
            if (found != null) {
              break;
            }
          }
          if (watch.Elapsed > TrovaTimeLimit) {
            break;
          }
        }
      }

      if (watch.Elapsed > TrovaTimeLimit && found == null) {
        return BadRequest(new { message = "Nessuna disponibilità trovata (ricerca interrotta: prova a restringere le preferenze)." });
      }
    }

    if (found == null) {
      return BadRequest(new { message = "Nessuna disponibilità trovata" });
    }

    var slots = found.SelectMany(o => o.Segments.Select(s => new {
      partId = s.Part.Id,
      therapyTypeId = s.Part.TherapyTypeId,
      therapyTypeName = s.Type.Name,
      category = s.Type.Category,
      executionType = s.Type.Type,
      date = o.Date.ToString("yyyy-MM-dd"),
      timeSlot = s.StartSlot,
      durationSlots = s.BaseSpan,
      therapistId = s.TherapistId,
      therapistName = s.TherapistName
    })).ToList();

    var partsInfo = parts.Select(p => new {
      id = p.Part.Id,
      therapyTypeCategory = p.Type.Category,
      therapyTypeExecutionType = p.Type.Type
    }).ToList();

    return Ok(new { therapyId = therapy.Id, slots, parts = partsInfo });
  }
}
