using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // "Gestisci assenza" - same-day urgent tool: a therapist calls in unable to
  // come, and staff need the full list of that therapist's patients today (name +
  // phone) to call and inform them, with a quick reschedule shortcut. Deliberately
  // ignores slot status entirely (CPU's call: nothing has happened yet for the day
  // by the time this runs) and TODAY only - no date picker, since a future-day
  // absence goes through the normal Assenze/vacation flow instead.

  public class GestisciAssenzaReportRequest {
    public int TherapistId { get; set; }
  }

  public class GestisciAssenzaMarkCalledRequest {
    public int TherapistId { get; set; }
    public int PatientId { get; set; }
    public bool Called { get; set; }
  }

  private void PruneGestisciAssenzaTable() {
    var cutoff = DateTime.Now.AddHours(-24);
    _db.TherapistAbsenceCalls.RemoveRange(_db.TherapistAbsenceCalls.Where(r => r.CreatedAt < cutoff));
    _db.SaveChanges();
  }

  // A therapist's own set of today's TherapyPart -> Patient slots, grouped by
  // patient - shared by Report (to seed rows) and List (to build the display).
  // Deliberately includes Rescheduled-status rows (unlike GetSlotsInDateRange) -
  // a quick Ripianifica from this page marks the original row Rescheduled without
  // moving its Date, and the patient should still show up here afterward with a
  // "Ripianificato al ..." label instead of vanishing from the list.
  private Dictionary<int, List<TherapySlot>> GetTodaysSlotsByPatientForTherapist(int therapistId) {
    var today = DateOnly.FromDateTime(DateTime.Today);
    var partCache = GetPartCache();
    var therapyCache = GetTherapyCache();

    var slots = _db.TherapySlots
      .Where(s => s.Date == today && s.TherapistId == therapistId)
      .ToList();

    var byPatient = new Dictionary<int, List<TherapySlot>>();
    foreach (var s in slots) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var therapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      if (therapy == null) {
        continue;
      }
      if (!byPatient.ContainsKey(therapy.PatientId)) {
        byPatient[therapy.PatientId] = new List<TherapySlot>();
      }
      byPatient[therapy.PatientId].Add(s);
    }

    return byPatient;
  }

  [HttpPost("/GestisciAssenza/Report")]
  public IActionResult GestisciAssenzaReport([FromBody] GestisciAssenzaReportRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapist = _db.Therapists.Find(request.TherapistId);
    if (therapist == null) {
      return NotFound();
    }

    PruneGestisciAssenzaTable();

    var today = DateOnly.FromDateTime(DateTime.Today);

    // Covers the whole day (span 96 slots) - a partial (AM/PM-only) vacation still
    // counts as "already handled" for this same-day tool (CPU's call: simpler than
    // splitting on AMPM here too).
    var existingVacation = FindBlockingVacation(request.TherapistId, today, 0, 96);

    // Seed one row per patient this therapist has today (Called defaults to
    // false) - a therapist "is in today's list" simply by having any row here, so
    // this both records the report and populates the call list in one step.
    var byPatient = GetTodaysSlotsByPatientForTherapist(request.TherapistId);
    var existingPatientIds = _db.TherapistAbsenceCalls
      .Where(r => r.TherapistId == request.TherapistId)
      .Select(r => r.PatientId)
      .ToHashSet();

    foreach (var patientId in byPatient.Keys) {
      if (!existingPatientIds.Contains(patientId)) {
        _db.TherapistAbsenceCalls.Add(new TherapistAbsenceCall {
          TherapistId = request.TherapistId,
          PatientId = patientId,
          CreatedAt = DateTime.Now,
          Called = false
        });
      }
    }
    _db.SaveChanges();

    if (existingVacation != null) {
      return Ok(new { alreadyAbsent = true, message = therapist.Name + " è già marcato come assente per oggi!" });
    }

    _db.Vacations.Add(new Vacation {
      TherapistId = request.TherapistId,
      AMPM = null, // full day
      Malattia = 0,
      StartDate = today,
      EndDate = today,
      IsSeeded = 0
    });
    _db.SaveChanges();

    return Ok(new { alreadyAbsent = false, message = (string?)null });
  }

  [HttpGet("/GestisciAssenza/List")]
  public IActionResult GestisciAssenzaList() {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    PruneGestisciAssenzaTable();

    var therapistCache = _db.Therapists.ToDictionary(t => t.Id, t => t);
    var patientCache = GetPatientEntityCache();
    var partCache = GetPartCache();
    var typeCache = GetTypeCache();

    var rows = _db.TherapistAbsenceCalls.ToList();
    var therapistIds = rows.Select(r => r.TherapistId).Distinct().ToList();

    var result = therapistIds.Select(tid => {
      var therapist = therapistCache.ContainsKey(tid) ? therapistCache[tid] : null;
      var byPatient = GetTodaysSlotsByPatientForTherapist(tid);
      var callRowsForTherapist = rows.Where(r => r.TherapistId == tid).ToDictionary(r => r.PatientId, r => r);

      var patients = callRowsForTherapist.Keys.Select(patientId => {
        var patient = patientCache.ContainsKey(patientId) ? patientCache[patientId] : null;
        var patientSlots = byPatient.ContainsKey(patientId) ? byPatient[patientId] : new List<TherapySlot>();

        var slotDtos = patientSlots.OrderBy(s => s.TimeSlot).Select(s => {
          var part = partCache[s.TherapyPartId];
          var type = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
          var label = type != null ? (string.IsNullOrEmpty(type.Abbreviazione) ? type.Name : type.Abbreviazione) : "?";

          string? rescheduledToDate = null;
          if (s.Status == TherapySlotStatus.Rescheduled && s.RescheduledToId.HasValue) {
            var newSlot = _db.TherapySlots.Find(s.RescheduledToId.Value);
            rescheduledToDate = newSlot?.Date.ToString("yyyy-MM-dd");
          }

          return new {
            slotId = s.Id,
            timeSlot = s.TimeSlot,
            therapyTypeLabel = label,
            rescheduledToDate
          };
        }).ToList();

        return new {
          patientId,
          patientName = patient?.Name ?? "?",
          phone = patient?.Phone,
          called = callRowsForTherapist[patientId].Called,
          slots = slotDtos
        };
      }).OrderBy(p => p.patientName).ToList();

      return new {
        therapistId = tid,
        therapistName = therapist?.Name ?? "?",
        patients
      };
    }).OrderBy(t => t.therapistName).ToList();

    return Ok(result);
  }

  [HttpPost("/GestisciAssenza/MarkCalled")]
  public IActionResult GestisciAssenzaMarkCalled([FromBody] GestisciAssenzaMarkCalledRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var row = _db.TherapistAbsenceCalls
      .FirstOrDefault(r => r.TherapistId == request.TherapistId && r.PatientId == request.PatientId);

    if (row == null) {
      return NotFound();
    }

    row.Called = request.Called;
    _db.SaveChanges();

    return Ok();
  }
}
