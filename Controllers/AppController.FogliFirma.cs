using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // "Fogli firma per domani" - three independent lists:
  // 1. Roster: everyone with a non-Rescheduled slot on the next WORKING day
  //    (skips weekends and any global holiday).
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

  private class FoglioFirmaPatientDto {
    public int PatientId { get; set; }
    public string PatientName { get; set; } = "?";
    public string Therapies { get; set; } = "";
    public bool IsFirstSession { get; set; }
    public bool IsLastSession { get; set; }
  }

  private class FoglioFirmaTherapyDto {
    public int PatientId { get; set; }
    public string PatientName { get; set; } = "?";
    public string Therapies { get; set; } = "";
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
          therapies.BillingCategory AS billing_category
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
          BillingCategory = reader.GetInt32(reader.GetOrdinal("billing_category"))
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
    var aggregateByTherapyId = aggregates.ToDictionary(a => a.TherapyId, a => a);
    var tomorrowTherapyIds = GetTherapyIdsWithSlotOnDate(targetDate);

    // ---- List 1: roster for the next working day ----------------------------
    var roster = new List<FoglioFirmaPatientDto>();
    var rosterByPatient = new Dictionary<int, List<FoglioFirmaTherapyAggregateRow>>();

    foreach (var therapyId in tomorrowTherapyIds) {
      if (!aggregateByTherapyId.TryGetValue(therapyId, out var agg)) {
        continue;
      }
      if (!rosterByPatient.ContainsKey(agg.PatientId)) {
        rosterByPatient[agg.PatientId] = new List<FoglioFirmaTherapyAggregateRow>();
      }
      rosterByPatient[agg.PatientId].Add(agg);
    }

    foreach (var kv in rosterByPatient) {
      var entries = kv.Value;
      var therapyLabels = entries.Select(e => e.TherapyTypes).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();

      roster.Add(new FoglioFirmaPatientDto {
        PatientId = kv.Key,
        PatientName = entries[0].PatientName,
        Therapies = string.Join(", ", therapyLabels),
        IsFirstSession = entries.Any(e => e.MinDate == targetDate),
        IsLastSession = entries.Any(e => e.MaxDate == targetDate)
      });
    }

    // ---- List 2: ToBeCreated, windowed by the therapy's FIRST slot date -----
    var toBeCreated = aggregates
      .Where(a => a.FoglioFirmaStatus == FoglioFirmaStatus.ToBeCreated
        && a.BillingCategory != TherapyBillingCategory.Privata
        && a.MinDate >= windowStart && a.MinDate <= windowEnd)
      .Select(a => new FoglioFirmaTherapyDto {
        PatientId = a.PatientId,
        PatientName = a.PatientName,
        Therapies = a.TherapyTypes,
        RelevantDate = a.MinDate.ToString("dd/MM/yyyy")
      })
      .ToList();

    // ---- List 3: ToBeFinalized, windowed by the therapy's LAST slot date ----
    var toBeFinalized = aggregates
      .Where(a => a.FoglioFirmaStatus == FoglioFirmaStatus.ToBeFinalized
        && a.BillingCategory != TherapyBillingCategory.Privata
        && a.MaxDate >= windowStart && a.MaxDate <= windowEnd)
      .Select(a => new FoglioFirmaTherapyDto {
        PatientId = a.PatientId,
        PatientName = a.PatientName,
        Therapies = a.TherapyTypes,
        RelevantDate = a.MaxDate.ToString("dd/MM/yyyy")
      })
      .ToList();

    return Ok(new {
      date = targetDate.ToString("yyyy-MM-dd"),
      patients = roster.OrderBy(r => r.PatientName).ToList(),
      toBeCreated = toBeCreated.OrderBy(r => r.PatientName).ToList(),
      toBeFinalized = toBeFinalized.OrderBy(r => r.PatientName).ToList()
    });
  }
}
