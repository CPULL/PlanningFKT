using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // "Mese" - a global occupation chart, one cell per weekday of the month (no
  // weekends, per CPU). Not Accettazione-gated - visible to Terapista too [AT].

  private class MeseDayDto {
    public string Date { get; set; } = "";
    public int NumA { get; set; } // planned 15-min slots that day
    public int NumB { get; set; } // max slot capacity that day
  }

  [HttpGet("/Mese/Data")]
  public IActionResult MeseData(int year, int month, int? therapistId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var firstDay = new DateOnly(year, month, 1);
    var lastDay = firstDay.AddMonths(1).AddDays(-1);

    var days = new List<DateOnly>();
    for (var d = firstDay; d <= lastDay; d = d.AddDays(1)) {
      if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) {
        days.Add(d);
      }
    }

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var slotsInRange = GetSlotsInDateRange(days);
    var globalVacations = GetGlobalVacations();

    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();

    // CPU's formula: per 15-min slot, availability = (Reparto-only or Reparto-e-aiuto
    // therapists available that slot) x (15 / RepartoTherapyStartingTime) + (Palestra-
    // only or Palestra-e-aiuto therapists available that slot). "Available" checks both
    // their weekly TherapistAvailability schedule AND that they're not vacation/absence-
    // blocked for that exact date+slot (CPU's call) - reuses IsBlockedByVacation, same
    // check used everywhere else in the app.
    var (repartoRate, _) = GetRepartoRates();
    var repartoMultiplier = 15.0 / repartoRate;

    // NumA: total booked 15-min slot-units that day (any category) - GetSpanSlots
    // already rounds a duration up to at least 1 slot (Ceiling), which covers CPU's
    // "Reparto activities count as 15 slots if they are shorter" rule for free.
    // A slot whose assigned therapist is on vacation that day doesn't count either
    // (CPU: "cannot be considered when evaluating the clinic performance") - same
    // exclusion NumB already applies to that therapist's availability.
    int ComputeNumA(DateOnly date, int? forTherapistId) {
      return slotsInRange
        .Where(s => s.Date == date && s.Status != TherapySlotStatus.Rescheduled && (forTherapistId == null || s.TherapistId == forTherapistId))
        .Sum(s => {
          var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
          var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
          if (type == null) {
            return 0;
          }

          var span = GetSpanSlots(type);
          if (s.TherapistId.HasValue && IsBlockedByVacation(s.TherapistId.Value, date, s.TimeSlot, span)) {
            return 0;
          }
          return span;
        });
    }

    double SlotMultiplierFor(Therapist t) {
      var isReparto = (t.OperatingArea & TherapistOperatingArea.Reparto) != 0;
      return isReparto ? repartoMultiplier : 1.0;
    }

    List<MeseDayDto> result;

    if (therapistId.HasValue) {
      var therapist = _db.Therapists.Find(therapistId.Value);

      if (therapist == null) {
        return NotFound();
      }

      var availability = GetTherapistWeeklyAvailability(therapistId.Value);
      var multiplier = SlotMultiplierFor(therapist);

      result = days.Select(d => {
        var dow = (int)d.DayOfWeek;
        var dayAvailability = availability.ContainsKey(dow) ? availability[dow] : new List<DayAvailabilityRangeDto>();

        double numB = 0;
        for (var slot = clinicStart; slot < clinicEnd; slot++) {
          var isAvailable = dayAvailability.Any(a => slot >= a.StartTime && slot < a.EndTime);
          if (isAvailable && !IsBlockedByVacation(therapistId.Value, d, slot, 1)) {
            numB += multiplier;
          }
        }

        return new MeseDayDto {
          Date = d.ToString("yyyy-MM-dd"),
          NumA = ComputeNumA(d, therapistId.Value),
          NumB = (int)Math.Round(numB)
        };
      }).ToList();
    } else {
      var activeTherapists = _db.Therapists
        .Where(t => t.IsActive == 1 && (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0)
        .ToList();

      var availabilityByTherapist = activeTherapists.ToDictionary(t => t.Id, t => GetTherapistWeeklyAvailability(t.Id));

      result = days.Select(d => {
        var dow = (int)d.DayOfWeek;
        double numB = 0;

        for (var slot = clinicStart; slot < clinicEnd; slot++) {
          foreach (var t in activeTherapists) {
            var dayAvailability = availabilityByTherapist[t.Id].ContainsKey(dow) ? availabilityByTherapist[t.Id][dow] : new List<DayAvailabilityRangeDto>();
            var isAvailable = dayAvailability.Any(a => slot >= a.StartTime && slot < a.EndTime);

            if (isAvailable && !IsBlockedByVacation(t.Id, d, slot, 1)) {
              numB += SlotMultiplierFor(t);
            }
          }
        }

        return new MeseDayDto {
          Date = d.ToString("yyyy-MM-dd"),
          NumA = ComputeNumA(d, null),
          NumB = (int)Math.Round(numB)
        };
      }).ToList();
    }

    return Ok(new { days = result, globalVacations });
  }
}
