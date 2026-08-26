using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class TherapyPartRequest {
    public int TherapyTypeId { get; set; }
    public int SessionCount { get; set; }
  }

  public class TherapySaveRequest {
    public int PatientId { get; set; }
    public string? Name { get; set; }
    public List<TherapyPartRequest> Parts { get; set; } = new();
  }

  public class TherapyRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  public class TherapyCancelFutureSlotsRequest {
    public string ConfirmName { get; set; } = string.Empty;
    public DateOnly? FromDate { get; set; }
  }

  private static bool IsValidTherapyPartsList(List<TherapyPartRequest>? parts) {
    return parts != null && parts.Count > 0 && parts.All(p => p.SessionCount >= 1);
  }

  [HttpPost("/Therapies/Create")]
  public IActionResult TherapiesCreate([FromBody] TherapySaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var patient = _db.Patients.Find(request.PatientId);

    if (patient == null) {
      return NotFound();
    }

    // Only one active Therapy per patient at a time (CONFIRMED, final) - checked
    // server-side too, not just via the disabled button in the UI.
    var hasActive = _db.Therapies.Any(t =>
      t.PatientId == request.PatientId &&
      t.Status != TherapyStatus.Completed &&
      t.Status != TherapyStatus.Cancelled);

    if (hasActive) {
      return BadRequest(new { message = "Il paziente ha già una terapia in corso." });
    }

    if (!IsValidTherapyPartsList(request.Parts)) {
      return BadRequest(new { message = "È necessaria almeno una parte, con almeno una seduta ciascuna." });
    }

    var therapy = new Therapy {
      Name = request.Name,
      Status = TherapyStatus.ToBeScheduled,
      PatientId = request.PatientId,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    _db.Therapies.Add(therapy);
    _db.SaveChanges();

    foreach (var part in request.Parts) {
      _db.TherapyParts.Add(new TherapyPart {
        TherapyId = therapy.Id,
        TherapyTypeId = part.TherapyTypeId,
        SessionCount = part.SessionCount,
        ModDate = DateTime.Now,
        ModUser = currentUserId.Value
      });
    }

    _db.SaveChanges();

    return Ok(new { therapy.Id });
  }

  [HttpPut("/Therapies/Update/{id}")]
  public IActionResult TherapiesUpdate(int id, [FromBody] TherapySaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var therapy = _db.Therapies.Find(id);

    if (therapy == null) {
      return NotFound();
    }

    // Locked once anything has actually been scheduled - direct edits only make
    // sense while nothing is placed on the calendar yet.
    if (therapy.Status != TherapyStatus.ToBeScheduled) {
      return BadRequest(new { message = "La terapia è già pianificata e non può più essere modificata direttamente." });
    }

    if (!IsValidTherapyPartsList(request.Parts)) {
      return BadRequest(new { message = "È necessaria almeno una parte, con almeno una seduta ciascuna." });
    }

    therapy.Name = request.Name;
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = currentUserId.Value;

    // Full replace, same pattern as TherapistAvailability - simplest correct approach
    // since Parts have no independent identity worth preserving across an edit.
    var existingParts = _db.TherapyParts.Where(p => p.TherapyId == id);
    _db.TherapyParts.RemoveRange(existingParts);

    foreach (var part in request.Parts) {
      _db.TherapyParts.Add(new TherapyPart {
        TherapyId = id,
        TherapyTypeId = part.TherapyTypeId,
        SessionCount = part.SessionCount,
        ModDate = DateTime.Now,
        ModUser = currentUserId.Value
      });
    }

    _db.SaveChanges();

    return Ok();
  }

  [HttpPost("/Therapies/Remove/{id}")]
  public IActionResult TherapiesRemove(int id, [FromBody] TherapyRemoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(id);

    if (therapy == null) {
      return NotFound();
    }

    var patient = _db.Patients.Find(therapy.PatientId);
    var expectedName = patient?.Name;

    if (!string.Equals(request.ConfirmName, expectedName, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest();
    }

    // Hard delete, cascades to TherapyParts and (via the TherapyPart -> TherapySlot
    // cascade configured in AppDbContext) every TherapySlot tied to them, past and
    // future alike - confirmed acceptable (CPU: no soft-delete/Cancelled status
    // needed here), the strong type-the-name confirmation above is the safeguard.
    var parts = _db.TherapyParts.Where(p => p.TherapyId == id);
    _db.TherapyParts.RemoveRange(parts);
    _db.Therapies.Remove(therapy);
    _db.SaveChanges();

    return Ok();
  }

  // "Cancella tutte le sedute future" (Slot interaction, universal across calendar
  // views) - hard-deletes only the FUTURE, not-yet-occurred TherapySlot rows for this
  // Therapy; past ones (Done/PatientAbsent/historical) stay untouched. No soft-delete/
  // Cancelled status involved (CPU's call) - the strong type-the-name confirmation is
  // the safeguard, same pattern as TherapiesRemove.
  [HttpPost("/Therapies/{id}/CancelFutureSlots")]
  public IActionResult TherapiesCancelFutureSlots(int id, [FromBody] TherapyCancelFutureSlotsRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(id);

    if (therapy == null) {
      return NotFound();
    }

    var patient = _db.Patients.Find(therapy.PatientId);
    var expectedName = patient?.Name;

    if (!string.Equals(request.ConfirmName, expectedName, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest();
    }

    // Filters from the clicked slot's own date, not "today" - a slot further in the
    // past-relative-to-now but still future-relative-to-the-therapy shouldn't be
    // spared just because today's date moved on. Falls back to today only if the
    // caller didn't send one (shouldn't happen from the UI, but keeps this safe).
    var fromDate = request.FromDate ?? DateOnly.FromDateTime(DateTime.Today);
    var partIds = _db.TherapyParts.Where(p => p.TherapyId == id).Select(p => p.Id).ToList();

    var futureSlots = _db.TherapySlots.Where(s => partIds.Contains(s.TherapyPartId) && s.Date >= fromDate).ToList();
    _db.TherapySlots.RemoveRange(futureSlots);
    _db.SaveChanges();

    return Ok(new { removedCount = futureSlots.Count });
  }
}
