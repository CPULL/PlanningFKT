using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class TherapyPartRequest {
    // Existing part being edited (Modifica) - null for a new part.
    public int? Id { get; set; }
    public int TherapyTypeId { get; set; }
    public int SessionCount { get; set; }
    public bool IncludeGinnasticaAttiva { get; set; }
  }

  public class TherapySaveRequest {
    public int PatientId { get; set; }
    public string? Name { get; set; }
    public int BillingCategory { get; set; }
    public List<TherapyPartRequest> Parts { get; set; } = new();
    // "Rinnova terapia": the new therapy is a renewal, planned in parallel with
    // the previous one - skips the one-active-therapy-per-patient rule.
    public bool IsRenewal { get; set; }
    // Modifica: required only when the edit deletes planned sessions.
    public string? ConfirmName { get; set; }
    // Pricing (CPU): PR codes for Convenzione, a pacchetto of the patient for
    // Privata/Assicurazione - whichever doesn't apply is ignored.
    public List<int>? PrCodeIds { get; set; }
    public int? TherapyPacketId { get; set; }
  }

  private const int MaxPrCodesPerTherapy = 5;

  // Pricing source check, no side effects - null when fine.
  private string? ValidateTherapyPricing(Therapy therapy, TherapySaveRequest request) {
    if (therapy.BillingCategory == TherapyBillingCategory.Convenzione) {
      var ids = (request.PrCodeIds ?? new List<int>()).Distinct().ToList();
      if (ids.Count > MaxPrCodesPerTherapy) {
        return "Al massimo " + MaxPrCodesPerTherapy + " codici PR per terapia.";
      }
      if (ids.Any(id => _db.PrCodes.Find(id) == null)) {
        return "Codice PR non valido.";
      }
      return null;
    }

    if (request.TherapyPacketId.HasValue) {
      var packet = _db.TherapyPackets.Find(request.TherapyPacketId.Value);
      if (packet == null || packet.PatientId != therapy.PatientId) {
        return "Pacchetto non valido per questo paziente.";
      }
    }

    return null;
  }

  // Stores the therapy's pricing source - see TherapySaveRequest. Call
  // ValidateTherapyPricing first; caller saves.
  private void ApplyTherapyPricing(Therapy therapy, TherapySaveRequest request) {
    var existingLinks = _db.TherapyPrCodes.Where(l => l.TherapyId == therapy.Id).ToList();
    _db.TherapyPrCodes.RemoveRange(existingLinks);

    if (therapy.BillingCategory == TherapyBillingCategory.Convenzione) {
      foreach (var id in (request.PrCodeIds ?? new List<int>()).Distinct()) {
        _db.TherapyPrCodes.Add(new TherapyPrCode { TherapyId = therapy.Id, PrCodeId = id });
      }
      therapy.TherapyPacketId = null;
    } else {
      therapy.TherapyPacketId = request.TherapyPacketId;
    }
  }

  public class TherapyRemoveRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  public class TherapyCancelFutureSlotsRequest {
    public string ConfirmName { get; set; } = string.Empty;
    public DateOnly? FromDate { get; set; }
  }

  private static bool IsValidBillingCategory(int category) {
    return category == TherapyBillingCategory.Convenzione
      || category == TherapyBillingCategory.Privata
      || category == TherapyBillingCategory.Assicurazione;
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

    // Only one active Therapy per patient at a time for a plain "Aggiungi terapia" -
    // checked server-side too, not just via the UI. A renewal ("Rinnova terapia")
    // is always allowed, in parallel with whatever is still running.
    var hasActive = !request.IsRenewal && _db.Therapies.Any(t =>
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
      BillingCategory = request.BillingCategory,
      FoglioFirmaStatus = FoglioFirmaStatus.ToBeCreated,
      ModDate = DateTime.Now,
      ModUser = currentUserId.Value
    };

    var pricingError = ValidateTherapyPricing(therapy, request);
    if (pricingError != null) {
      return BadRequest(new { message = pricingError });
    }

    _db.Therapies.Add(therapy);
    _db.SaveChanges();

    ApplyTherapyPricing(therapy, request);

    foreach (var part in request.Parts) {
      var type = _db.TherapyTypes.Find(part.TherapyTypeId);
      var allowsGA = type != null && type.AllowsGinnasticaAttiva != 0;

      _db.TherapyParts.Add(new TherapyPart {
        TherapyId = therapy.Id,
        TherapyTypeId = part.TherapyTypeId,
        SessionCount = part.SessionCount,
        DefaultGinnasticaAttivaSlots = (allowsGA && part.IncludeGinnasticaAttiva) ? 1 : 0,
        ModDate = DateTime.Now,
        ModUser = currentUserId.Value
      });
    }

    _db.SaveChanges();

    // CPU: warn when this month's Convenzione revenue is already at 95% of the
    // Fatturato Massimo ASL.
    var warnings = GetBudgetWarningsForTherapy(therapy, new[] { DateOnly.FromDateTime(DateTime.Today) });

    return Ok(new { therapy.Id, warnings });
  }

  // ---- "Modifica terapia" (CPU): always available, edits every part - type,
  // number of sessions, add and remove parts. Planned sessions follow the edit:
  // - a removed part loses its future sessions not yet done; its past ones stay
  //   as history (SessionCount lowered to them) or the part goes entirely;
  // - fewer sessions on a part deletes its LAST future not-done sessions;
  // - the type of a part can change only while it has no sessions at all;
  // - a part can't go below what is already done or past.
  // Deleting planned sessions needs the type-the-name confirmation.
  private class TherapyEditPlan {
    public List<TherapySlot> SlotsToDelete { get; } = new();
    public List<(TherapyPart Part, TherapyPartRequest Request)> Updated { get; } = new();
    public Dictionary<TherapyPart, int> RemovedWithKept { get; } = new();
    public List<TherapyPartRequest> Added { get; } = new();
  }

  private TherapyEditPlan? BuildTherapyEditPlan(int therapyId, List<TherapyPartRequest> requested, out string? error) {
    error = null;
    var today = DateOnly.FromDateTime(DateTime.Today);
    var typeCache = GetTypeCache();

    var existing = _db.TherapyParts.Where(p => p.TherapyId == therapyId).ToList();
    var existingIds = existing.Select(p => p.Id).ToHashSet();
    var requestedIds = requested.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToList();

    if (requestedIds.Any(i => !existingIds.Contains(i)) || requestedIds.Distinct().Count() != requestedIds.Count) {
      error = "Parte non valida.";
      return null;
    }

    if (requested.Any(r => !typeCache.ContainsKey(r.TherapyTypeId))) {
      error = "Tipo di terapia non valido.";
      return null;
    }

    var partIds = existing.Select(p => p.Id).ToList();
    var slots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    bool IsRemovable(TherapySlot slot) => slot.Date >= today && slot.Status == TherapySlotStatus.ToBeDone;

    var plan = new TherapyEditPlan();

    foreach (var part in existing) {
      var partSlots = slots.Where(sl => sl.TherapyPartId == part.Id).ToList();
      var history = partSlots.Count(sl => !IsRemovable(sl));
      var typeName = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId].Name : "?";
      var req = requested.FirstOrDefault(r => r.Id == part.Id);

      if (req == null) {
        plan.RemovedWithKept[part] = history;
        plan.SlotsToDelete.AddRange(partSlots.Where(IsRemovable));
        continue;
      }

      if (req.TherapyTypeId != part.TherapyTypeId && partSlots.Count > 0) {
        error = typeName + ": il tipo non si può cambiare perché ci sono già sedute pianificate. Rimuovi la parte e aggiungine una nuova.";
        return null;
      }

      if (req.SessionCount < history) {
        error = typeName + ": servono almeno " + history + " sedute (già svolte o passate).";
        return null;
      }

      var excess = partSlots.Count - req.SessionCount;
      if (excess > 0) {
        plan.SlotsToDelete.AddRange(partSlots
          .Where(IsRemovable)
          .OrderByDescending(sl => sl.Date)
          .ThenByDescending(sl => sl.TimeSlot)
          .Take(excess));
      }

      plan.Updated.Add((part, req));
    }

    plan.Added.AddRange(requested.Where(r => !r.Id.HasValue));
    return plan;
  }

  // Preview for the confirmation: every planned session saving would delete.
  [HttpPost("/Therapies/{id}/EditPreview")]
  public IActionResult TherapiesEditPreview(int id, [FromBody] TherapySaveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(id);
    if (therapy == null) {
      return NotFound();
    }

    if (!IsValidTherapyPartsList(request.Parts)) {
      return BadRequest(new { message = "È necessaria almeno una parte, con almeno una seduta ciascuna." });
    }

    var plan = BuildTherapyEditPlan(id, request.Parts, out var error);
    if (plan == null) {
      return BadRequest(new { message = error });
    }

    var typeCache = GetTypeCache();
    var partCache = GetPartCache();
    var therapistNameCache = GetTherapistNameCache();

    var slots = plan.SlotsToDelete
      .OrderBy(sl => sl.Date).ThenBy(sl => sl.TimeSlot)
      .Select(sl => {
        var part = partCache.ContainsKey(sl.TherapyPartId) ? partCache[sl.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        return new {
          date = sl.Date.ToString("yyyy-MM-dd"),
          timeSlot = sl.TimeSlot,
          therapyTypeName = type?.Name ?? "?",
          therapistName = sl.TherapistId.HasValue && therapistNameCache.ContainsKey(sl.TherapistId.Value)
            ? therapistNameCache[sl.TherapistId.Value]
            : null
        };
      }).ToList();

    return Ok(new { slots });
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

    if (therapy.Status == TherapyStatus.Cancelled) {
      return BadRequest(new { message = "La terapia è annullata e non può essere modificata." });
    }

    if (!IsValidTherapyPartsList(request.Parts)) {
      return BadRequest(new { message = "È necessaria almeno una parte, con almeno una seduta ciascuna." });
    }

    var plan = BuildTherapyEditPlan(id, request.Parts, out var error);
    if (plan == null) {
      return BadRequest(new { message = error });
    }

    // Billing category editable any time (CPU: mistakes must be fixable). Only
    // changes the figures, which are always recalculated. Switching to
    // Convenzione needs at least one PR code. Not saved unless all checks pass.
    if (!IsValidBillingCategory(request.BillingCategory)) {
      return BadRequest(new { message = "Tipo di terapia non valido." });
    }

    var switchedToConvenzione = therapy.BillingCategory != TherapyBillingCategory.Convenzione
      && request.BillingCategory == TherapyBillingCategory.Convenzione;
    if (switchedToConvenzione && (request.PrCodeIds ?? new List<int>()).Count == 0) {
      return BadRequest(new { message = "Per una terapia in Convenzione serve almeno un codice PR." });
    }

    therapy.BillingCategory = request.BillingCategory;

    var pricingError = ValidateTherapyPricing(therapy, request);
    if (pricingError != null) {
      return BadRequest(new { message = pricingError });
    }

    if (plan.SlotsToDelete.Count > 0) {
      var patient = _db.Patients.Find(therapy.PatientId);
      if (!string.Equals((request.ConfirmName ?? "").Trim(), patient?.Name, StringComparison.OrdinalIgnoreCase)) {
        return BadRequest(new { message = "Il nome digitato non corrisponde." });
      }
    }

    var typeCache = GetTypeCache();
    int DefaultGaFor(int typeId, bool include) {
      var allowsGA = typeCache.ContainsKey(typeId) && typeCache[typeId].AllowsGinnasticaAttiva != 0;
      return allowsGA && include ? 1 : 0;
    }

    // 1) Sessions that no longer fit the edit.
    DetachRescheduledReferences(plan.SlotsToDelete.Select(sl => sl.Id).ToList());
    _db.TherapySlots.RemoveRange(plan.SlotsToDelete);
    _db.SaveChanges();

    // 2) Removed parts keep their history, or go entirely.
    foreach (var entry in plan.RemovedWithKept) {
      var part = entry.Key;
      if (entry.Value > 0) {
        part.SessionCount = entry.Value;
        part.ModDate = DateTime.Now;
        part.ModUser = currentUserId.Value;
      } else {
        var leftoverIds = _db.TherapySlots.Where(sl => sl.TherapyPartId == part.Id).Select(sl => sl.Id).ToList();
        DetachRescheduledReferences(leftoverIds);
        _db.TherapyParts.Remove(part);
      }
    }

    // Session counts or parts changed: completion is re-evaluated afterwards
    // (a name-only edit leaves a therapy closed by hand as it is).
    var countsChanged = plan.RemovedWithKept.Count > 0 || plan.Added.Count > 0
      || plan.Updated.Any(u => u.Part.SessionCount != u.Request.SessionCount);

    // 3) Edited parts. Ginnastica Attiva keeps its current value unless the
    // checkbox was actually changed.
    foreach (var (part, req) in plan.Updated) {
      var hadGA = part.DefaultGinnasticaAttivaSlots > 0;
      if (part.TherapyTypeId != req.TherapyTypeId || hadGA != req.IncludeGinnasticaAttiva) {
        part.DefaultGinnasticaAttivaSlots = DefaultGaFor(req.TherapyTypeId, req.IncludeGinnasticaAttiva);
      }
      part.TherapyTypeId = req.TherapyTypeId;
      part.SessionCount = req.SessionCount;
      part.ModDate = DateTime.Now;
      part.ModUser = currentUserId.Value;
    }

    // 4) New parts, unplanned.
    foreach (var req in plan.Added) {
      _db.TherapyParts.Add(new TherapyPart {
        TherapyId = id,
        TherapyTypeId = req.TherapyTypeId,
        SessionCount = req.SessionCount,
        DefaultGinnasticaAttivaSlots = DefaultGaFor(req.TherapyTypeId, req.IncludeGinnasticaAttiva),
        ModDate = DateTime.Now,
        ModUser = currentUserId.Value
      });
    }

    therapy.Name = request.Name;
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = currentUserId.Value;

    ApplyTherapyPricing(therapy, request);
    _db.SaveChanges();

    // More or fewer sessions can complete or reopen the therapy.
    var anyPartId = _db.TherapyParts.Where(p => p.TherapyId == id).Select(p => p.Id).FirstOrDefault();
    if (countsChanged && anyPartId != 0) {
      RecomputeTherapyCompletion(anyPartId);
    }

    RevertToBeScheduledIfEmpty(id);

    return Ok(new { removedCount = plan.SlotsToDelete.Count });
  }

  // Foglio firma (CPU): one button cycling Da creare -> In uso -> Chiuso -> Da
  // creare. "In uso" covers both InProgress and ToBeFinalized - the automatic
  // InProgress -> ToBeFinalized step on completion stays as it is (it feeds the
  // "da chiudere" list on the Fogli firma page). Never for Privata therapies.
  [HttpPost("/Therapies/{id}/CycleFoglioFirma")]
  public IActionResult TherapiesCycleFoglioFirma(int id) {
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

    if (therapy.BillingCategory == TherapyBillingCategory.Privata) {
      return BadRequest(new { message = "Il Foglio Firma non si applica alle terapie private." });
    }

    therapy.FoglioFirmaStatus = therapy.FoglioFirmaStatus switch {
      FoglioFirmaStatus.ToBeCreated => FoglioFirmaStatus.InProgress,
      FoglioFirmaStatus.InProgress => FoglioFirmaStatus.Completed,
      FoglioFirmaStatus.ToBeFinalized => FoglioFirmaStatus.Completed,
      _ => FoglioFirmaStatus.ToBeCreated
    };
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = currentUserId.Value;
    _db.SaveChanges();

    return Ok(new { foglioFirmaStatus = therapy.FoglioFirmaStatus });
  }

  // Pagato / Da pagare (CPU) - every therapy, whatever its BillingCategory.
  // Just the toggle on the patient page - no list of payments (CPU's call).
  [HttpPost("/Therapies/{id}/TogglePagato")]
  public IActionResult TherapiesTogglePagato(int id) {
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

    therapy.Pagato = !therapy.Pagato;
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = currentUserId.Value;
    _db.SaveChanges();

    return Ok(new { pagato = therapy.Pagato });
  }

  // "Non rinnova" checkbox (CPU): ticked = the patient won't renew; hides the
  // Rinnova button and the "Terapia in scadenza" alert. Can be unticked.
  [HttpPost("/Therapies/{id}/ToggleNonRinnova")]
  public IActionResult TherapiesToggleNonRinnova(int id) {
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

    therapy.NonRinnova = !therapy.NonRinnova;
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = currentUserId.Value;
    _db.SaveChanges();

    return Ok(new { nonRinnova = therapy.NonRinnova });
  }

  // "Chiudi terapia" (CPU): only a status change - marks the therapy Conclusa,
  // deletes nothing. Offered once at least one session was done. Foglio Firma
  // advances exactly as on a normal completion.
  [HttpPost("/Therapies/{id}/Close")]
  public IActionResult TherapiesClose(int id) {
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

    if (therapy.Status == TherapyStatus.Completed || therapy.Status == TherapyStatus.Cancelled) {
      return BadRequest(new { message = "La terapia è già chiusa." });
    }

    var partIds = _db.TherapyParts.Where(p => p.TherapyId == id).Select(p => p.Id).ToList();
    if (!_db.TherapySlots.Any(sl => partIds.Contains(sl.TherapyPartId) && sl.Status == TherapySlotStatus.Done)) {
      return BadRequest(new { message = "Nessuna seduta svolta: la terapia non può essere chiusa." });
    }

    therapy.Status = TherapyStatus.Completed;
    if (therapy.BillingCategory == TherapyBillingCategory.Privata) {
      therapy.FoglioFirmaStatus = FoglioFirmaStatus.Completed;
    } else if (therapy.FoglioFirmaStatus == FoglioFirmaStatus.InProgress) {
      therapy.FoglioFirmaStatus = FoglioFirmaStatus.ToBeFinalized;
    }
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = currentUserId.Value;
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
      return BadRequest(new { message = "Il nome digitato non corrisponde." });
    }

    // Filters from the clicked slot's own date, not "today" - a slot further in the
    // past-relative-to-now but still future-relative-to-the-therapy shouldn't be
    // spared just because today's date moved on. Falls back to today only if the
    // caller didn't send one (shouldn't happen from the UI, but keeps this safe).
    var fromDate = request.FromDate ?? DateOnly.FromDateTime(DateTime.Today);
    var partIds = _db.TherapyParts.Where(p => p.TherapyId == id).Select(p => p.Id).ToList();

    var futureSlots = _db.TherapySlots.Where(s => partIds.Contains(s.TherapyPartId) && s.Date >= fromDate).ToList();
    DetachRescheduledReferences(futureSlots.Select(s => s.Id).ToList());
    _db.TherapySlots.RemoveRange(futureSlots);
    _db.SaveChanges();

    RevertToBeScheduledIfEmpty(id);

    return Ok(new { removedCount = futureSlots.Count });
  }

  public class TherapyForceCloseRequest {
    public string ConfirmName { get; set; } = string.Empty;
  }

  // "Elimina -> sedute future" (CPU): every future session not yet done (from
  // today on, status Da fare) is deleted, freeing the slots, and the therapy is
  // marked Conclusa. Past/Done/Absent sessions stay as they are. Foglio Firma
  // advances exactly as on a normal completion.
  [HttpPost("/Therapies/{id}/ForceClose")]
  public IActionResult TherapiesForceClose(int id, [FromBody] TherapyForceCloseRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(id);

    if (therapy == null) {
      return NotFound();
    }

    var patient = _db.Patients.Find(therapy.PatientId);

    if (!string.Equals(request.ConfirmName, patient?.Name, StringComparison.OrdinalIgnoreCase)) {
      return BadRequest(new { message = "Il nome digitato non corrisponde." });
    }

    var today = DateOnly.FromDateTime(DateTime.Today);
    var partIds = _db.TherapyParts.Where(p => p.TherapyId == id).Select(p => p.Id).ToList();

    var futureSlots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Date >= today && s.Status == TherapySlotStatus.ToBeDone)
      .ToList();

    DetachRescheduledReferences(futureSlots.Select(s => s.Id).ToList());
    _db.TherapySlots.RemoveRange(futureSlots);

    therapy.Status = TherapyStatus.Completed;
    if (therapy.BillingCategory == TherapyBillingCategory.Privata) {
      therapy.FoglioFirmaStatus = FoglioFirmaStatus.Completed;
    } else if (therapy.FoglioFirmaStatus == FoglioFirmaStatus.InProgress) {
      therapy.FoglioFirmaStatus = FoglioFirmaStatus.ToBeFinalized;
    }
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = GetCurrentTherapistId() ?? therapy.ModUser;

    _db.SaveChanges();

    return Ok(new { removedCount = futureSlots.Count });
  }

  // Pianifica (CPU, formerly Ripianifica): the sessions that go back into
  // planning - this therapy's not-yet-done ones from today on (Fatto stays
  // fixed, past stays as it is), optionally just one part. The client opens the
  // right planning flow with these as editable drafts; see GetReplaceableSlots /
  // RemoveReplacedSlots.
  [HttpGet("/Therapies/{id}/ReplanSlots")]
  public IActionResult TherapiesReplanSlots(int id, int? partId = null) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var therapy = _db.Therapies.Find(id);

    if (therapy == null) {
      return NotFound();
    }

    if (therapy.Status == TherapyStatus.Cancelled) {
      return BadRequest(new { message = "La terapia è annullata." });
    }

    var today = DateOnly.FromDateTime(DateTime.Today);
    var parts = _db.TherapyParts.Where(p => p.TherapyId == id).ToList();
    var partIds = parts.Where(p => partId == null || p.Id == partId.Value).Select(p => p.Id).ToList();
    var typeCache = GetTypeCache();
    var therapistNames = _db.Therapists.ToDictionary(t => t.Id, t => t.Name);

    var slots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status == TherapySlotStatus.ToBeDone && s.Date >= today)
      .OrderBy(s => s.Date).ThenBy(s => s.TimeSlot)
      .ToList()
      .Select(s => {
        var part = parts.First(p => p.Id == s.TherapyPartId);
        var type = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        return new {
          s.Id,
          partId = part.Id,
          therapyTypeId = part.TherapyTypeId,
          therapyTypeName = type?.Name ?? "?",
          category = type?.Category ?? -1,
          executionType = type?.Type ?? -1,
          date = s.Date.ToString("yyyy-MM-dd"),
          s.TimeSlot,
          durationSlots = type != null ? GetSpanSlots(type) : 1,
          s.TherapistId,
          therapistName = s.TherapistId.HasValue && therapistNames.ContainsKey(s.TherapistId.Value) ? therapistNames[s.TherapistId.Value] : "Reparto"
        };
      })
      .ToList();

    if (slots.Count == 0) {
      return BadRequest(new { message = "Nessuna seduta futura da ripianificare." });
    }

    return Ok(new { slots });
  }
}
