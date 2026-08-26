using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class VacationSaveRequest {
    // "assenza" | "festivitaFissa" | "festivitaVariabile"
    public string Tipo { get; set; } = string.Empty;
    public int? TherapistId { get; set; }
    public string? Name { get; set; }
    public int? AMPM { get; set; }
    public int? Malattia { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
  }

  public class VacationRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  [HttpGet("/Vacations/List")]
  public IActionResult VacationsList() {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var today = DateOnly.FromDateTime(DateTime.Now);
    var therapistNames = _db.Therapists.ToDictionary(t => t.Id, t => t.Name);

    var list = _db.Vacations
      .ToList()
      .Select(v => {
        DateOnly? sortDate;

        if (v.IsYearIndependent == 1 && v.Month.HasValue && v.Day.HasValue) {
          var candidate = new DateOnly(today.Year, v.Month.Value, v.Day.Value);
          sortDate = candidate < today ? candidate.AddYears(1) : candidate;
        } else {
          sortDate = v.StartDate;
        }

        return new {
          v.Id,
          v.TherapistId,
          therapistName = v.TherapistId.HasValue && therapistNames.ContainsKey(v.TherapistId.Value)
            ? therapistNames[v.TherapistId.Value]
            : null,
          v.Name,
          v.AMPM,
          v.Malattia,
          v.IsYearIndependent,
          v.Month,
          v.Day,
          v.StartDate,
          v.EndDate,
          v.IsSeeded,
          sortDate
        };
      })
      .OrderBy(v => v.sortDate)
      .ToList();

    return Ok(list);
  }

  [HttpGet("/Vacations/Get/{id}")]
  public IActionResult VacationsGet(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var vacation = _db.Vacations.Find(id);

    if (vacation == null) {
      return NotFound();
    }

    return Ok(new {
      vacation.Id,
      vacation.TherapistId,
      vacation.Name,
      vacation.AMPM,
      vacation.Malattia,
      vacation.IsYearIndependent,
      vacation.Month,
      vacation.Day,
      vacation.StartDate,
      vacation.EndDate,
      vacation.IsSeeded
    });
  }

  private void ApplyVacationRequest(Vacation vacation, VacationSaveRequest request) {
    // Full reset every save - only the fields for the chosen Tipo get repopulated,
    // so switching Tipo on an existing row cleanly drops the previous shape's data.
    vacation.TherapistId = null;
    vacation.Name = null;
    vacation.AMPM = null;
    vacation.Malattia = null;
    vacation.IsYearIndependent = null;
    vacation.Month = null;
    vacation.Day = null;
    vacation.StartDate = null;
    vacation.EndDate = null;

    if (request.Tipo == "assenza") {
      vacation.TherapistId = request.TherapistId;
      vacation.StartDate = request.StartDate;
      // A blank end date always means "single day": normalize to equal StartDate so
      // every downstream consumer (overlay, list display) only needs one comparison
      // (StartDate == EndDate) rather than special-casing a null EndDate as well.
      vacation.EndDate = request.EndDate ?? request.StartDate;
      vacation.AMPM = request.AMPM;
      vacation.Malattia = request.Malattia;
    } else if (request.Tipo == "festivitaFissa") {
      vacation.Name = request.Name;
      vacation.IsYearIndependent = 1;
      vacation.Month = request.Month;
      vacation.Day = request.Day;
    } else if (request.Tipo == "festivitaVariabile") {
      vacation.Name = request.Name;
      vacation.IsYearIndependent = 0;
      vacation.StartDate = request.StartDate;
      vacation.EndDate = request.EndDate;
    }
  }

  [HttpPost("/Vacations/Create")]
  public IActionResult VacationsCreate([FromBody] VacationSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var vacation = new Vacation { IsSeeded = 0 };
    ApplyVacationRequest(vacation, request);

    _db.Vacations.Add(vacation);
    _db.SaveChanges();

    return Ok(new { vacation.Id });
  }

  [HttpPut("/Vacations/Update/{id}")]
  public IActionResult VacationsUpdate(int id, [FromBody] VacationSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var vacation = _db.Vacations.Find(id);

    if (vacation == null) {
      return NotFound();
    }

    if (vacation.IsSeeded == 1) {
      return BadRequest(new { message = "Le festività predefinite non possono essere modificate." });
    }

    ApplyVacationRequest(vacation, request);
    _db.SaveChanges();

    return Ok();
  }

  [HttpPost("/Vacations/Remove/{id}")]
  public IActionResult VacationsRemove(int id, [FromBody] VacationRemoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var vacation = _db.Vacations.Find(id);

    if (vacation == null) {
      return NotFound();
    }

    if (vacation.IsSeeded == 1) {
      return BadRequest(new { message = "Le festività predefinite non possono essere eliminate." });
    }

    var expectedName = vacation.TherapistId.HasValue
      ? _db.Therapists.Find(vacation.TherapistId.Value)?.Name
      : vacation.Name;

    if (!string.Equals(request.ConfirmName, expectedName, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest();
    }

    _db.Vacations.Remove(vacation);
    _db.SaveChanges();

    return Ok();
  }
}
