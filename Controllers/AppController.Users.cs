using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class UserSaveRequest {
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public int OperatingArea { get; set; }
    public int OvertimeAllowed { get; set; }
    public string? Password { get; set; }
    public List<AvailabilitySlotRequest> Availability { get; set; } = new();
  }

  public class AvailabilitySlotRequest {
    public int DayOfWeek { get; set; }
    public int StartTime { get; set; }
    public int EndTime { get; set; }
    public int? OverrideOperatingArea { get; set; }
  }

  public class UserRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  [HttpGet("/Users/List")]
  public IActionResult UsersList(bool includeRemoved = false, bool includeAudit = false) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var query = _db.Therapists.Where(t => t.Id != 1);

    if (!includeRemoved) {
      query = query.Where(t => t.IsActive == 1);
    }

    var rows = query.OrderBy(t => t.Name).ToList();

    var modifierNames = includeAudit
      ? _db.Therapists.ToDictionary(t => t.Id, t => t.Name)
      : new Dictionary<int, string>();

    var list = rows.Select(t => new {
      t.Id,
      t.Name,
      t.Phone,
      role = TherapistOperatingArea.ToRoleLabel(t.OperatingArea),
      isActive = t.IsActive == 1,
      modDate = includeAudit ? t.ModDate : (DateTime?)null,
      modifier = includeAudit && modifierNames.ContainsKey(t.ModUser) ? modifierNames[t.ModUser] : null
    });

    return Ok(list);
  }

  [HttpGet("/Users/Get/{id}")]
  public IActionResult UsersGet(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (id == 1) {
      return NotFound();
    }

    var therapist = _db.Therapists.Find(id);

    if (therapist == null) {
      return NotFound();
    }

    var availability = _db.TherapistAvailabilities
      .Where(a => a.TherapistId == id)
      .OrderBy(a => a.DayOfWeek).ThenBy(a => a.StartTime)
      .Select(a => new { a.DayOfWeek, a.StartTime, a.EndTime, a.OverrideOperatingArea })
      .ToList();

    return Ok(new {
      therapist.Id,
      therapist.Name,
      therapist.Phone,
      therapist.OperatingArea,
      therapist.OvertimeAllowed,
      therapist.IsActive,
      availability
    });
  }

  [HttpPost("/Users/Create")]
  public IActionResult UsersCreate([FromBody] UserSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var therapist = new Therapist {
      Name = request.Name,
      Phone = request.Phone,
      IsActive = 1,
      OperatingArea = request.OperatingArea,
      OvertimeAllowed = request.OvertimeAllowed,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    if (!string.IsNullOrEmpty(request.Password)) {
      var hasher = new PasswordHasher<Therapist>();
      therapist.PasswordHash = hasher.HashPassword(therapist, request.Password);
    }

    _db.Therapists.Add(therapist);
    _db.SaveChanges();

    var isTerapista = (therapist.OperatingArea & TherapistOperatingArea.Accettazione) == 0;

    if (isTerapista) {
      foreach (var slot in request.Availability) {
        _db.TherapistAvailabilities.Add(new TherapistAvailability {
          TherapistId = therapist.Id,
          DayOfWeek = slot.DayOfWeek,
          StartTime = slot.StartTime,
          EndTime = slot.EndTime,
          OverrideOperatingArea = slot.OverrideOperatingArea
        });
      }

      _db.SaveChanges();
    }

    return Ok(new { therapist.Id });
  }

  [HttpPut("/Users/Update/{id}")]
  public IActionResult UsersUpdate(int id, [FromBody] UserSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (id == 1) {
      return NotFound();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var therapist = _db.Therapists.Find(id);

    if (therapist == null) {
      return NotFound();
    }

    therapist.Name = request.Name;
    therapist.Phone = request.Phone;
    therapist.OperatingArea = request.OperatingArea;
    therapist.OvertimeAllowed = request.OvertimeAllowed;
    therapist.ModDate = DateTime.Now;
    therapist.ModUser = currentUserId.Value;

    if (!string.IsNullOrEmpty(request.Password)) {
      var hasher = new PasswordHasher<Therapist>();
      therapist.PasswordHash = hasher.HashPassword(therapist, request.Password);
    }

    var isTerapista = (therapist.OperatingArea & TherapistOperatingArea.Accettazione) == 0;

    var existingAvailability = _db.TherapistAvailabilities.Where(a => a.TherapistId == id);
    _db.TherapistAvailabilities.RemoveRange(existingAvailability);

    if (isTerapista) {
      foreach (var slot in request.Availability) {
        _db.TherapistAvailabilities.Add(new TherapistAvailability {
          TherapistId = id,
          DayOfWeek = slot.DayOfWeek,
          StartTime = slot.StartTime,
          EndTime = slot.EndTime,
          OverrideOperatingArea = slot.OverrideOperatingArea
        });
      }
    }

    _db.SaveChanges();

    return Ok();
  }

  [HttpPost("/Users/Remove/{id}")]
  public IActionResult UsersRemove(int id, [FromBody] UserRemoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (id == 1) {
      return NotFound();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var therapist = _db.Therapists.Find(id);

    if (therapist == null) {
      return NotFound();
    }

    if (!string.Equals(therapist.Name, request.ConfirmName, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest(new { error = "name_mismatch" });
    }

    var isUsed = _db.TherapySlots.Any(s => s.TherapistId == id)
      || _db.TherapistAvailabilities.Any(a => a.TherapistId == id)
      || _db.Vacations.Any(v => v.TherapistId == id);

    if (isUsed) {
      therapist.IsActive = 0;
      therapist.ModDate = DateTime.Now;
      therapist.ModUser = currentUserId.Value;
      _db.SaveChanges();
    } else {
      _db.Therapists.Remove(therapist);
      _db.SaveChanges();
    }

    return Ok();
  }
}
