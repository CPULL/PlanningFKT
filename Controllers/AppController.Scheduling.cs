using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // Unlike Users/List and Users/Get (Accettazione-only, used for user management),
  // these two are for the Giorno/Settimana scheduling views, which both roles use -
  // so they only rely on the controller's base [Authorize], not IsCurrentUserAccettazione().

  [HttpGet("/Users/TherapistsForScheduling")]
  public IActionResult UsersTherapistsForScheduling() {
    var rows = _db.Therapists
      .Where(t => t.Id != 1 && t.IsActive == 1 && (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0)
      .OrderBy(t => t.Name)
      .Select(t => new { t.Id, t.Name })
      .ToList();

    return Ok(rows);
  }

  [HttpGet("/Users/AvailabilityFor/{id}")]
  public IActionResult UsersAvailabilityFor(int id) {
    var therapist = _db.Therapists.Find(id);

    if (therapist == null || (therapist.OperatingArea & TherapistOperatingArea.Accettazione) != 0) {
      return NotFound();
    }

    var availability = _db.TherapistAvailabilities
      .Where(a => a.TherapistId == id)
      .OrderBy(a => a.DayOfWeek).ThenBy(a => a.StartTime)
      .Select(a => new { a.DayOfWeek, a.StartTime, a.EndTime })
      .ToList();

    return Ok(new { therapist.Id, therapist.Name, availability });
  }

  [HttpGet("/Users/VacationsFor/{id}")]
  public IActionResult UsersVacationsFor(int id) {
    var rows = _db.Vacations
      .Where(v => v.TherapistId == id || v.TherapistId == null)
      .Select(v => new {
        v.Id,
        v.TherapistId,
        v.Name,
        v.AMPM,
        v.Malattia,
        v.IsYearIndependent,
        v.Month,
        v.Day,
        v.StartDate,
        v.EndDate
      })
      .ToList();

    return Ok(rows);
  }
}
