using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // "Fogli firma per domani" - three independent lists:
  // 1. Roster: every patient with an ACTIVE therapy (Pianificata, not Privata,
  //    with sessions from today on) - CPU: all active fogli firma, not only the
  //    patients coming on the next working day. The next working day is still
  //    used for the "Prima/Ultima seduta" badges.
  // 2. ToBeCreated: therapies still needing their Foglio Firma set up, bounded
  //    by the therapy's FIRST slot date (not tied to tomorrow at all - CPU's
  //    call, these are independent lists).
  // 3. ToBeFinalized: therapies awaiting Foglio Firma closure, bounded by the
  //    therapy's LAST slot date.
  // Both 2 and 3 are windowed to [today-1 month, today+1 week] so old or
  // far-future therapies don't clutter the list (CPU's call). Neither applies
  // to Privata therapies.
  //
  // PERF: CPU's own tested query design - aggregate MIN/MAX slot date and
  // GROUP_CONCAT the type labels IN SQL (one row per therapy), instead of
  // pulling every individual slot row into the app and aggregating in C#.
  // Two raw queries total: one master aggregate (drives lists 2 and 3, and
  // supplies first/last-date context for list 1), one targeted query for
  // "who has a slot on exactly tomorrow" (drives list 1's membership).

  // KeyValue is a 64-bit hash - sent to the browser as a string (Key), since a JS
  // number can't hold it exactly (same as the Avvisi dismissKey).
  private class FoglioFirmaPatientDto {
    [System.Text.Json.Serialization.JsonIgnore]
    public long KeyValue { get; set; }
    public string Key => KeyValue.ToString();
    public bool Fatto { get; set; }
    public int PatientId { get; set; }
    public string PatientName { get; set; } = "?";
    public string Therapies { get; set; } = "";
    public string Therapists { get; set; } = "";
    public bool IsFirstSession { get; set; }
    public bool IsLastSession { get; set; }
  }

  private class FoglioFirmaTherapyDto {
    [System.Text.Json.Serialization.JsonIgnore]
    public long KeyValue { get; set; }
    public string Key => KeyValue.ToString();
    public bool Fatto { get; set; }
    public int TherapyId { get; set; }
    public int PatientId { get; set; }
    public string PatientName { get; set; } = "?";
    public string Therapies { get; set; } = "";
    public string Therapists { get; set; } = "";
    public string RelevantDate { get; set; } = "";
  }

  private class FoglioFirmaTherapyAggregateRow {
    public int TherapyId { get; set; }
    public int PatientId { get; set; }
    public string PatientName { get; set; } = "?";
    public string TherapyTypes { get; set; } = "";
    public DateOnly MinDate { get; set; }
    public DateOnly MaxDate { get; set; }
    public int FoglioFirmaStatus { get; set; }
    public int BillingCategory { get; set; }
    public int Status { get; set; }
  }

  // Global (therapist-less) vacations only - a therapist's own personal day off
  // doesn't close the clinic for everyone else.
  private bool IsGlobalHoliday(DateOnly date) {
    var globalVacations = _db.Vacations.Where(v => v.TherapistId == null).ToList();

    foreach (var v in globalVacations) {
      if (v.IsYearIndependent == 1 && v.Month.HasValue && v.Day.HasValue) {
        if (date.Month == v.Month.Value && date.Day == v.Day.Value) {
          return true;
        }
      } else if (v.StartDate.HasValue && v.EndDate.HasValue && date >= v.StartDate.Value && date <= v.EndDate.Value) {
        return true;
      }
    }

    return false;
  }

  private DateOnly GetNextWorkingDay(DateOnly from) {
    var d = from.AddDays(1);
    while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday || IsGlobalHoliday(d)) {
      d = d.AddDays(1);
    }
    return d;
  }

  // One row per therapy: true MIN/MAX slot date across ALL of its
  // non-Rescheduled slots (every part, every type - CPU's whole-therapy
  // convention), type labels concatenated, status/category alongside.
  private List<FoglioFirmaTherapyAggregateRow> RunFoglioFirmaAggregateQuery() {
    var results = new List<FoglioFirmaTherapyAggregateRow>();
    var connection = _db.Database.GetDbConnection();
    var shouldClose = connection.State != ConnectionState.Open;
    if (shouldClose) {
      connection.Open();
    }

    try {
      using var command = connection.CreateCommand();
      command.CommandText = @"
        SELECT
          therapies.Id AS therapy_id,
          patients.id AS patient_id,
          patients.name AS patient_name,
          GROUP_CONCAT(DISTINCT therapytypes.name SEPARATOR ', ') AS therapy_types,
          MIN(therapyslots.Date) AS mindate,
          MAX(therapyslots.Date) AS maxdate,
          therapies.FoglioFirmaStatus AS foglio_firma_status,
          therapies.BillingCategory AS billing_category,
          therapies.Status AS therapy_status
        FROM therapies
        INNER JOIN patients ON patients.id = therapies.PatientId
        INNER JOIN therapyparts ON therapyparts.TherapyId = therapies.Id
        INNER JOIN therapytypes ON therapytypes.id = therapyparts.TherapyTypeId
        INNER JOIN therapyslots ON therapyslots.TherapyPartId = therapyparts.id
        WHERE therapyslots.Status <> 3
        GROUP BY therapies.Id";

      using var reader = command.ExecuteReader();
      while (reader.Read()) {
        results.Add(new FoglioFirmaTherapyAggregateRow {
          TherapyId = reader.GetInt32(reader.GetOrdinal("therapy_id")),
          PatientId = reader.GetInt32(reader.GetOrdinal("patient_id")),
          PatientName = reader.IsDBNull(reader.GetOrdinal("patient_name")) ? "?" : reader.GetString(reader.GetOrdinal("patient_name")),
          TherapyTypes = reader.IsDBNull(reader.GetOrdinal("therapy_types")) ? "" : reader.GetString(reader.GetOrdinal("therapy_types")),
          MinDate = DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("mindate"))),
          MaxDate = DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("maxdate"))),
          FoglioFirmaStatus = reader.GetInt32(reader.GetOrdinal("foglio_firma_status")),
          BillingCategory = reader.GetInt32(reader.GetOrdinal("billing_category")),
          Status = reader.GetInt32(reader.GetOrdinal("therapy_status"))
        });
      }
    } finally {
      if (shouldClose) {
        connection.Close();
      }
    }

    return results;
  }

  // Which therapy ids have a non-Rescheduled slot on exactly the given date.
  private HashSet<int> GetTherapyIdsWithSlotOnDate(DateOnly date) {
    var results = new HashSet<int>();
    var connection = _db.Database.GetDbConnection();
    var shouldClose = connection.State != ConnectionState.Open;
    if (shouldClose) {
      connection.Open();
    }

    try {
      using var command = connection.CreateCommand();
      command.CommandText = @"
        SELECT DISTINCT therapyparts.TherapyId AS therapy_id
        FROM therapyslots
        INNER JOIN therapyparts ON therapyparts.id = therapyslots.TherapyPartId
        WHERE therapyslots.Date = @targetDate AND therapyslots.Status <> 3";

      var param = command.CreateParameter();
      param.ParameterName = "@targetDate";
      param.Value = date.ToDateTime(TimeOnly.MinValue);
      command.Parameters.Add(param);

      using var reader = command.ExecuteReader();
      while (reader.Read()) {
        results.Add(reader.GetInt32(reader.GetOrdinal("therapy_id")));
      }
    } finally {
      if (shouldClose) {
        connection.Close();
      }
    }

    return results;
  }

  // CPU: show every therapist who works the therapy on more than 2 different
  // days (a one-off substitution doesn't count). Per therapy id -> names. A
  // therapy whose sessions are all Reparto with no therapist assigned (Light
  // Reparto only) gets just "Reparto".
  private Dictionary<int, List<string>> GetFoglioFirmaTherapists(List<int> therapyIds) {
    var partToTherapy = _db.TherapyParts.AsNoTracking()
      .Where(p => therapyIds.Contains(p.TherapyId))
      .Select(p => new { p.Id, p.TherapyId })
      .ToList()
      .ToDictionary(p => p.Id, p => p.TherapyId);
    var partIds = partToTherapy.Keys.ToList();

    var slots = _db.TherapySlots.AsNoTracking()
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .Select(s => new { s.TherapyPartId, s.TherapistId, s.Date })
      .ToList();

    var therapistNames = _db.Therapists.AsNoTracking().ToDictionary(t => t.Id, t => t.Name);

    return slots
      .GroupBy(s => partToTherapy[s.TherapyPartId])
      .ToDictionary(
        g => g.Key,
        g => g.All(s => s.TherapistId == null)
          ? new List<string> { "Reparto" }
          : g.Where(s => s.TherapistId != null)
            .GroupBy(s => s.TherapistId!.Value)
            .Where(tg => tg.Select(s => s.Date).Distinct().Count() > 2)
            .Select(tg => therapistNames.TryGetValue(tg.Key, out var name) ? name : "?")
            .OrderBy(n => n)
            .ToList());
  }

  private static long FoglioFirmaChecklistKey(int type, int id, DateOnly date) {
    return ComputeAlertKey("foglioFirma|" + type + "|" + id + "|" + date.ToString("yyyy-MM-dd"));
  }

  [HttpGet("/FogliFirma/Domani")]
  public IActionResult FogliFirmaDomani() {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var today = DateOnly.FromDateTime(DateTime.Today);
    var targetDate = GetNextWorkingDay(today);
    var windowStart = today.AddMonths(-1);
    var windowEnd = today.AddDays(7);

    var aggregates = RunFoglioFirmaAggregateQuery();
    var tomorrowTherapyIds = GetTherapyIdsWithSlotOnDate(targetDate);

    // ---- List 1: every active therapy, grouped per patient ------------------
    var activeTherapies = aggregates
      .Where(a => a.Status == TherapyStatus.Scheduled
        && a.BillingCategory != TherapyBillingCategory.Privata
        && a.MaxDate >= today)
      .ToList();

    // ---- List 2: ToBeCreated, windowed by the therapy's FIRST slot date -----
    var toBeCreatedRows = aggregates
      .Where(a => a.FoglioFirmaStatus == FoglioFirmaStatus.ToBeCreated
        && a.BillingCategory != TherapyBillingCategory.Privata
        && a.MinDate >= windowStart && a.MinDate <= windowEnd)
      .ToList();

    // ---- List 3: ToBeFinalized, windowed by the therapy's LAST slot date ----
    var toBeFinalizedRows = aggregates
      .Where(a => a.FoglioFirmaStatus == FoglioFirmaStatus.ToBeFinalized
        && a.BillingCategory != TherapyBillingCategory.Privata
        && a.MaxDate >= windowStart && a.MaxDate <= windowEnd)
      .ToList();

    var therapistsByTherapy = GetFoglioFirmaTherapists(
      activeTherapies.Concat(toBeCreatedRows).Concat(toBeFinalizedRows).Select(a => a.TherapyId).Distinct().ToList());

    string TherapistsLabel(IEnumerable<int> therapyIds) {
      return string.Join(", ", therapyIds
        .SelectMany(id => therapistsByTherapy.TryGetValue(id, out var names) ? names : new List<string>())
        .Distinct()
        .OrderBy(n => n));
    }

    var roster = activeTherapies
      .GroupBy(a => a.PatientId)
      .Select(g => new FoglioFirmaPatientDto {
        KeyValue = FoglioFirmaChecklistKey(FoglioFirmaChecklistType.Roster, g.Key, today),
        PatientId = g.Key,
        PatientName = g.First().PatientName,
        Therapies = string.Join(", ", g.Select(e => e.TherapyTypes).Where(t => !string.IsNullOrEmpty(t)).Distinct()),
        Therapists = TherapistsLabel(g.Select(e => e.TherapyId)),
        IsFirstSession = g.Any(e => e.MinDate == targetDate && tomorrowTherapyIds.Contains(e.TherapyId)),
        IsLastSession = g.Any(e => e.MaxDate == targetDate && tomorrowTherapyIds.Contains(e.TherapyId))
      })
      .ToList();

    FoglioFirmaTherapyDto ToTherapyDto(FoglioFirmaTherapyAggregateRow a, int type, DateOnly relevantDate) {
      return new FoglioFirmaTherapyDto {
        KeyValue = FoglioFirmaChecklistKey(type, a.TherapyId, today),
        TherapyId = a.TherapyId,
        PatientId = a.PatientId,
        PatientName = a.PatientName,
        Therapies = a.TherapyTypes,
        Therapists = TherapistsLabel(new[] { a.TherapyId }),
        RelevantDate = relevantDate.ToString("dd/MM/yyyy")
      };
    }

    var toBeCreated = toBeCreatedRows.Select(a => ToTherapyDto(a, FoglioFirmaChecklistType.DaPreparare, a.MinDate)).ToList();
    var toBeFinalized = toBeFinalizedRows.Select(a => ToTherapyDto(a, FoglioFirmaChecklistType.DaChiudere, a.MaxDate)).ToList();

    // ---- "Fatto" checklist (see FoglioFirmaChecklistItem) -------------------
    // Scoped to today: older rows are dropped, a row is created for every item
    // shown that doesn't have one yet, and each item carries its saved flag - so
    // leaving the page and coming back keeps what was already handled.
    _db.FoglioFirmaChecklistItems.RemoveRange(_db.FoglioFirmaChecklistItems.Where(c => c.MarkedDate != today));
    _db.SaveChanges();

    var existing = _db.FoglioFirmaChecklistItems.ToDictionary(c => c.Key);
    var seen = new HashSet<long>();

    void Track(long key, int type, int patientId) {
      if (!existing.ContainsKey(key) && seen.Add(key)) {
        _db.FoglioFirmaChecklistItems.Add(new FoglioFirmaChecklistItem {
          Key = key,
          Type = type,
          PatientId = patientId,
          MarkedDate = today,
          Fatto = false
        });
      }
    }

    foreach (var r in roster) {
      Track(r.KeyValue, FoglioFirmaChecklistType.Roster, r.PatientId);
      r.Fatto = existing.TryGetValue(r.KeyValue, out var item) && item.Fatto;
    }
    foreach (var t in toBeCreated) {
      Track(t.KeyValue, FoglioFirmaChecklistType.DaPreparare, t.PatientId);
      t.Fatto = existing.TryGetValue(t.KeyValue, out var item) && item.Fatto;
    }
    foreach (var t in toBeFinalized) {
      Track(t.KeyValue, FoglioFirmaChecklistType.DaChiudere, t.PatientId);
      t.Fatto = existing.TryGetValue(t.KeyValue, out var item) && item.Fatto;
    }

    try {
      _db.SaveChanges();
    } catch (DbUpdateException) {
      // Another device opened the page at the same moment and already created
      // the same rows - theirs are identical, nothing to do.
    }

    return Ok(new {
      date = targetDate.ToString("yyyy-MM-dd"),
      patients = roster.OrderBy(r => r.PatientName).ToList(),
      toBeCreated = toBeCreated.OrderBy(r => r.PatientName).ToList(),
      toBeFinalized = toBeFinalized.OrderBy(r => r.PatientName).ToList()
    });
  }

  public class FoglioFirmaToggleRequest {
    public string Key { get; set; } = string.Empty;
    // The new state as shown on screen; omitted = flip the saved one.
    public bool? Fatto { get; set; }
  }

  // Sets one item's "Fatto" flag - the row always exists already (created by
  // FogliFirmaDomani on page load).
  [HttpPost("/FogliFirma/ToggleFatto")]
  public IActionResult FogliFirmaToggleFatto([FromBody] FoglioFirmaToggleRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (!long.TryParse(request.Key, out var key)) {
      return BadRequest();
    }

    var item = _db.FoglioFirmaChecklistItems.Find(key);

    if (item == null) {
      return NotFound();
    }

    item.Fatto = request.Fatto ?? !item.Fatto;
    _db.SaveChanges();

    return Ok(new { fatto = item.Fatto });
  }
}
