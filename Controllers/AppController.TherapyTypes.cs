using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class TherapyTypeSaveRequest {
    public string Name { get; set; } = string.Empty;
    public string? Abbreviazione { get; set; }
    public int Duration { get; set; }
    public int Category { get; set; }
    public int Type { get; set; }
    public int Color { get; set; }
    public int? MaxParallelMale { get; set; }
    public int? MaxParallelFemale { get; set; }
    public int TherapyWeeklyFrequency { get; set; }
  }

  public class TherapyTypeRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  [HttpGet("/TherapyTypes/List")]
  public IActionResult TherapyTypesList(bool includeRemoved = false, bool includeAudit = false) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var query = _db.TherapyTypes.AsQueryable();

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
      t.Abbreviazione,
      t.Duration,
      category = TherapyCategory.ToLabel(t.Category),
      type = TherapyExecutionType.ToLabel(t.Type),
      t.Color,
      t.MaxParallelMale,
      t.MaxParallelFemale,
      t.TherapyWeeklyFrequency,
      allowsGinnasticaAttiva = t.AllowsGinnasticaAttiva != 0,
      isActive = t.IsActive == 1,
      modDate = includeAudit ? t.ModDate : (DateTime?)null,
      modifier = includeAudit && modifierNames.ContainsKey(t.ModUser) ? modifierNames[t.ModUser] : null
    });

    return Ok(list);
  }

  [HttpGet("/TherapyTypes/Get/{id}")]
  public IActionResult TherapyTypesGet(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapyType = _db.TherapyTypes.Find(id);

    if (therapyType == null) {
      return NotFound();
    }

    return Ok(new {
      therapyType.Id,
      therapyType.Name,
      therapyType.Abbreviazione,
      therapyType.Duration,
      therapyType.Category,
      therapyType.Type,
      therapyType.Color,
      therapyType.MaxParallelMale,
      therapyType.MaxParallelFemale,
      therapyType.TherapyWeeklyFrequency,
      therapyType.IsActive
    });
  }

  [HttpPost("/TherapyTypes/Create")]
  public IActionResult TherapyTypesCreate([FromBody] TherapyTypeSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var therapyType = new TherapyType {
      Name = request.Name,
      Abbreviazione = request.Abbreviazione,
      Duration = request.Duration,
      Category = request.Category,
      Type = request.Type,
      Color = request.Color,
      MaxParallelMale = request.MaxParallelMale,
      MaxParallelFemale = request.MaxParallelFemale,
      TherapyWeeklyFrequency = request.TherapyWeeklyFrequency,
      IsActive = 1,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    _db.TherapyTypes.Add(therapyType);
    _db.SaveChanges();

    return Ok(new { therapyType.Id });
  }

  [HttpPut("/TherapyTypes/Update/{id}")]
  public IActionResult TherapyTypesUpdate(int id, [FromBody] TherapyTypeSaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var therapyType = _db.TherapyTypes.Find(id);

    if (therapyType == null) {
      return NotFound();
    }

    therapyType.Name = request.Name;
    therapyType.Abbreviazione = request.Abbreviazione;
    therapyType.Duration = request.Duration;
    therapyType.Category = request.Category;
    therapyType.Type = request.Type;
    therapyType.Color = request.Color;
    therapyType.MaxParallelMale = request.MaxParallelMale;
    therapyType.MaxParallelFemale = request.MaxParallelFemale;
    therapyType.TherapyWeeklyFrequency = request.TherapyWeeklyFrequency;
    therapyType.ModDate = DateTime.Now;
    therapyType.ModUser = currentUserId.Value;

    _db.SaveChanges();

    return Ok();
  }

  [HttpPost("/TherapyTypes/Remove/{id}")]
  public IActionResult TherapyTypesRemove(int id, [FromBody] TherapyTypeRemoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapyType = _db.TherapyTypes.Find(id);

    if (therapyType == null) {
      return NotFound();
    }

    if (!string.Equals(therapyType.Name, request.ConfirmName, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest(new { error = "name_mismatch" });
    }

    var isUsed = _db.TherapyParts.Any(p => p.TherapyTypeId == id)
      || _db.TherapyPacketItems.Any(i => i.TherapyTypeId == id);

    if (isUsed) {
      therapyType.IsActive = 0;
      var currentUserId = GetCurrentTherapistId();
      therapyType.ModDate = DateTime.Now;
      therapyType.ModUser = currentUserId ?? therapyType.ModUser;
      _db.SaveChanges();
    } else {
      _db.TherapyTypes.Remove(therapyType);
      _db.SaveChanges();
    }

    return Ok();
  }
}
