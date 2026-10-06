using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // ---- Fatturato (CPU) ---------------------------------------------------------
  //
  // A forecast, not accounting: what the clinic expects to earn and what it has
  // earned, public (Convenzione, invoiced to the ASL) vs private (Privata and
  // Assicurazione - CPU: marginal, treated like private).
  // A therapy's value comes from, in order:
  //   1. its PR codes: sum of their costs, spread evenly over the therapy's sessions;
  //   2. its Pacchetto: the pacchetto's final price, spread the same way;
  //   3. otherwise the Tariffario: each session is worth its type's unit price.
  // Per month: planned = sessions Da fare, executed = sessions Fatto (Assente is
  // lost money, moved originals don't count). A therapy closed early simply
  // earns less. Prices are always the current ones (past months recalculated).

  private const int FatturatoBudgetWarningPercent = 95;

  private class FatturatoTotals {
    public decimal ExecutedPublic { get; set; }
    public decimal PlannedPublic { get; set; }
    public decimal ExecutedPrivate { get; set; }
    public decimal PlannedPrivate { get; set; }
    public decimal Total => ExecutedPublic + PlannedPublic + ExecutedPrivate + PlannedPrivate;
    public decimal PublicTotal => ExecutedPublic + PlannedPublic;
  }

  // Value of one session of each part, keyed by part id.
  private Dictionary<int, decimal> BuildSessionValueByPart(List<Therapy> therapies) {
    var therapyIds = therapies.Select(t => t.Id).ToList();
    var parts = _db.TherapyParts.AsNoTracking().Where(p => therapyIds.Contains(p.TherapyId)).ToList();
    var prLinks = _db.TherapyPrCodes.AsNoTracking().Where(l => therapyIds.Contains(l.TherapyId)).ToList();
    var prCosts = _db.PrCodes.AsNoTracking().ToDictionary(c => c.Id, c => c.Cost);
    var packetIds = therapies.Where(t => t.TherapyPacketId.HasValue).Select(t => t.TherapyPacketId!.Value).Distinct().ToList();
    var packetPrices = _db.TherapyPackets.AsNoTracking().Where(p => packetIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.TotalPrice);
    var unitPrices = _db.TherapyPacketConfigs.AsNoTracking().ToList()
      .GroupBy(c => c.TherapyTypeId)
      .ToDictionary(g => g.Key, g => g.First().UnitPrice);

    var result = new Dictionary<int, decimal>();

    foreach (var therapy in therapies) {
      var therapyParts = parts.Where(p => p.TherapyId == therapy.Id).ToList();
      var sessions = therapyParts.Sum(p => p.SessionCount);
      var prTotal = prLinks.Where(l => l.TherapyId == therapy.Id).Sum(l => prCosts.GetValueOrDefault(l.PrCodeId));

      decimal? perSession = null;
      if (prTotal > 0 && sessions > 0) {
        perSession = prTotal / sessions;
      } else if (therapy.TherapyPacketId.HasValue && packetPrices.ContainsKey(therapy.TherapyPacketId.Value) && sessions > 0) {
        perSession = packetPrices[therapy.TherapyPacketId.Value] / sessions;
      }

      foreach (var part in therapyParts) {
        result[part.Id] = perSession ?? unitPrices.GetValueOrDefault(part.TherapyTypeId);
      }
    }

    return result;
  }

  // Totals per (year, month) for every month touched by [from, to].
  private Dictionary<(int Year, int Month), FatturatoTotals> ComputeFatturato(DateOnly from, DateOnly to) {
    var slots = _db.TherapySlots.AsNoTracking()
      .Where(s => s.Date >= from && s.Date <= to
        && (s.Status == TherapySlotStatus.ToBeDone || s.Status == TherapySlotStatus.Done))
      .Select(s => new { s.TherapyPartId, s.Date, s.Status })
      .ToList();

    var partIds = slots.Select(s => s.TherapyPartId).Distinct().ToList();
    var partToTherapy = _db.TherapyParts.AsNoTracking()
      .Where(p => partIds.Contains(p.Id))
      .Select(p => new { p.Id, p.TherapyId })
      .ToList()
      .ToDictionary(p => p.Id, p => p.TherapyId);
    var therapyIds = partToTherapy.Values.Distinct().ToList();
    var therapies = _db.Therapies.AsNoTracking()
      .Where(t => therapyIds.Contains(t.Id) && t.Status != TherapyStatus.Cancelled)
      .ToList();
    var therapyById = therapies.ToDictionary(t => t.Id, t => t);
    var valueByPart = BuildSessionValueByPart(therapies);

    var result = new Dictionary<(int Year, int Month), FatturatoTotals>();

    foreach (var slot in slots) {
      if (!partToTherapy.TryGetValue(slot.TherapyPartId, out var therapyId) || !therapyById.TryGetValue(therapyId, out var therapy)) {
        continue;
      }

      var key = (slot.Date.Year, slot.Date.Month);
      if (!result.TryGetValue(key, out var totals)) {
        totals = new FatturatoTotals();
        result[key] = totals;
      }

      var value = valueByPart.GetValueOrDefault(slot.TherapyPartId);
      var isPublic = therapy.BillingCategory == TherapyBillingCategory.Convenzione;
      var isDone = slot.Status == TherapySlotStatus.Done;

      if (isPublic && isDone) {
        totals.ExecutedPublic += value;
      } else if (isPublic) {
        totals.PlannedPublic += value;
      } else if (isDone) {
        totals.ExecutedPrivate += value;
      } else {
        totals.PlannedPrivate += value;
      }
    }

    return result;
  }

  private FatturatoTotals FatturatoForMonth(int year, int month) {
    var from = new DateOnly(year, month, 1);
    var to = from.AddMonths(1).AddDays(-1);
    return ComputeFatturato(from, to).GetValueOrDefault((year, month)) ?? new FatturatoTotals();
  }

  private int GetFatturatoBudget() {
    return _settingsCache.Get("FatturatoMassimoASL", 0);
  }

  private static readonly string[] ItalianMonthNames = {
    "Gennaio", "Febbraio", "Marzo", "Aprile", "Maggio", "Giugno",
    "Luglio", "Agosto", "Settembre", "Ottobre", "Novembre", "Dicembre"
  };

  // Budget figures for one month (null when no budget is set).
  private object? BuildBudgetInfo(int year, int month) {
    var budget = GetFatturatoBudget();
    if (budget <= 0) {
      return null;
    }

    var used = FatturatoForMonth(year, month).PublicTotal;
    var remaining = budget - used;

    return new {
      year,
      month,
      monthLabel = ItalianMonthNames[month - 1] + " " + year,
      budget,
      used = Math.Round(used, 2),
      remaining = Math.Round(remaining, 2),
      remainingPercent = Math.Round(remaining * 100m / budget, 0)
    };
  }

  // Warnings for the "95% of Fatturato Massimo ASL" rule, one per month reached
  // (CPU: alert the operator adding more Convenzione therapies).
  private List<string> GetBudgetWarnings(IEnumerable<DateOnly> dates) {
    var warnings = new List<string>();
    var budget = GetFatturatoBudget();
    if (budget <= 0) {
      return warnings;
    }

    foreach (var key in dates.Select(d => (d.Year, d.Month)).Distinct().OrderBy(k => k.Year).ThenBy(k => k.Month)) {
      var used = FatturatoForMonth(key.Year, key.Month).PublicTotal;
      var usedPercent = used * 100m / budget;

      if (usedPercent >= FatturatoBudgetWarningPercent) {
        warnings.Add("Attenzione: il Fatturato ASL di " + ItalianMonthNames[key.Month - 1] + " " + key.Year
          + " è al " + Math.Round(usedPercent, 0) + "% del massimo (" + budget.ToString("N0", new System.Globalization.CultureInfo("it-IT")) + " €).");
      }
    }

    return warnings;
  }

  // Budget warnings after placing sessions of a therapy - only for Convenzione.
  private List<string> GetBudgetWarningsForTherapy(Therapy therapy, IEnumerable<DateOnly> dates) {
    return therapy.BillingCategory == TherapyBillingCategory.Convenzione
      ? GetBudgetWarnings(dates)
      : new List<string>();
  }

  [HttpGet("/Fatturato/Data")]
  public IActionResult FatturatoData(int year, int month) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (month < 1 || month > 12 || year < 2000 || year > 2100) {
      return BadRequest(new { message = "Mese non valido." });
    }

    var all = ComputeFatturato(new DateOnly(year - 1, 1, 1), new DateOnly(year, 12, 31));
    var current = all.GetValueOrDefault((year, month)) ?? new FatturatoTotals();

    var months = Enumerable.Range(1, 12).Select(m => {
      var totals = all.GetValueOrDefault((year, m));
      var previous = all.GetValueOrDefault((year - 1, m));
      var total = totals?.Total ?? 0;
      var previousTotal = previous?.Total ?? 0;
      return new {
        month = m,
        total = Math.Round(total, 2),
        hasPrevious = previous != null && previousTotal != 0,
        difference = Math.Round(total - previousTotal, 2)
      };
    }).ToList();

    return Ok(new {
      year,
      month,
      executedPrivate = Math.Round(current.ExecutedPrivate, 2),
      plannedPrivate = Math.Round(current.PlannedPrivate, 2),
      executedPublic = Math.Round(current.ExecutedPublic, 2),
      plannedPublic = Math.Round(current.PlannedPublic, 2),
      months,
      budget = BuildBudgetInfo(year, month)
    });
  }

  // Budget label on the patient page (CPU): only for patients with at least one
  // Convenzione therapy; the month of that therapy's first session, or the
  // current month when it isn't planned yet.
  [HttpGet("/Fatturato/PatientBudget/{patientId}")]
  public IActionResult FatturatoPatientBudget(int patientId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = GetCurrentTherapies(patientId)
      .FirstOrDefault(t => t.BillingCategory == TherapyBillingCategory.Convenzione);

    if (therapy == null) {
      return Ok(new { budget = (object?)null });
    }

    var partIds = _db.TherapyParts.Where(p => p.TherapyId == therapy.Id).Select(p => p.Id).ToList();
    var firstDate = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .OrderBy(s => s.Date)
      .Select(s => (DateOnly?)s.Date)
      .FirstOrDefault();
    var date = firstDate ?? DateOnly.FromDateTime(DateTime.Today);

    return Ok(new { budget = BuildBudgetInfo(date.Year, date.Month) });
  }

  // ---- PR codes (Fatturato -> "Codici PR" tab) ----------------------------------

  [HttpGet("/PrCodes/List")]
  public IActionResult PrCodesList() {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var codes = _db.PrCodes.AsNoTracking().ToList()
      .OrderBy(c => PrCodeSortKey(c.Code))
      .Select(c => new { c.Id, c.Code, c.Description, c.Cost })
      .ToList();

    return Ok(codes);
  }

  // "PR4A" < "PR10" < "PR31G": number first, then the letter suffix.
  private static (int, string) PrCodeSortKey(string code) {
    var digits = new string(code.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(char.IsDigit).ToArray());
    var number = int.TryParse(digits, out var n) ? n : int.MaxValue;
    var suffix = code.Length > 0 ? code.Substring(code.IndexOf(digits, StringComparison.Ordinal) + digits.Length) : "";
    return (number, suffix);
  }

  public class PrCodeUpdateRequest {
    public decimal Cost { get; set; }
  }

  [HttpPut("/PrCodes/Update/{id}")]
  public IActionResult PrCodesUpdate(int id, [FromBody] PrCodeUpdateRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();
    if (currentUserId == null) {
      return Unauthorized();
    }

    var code = _db.PrCodes.Find(id);
    if (code == null) {
      return NotFound();
    }

    if (request.Cost < 0) {
      return BadRequest(new { message = "Il costo non può essere negativo." });
    }

    code.Cost = Math.Round(request.Cost, 2);
    code.ModDate = DateTime.Now;
    code.ModUser = currentUserId.Value;
    _db.SaveChanges();

    return Ok(new { code.Cost });
  }

  // The patient's own pacchetti, for the therapy form's pacchetto dropdown.
  [HttpGet("/Pacchetti/ForPatient/{patientId}")]
  public IActionResult PacchettiForPatient(int patientId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var packets = _db.TherapyPackets.AsNoTracking()
      .Where(p => p.PatientId == patientId)
      .OrderByDescending(p => p.CreatedAt)
      .Select(p => new { p.Id, p.Name, p.TotalPrice, p.CreatedAt })
      .ToList();

    return Ok(packets);
  }
}
