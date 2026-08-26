using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  [HttpGet("/Settings/AvailabilityRange")]
  public IActionResult SettingsGetAvailabilityRange() {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    return Ok(new {
      start = GetClinicHoursStart(),
      end = GetClinicHoursEnd()
    });
  }

  public class SettingUpdateRequest {
    public int Value { get; set; }
  }

  [HttpGet("/Settings/List")]
  public IActionResult SettingsList(bool includeAudit = false) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    // Deliberately reads straight from the DB, not the SettingsCache: this admin list
    // needs full row metadata (Id, ModDate, ModUser) that the cache doesn't carry, and
    // it's a low-frequency page, not part of the Giorno/Settimana hot path.
    var rows = _db.Settings.OrderBy(s => s.Key).ToList();

    var modifierNames = includeAudit
      ? _db.Therapists.ToDictionary(t => t.Id, t => t.Name)
      : new Dictionary<int, string>();

    var list = rows.Select(s => new {
      s.Id,
      s.Key,
      s.Value,
      modDate = includeAudit ? s.ModDate : (DateTime?)null,
      modifier = includeAudit && modifierNames.ContainsKey(s.ModUser) ? modifierNames[s.ModUser] : null
    });

    return Ok(list);
  }

  [HttpPut("/Settings/Update/{id}")]
  public IActionResult SettingsUpdate(int id, [FromBody] SettingUpdateRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();

    if (currentUserId == null) {
      return Unauthorized();
    }

    var setting = _db.Settings.Find(id);

    if (setting == null) {
      return NotFound();
    }

    setting.Value = request.Value;
    setting.ModDate = DateTime.Now;
    setting.ModUser = currentUserId.Value;

    _db.SaveChanges();

    // The one and only write path for Settings - refresh the app-wide cache right
    // after so every subsequent request (on this and every other thread) sees the
    // new value immediately, without waiting for a restart.
    _settingsCache.Reload();

    return Ok();
  }
}
