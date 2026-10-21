using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // ---- Moved Assente sessions (CPU) --------------------------------------------
  //
  // A session that was Assente and then moved to a later date keeps its row as
  // Rescheduled + WasAbsent: it never counts, occupies nothing and isn't in the
  // absence alerts, but Presenze/Giorno/Settimana/Sposta sedute/Stato paziente
  // show it read-only (arrow, semi-transparent) with the date it went to.

  private List<TherapySlot> GetMovedAbsentSlotsInDateRange(List<DateOnly> dates) {
    return _db.TherapySlots
      .Where(s => dates.Contains(s.Date) && s.Status == TherapySlotStatus.Rescheduled && s.WasAbsent)
      .ToList();
  }

  // slot id -> "yyyy-MM-dd" of the session it was moved to, following the chain
  // when that session was moved again.
  private Dictionary<int, string> GetMovedToDates(List<TherapySlot> moved) {
    var result = new Dictionary<int, string>();

    foreach (var slot in moved) {
      var nextId = slot.RescheduledToId;
      TherapySlot? target = null;

      for (var hops = 0; nextId.HasValue && hops < 20; hops++) {
        target = _db.TherapySlots.Find(nextId.Value);
        if (target == null || target.Status != TherapySlotStatus.Rescheduled) {
          break;
        }
        nextId = target.RescheduledToId;
      }

      if (target != null) {
        result[slot.Id] = target.Date.ToString("yyyy-MM-dd");
      }
    }

    return result;
  }

  private static TherapistDaySlotDto MarkMovedAbsent(TherapistDaySlotDto dto, Dictionary<int, string> movedTo) {
    dto.MovedAbsent = true;
    dto.MovedToDate = movedTo.GetValueOrDefault(dto.Id);
    return dto;
  }

  private static DaySlotDto MarkMovedAbsent(DaySlotDto dto, Dictionary<int, string> movedTo) {
    dto.MovedAbsent = true;
    dto.MovedToDate = movedTo.GetValueOrDefault(dto.Id);
    return dto;
  }

  // ---- "Sposta sedute" (CPU) ---------------------------------------------------
  //
  // Patient page -> calendar of every session not marked Fatto of the patient's
  // open therapies (Assente and past Da fare included). "Sposta alla fine" moves
  // the clicked day's not-done sessions of that therapy (all parts together) to
  // after the therapy's last session, as a draft: same time, same therapist,
  // keeping the cadence used so far when possible and each type's sessions per
  // week; Reparto sessions without a therapist only need a working day and no
  // patient clash (relaxed - no capacity check). Drafts can be dragged to another
  // day (same time, therapist free). Nothing is written until Accept.

  public class SpostaSeduteDraft {
    public int SlotId { get; set; }
    public DateOnly Date { get; set; }
  }

  public class SpostaSeduteBlockRequest {
    public List<int> SlotIds { get; set; } = new();
    public List<SpostaSeduteDraft> Drafts { get; set; } = new();
    // Check only: the day the block was dropped on.
    public DateOnly? Date { get; set; }
  }

  public class SpostaSeduteAcceptRequest {
    public List<SpostaSeduteDraft> Moves { get; set; } = new();
  }

  private List<Therapy> GetOpenTherapies(int patientId) {
    return _db.Therapies
      .Where(t => t.PatientId == patientId && t.Status != TherapyStatus.Completed && t.Status != TherapyStatus.Cancelled)
      .ToList();
  }

  private static bool IsMovableStatus(int status) {
    return status == TherapySlotStatus.ToBeDone || status == TherapySlotStatus.PatientAbsent;
  }

  [HttpGet("/SpostaSedute/Data/{patientId}")]
  public IActionResult SpostaSeduteData(int patientId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var patient = _db.Patients.Find(patientId);
    if (patient == null) {
      return NotFound();
    }

    var therapies = GetOpenTherapies(patientId);
    var therapyIds = therapies.Select(t => t.Id).ToList();
    var parts = _db.TherapyParts.Where(p => therapyIds.Contains(p.TherapyId)).ToList();
    var partIds = parts.Select(p => p.Id).ToList();
    var typeCache = GetTypeCache();
    var therapistNames = GetTherapistNameCache();

    var slots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId)
        && (s.Status == TherapySlotStatus.ToBeDone || s.Status == TherapySlotStatus.PatientAbsent
          || (s.Status == TherapySlotStatus.Rescheduled && s.WasAbsent)))
      .ToList();

    var moved = slots.Where(s => s.Status == TherapySlotStatus.Rescheduled).ToList();
    var movedTo = GetMovedToDates(moved);

    var sessions = slots
      .OrderBy(s => s.Date).ThenBy(s => s.TimeSlot)
      .Select(s => {
        var part = parts.First(p => p.Id == s.TherapyPartId);
        var type = typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        return new {
          id = s.Id,
          therapyId = part.TherapyId,
          partId = part.Id,
          date = s.Date.ToString("yyyy-MM-dd"),
          timeSlot = s.TimeSlot,
          therapistName = s.TherapistId.HasValue && therapistNames.ContainsKey(s.TherapistId.Value) ? therapistNames[s.TherapistId.Value] : "Reparto",
          typeName = type?.Name ?? "?",
          typeLabel = type == null ? "?" : (string.IsNullOrEmpty(type.Abbreviazione) ? type.Name : type.Abbreviazione),
          color = type?.Color,
          status = s.Status == TherapySlotStatus.Rescheduled ? TherapySlotStatus.PatientAbsent : s.Status,
          movedAbsent = s.Status == TherapySlotStatus.Rescheduled,
          movedToDate = movedTo.GetValueOrDefault(s.Id)
        };
      })
      .ToList();

    return Ok(new {
      patientId,
      patientName = patient.Name,
      today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
      sessions
    });
  }

  // Loads and validates a block: every slot movable, of the same open therapy of
  // this patient. Returns null + error when not.
  private (Therapy Therapy, List<TherapySlot> Block)? LoadSpostaBlock(int patientId, List<int> slotIds, out string? error) {
    error = null;
    var ids = slotIds.Distinct().ToList();

    if (ids.Count == 0) {
      error = "Nessuna seduta selezionata.";
      return null;
    }

    var block = _db.TherapySlots.Where(s => ids.Contains(s.Id)).ToList();
    if (block.Count != ids.Count || block.Any(s => !IsMovableStatus(s.Status))) {
      error = "Alcune sedute sono cambiate nel frattempo. Ricarica la pagina.";
      return null;
    }

    var therapyIds = block
      .Select(s => _db.TherapyParts.Find(s.TherapyPartId)?.TherapyId ?? 0)
      .Distinct()
      .ToList();
    var therapy = therapyIds.Count == 1 ? _db.Therapies.Find(therapyIds[0]) : null;

    if (therapy == null || therapy.PatientId != patientId
        || therapy.Status == TherapyStatus.Completed || therapy.Status == TherapyStatus.Cancelled) {
      error = "Seduta non valida.";
      return null;
    }

    return (therapy, block);
  }

  // Why the block can't go on that date (null = it can). drafts = every draft
  // position (slot id -> date) on the page, block excluded; their original rows
  // are ignored by the conflict checks (_replanExcludedSlotIds) and their draft
  // positions are checked here instead. strict = cadence + sessions per week too.
  private string? DescribeSpostaBlock(Therapy therapy, List<TherapySlot> block, DateOnly date,
      Dictionary<int, DateOnly> drafts, bool strict, HashSet<DayOfWeek>? cadence) {
    var today = DateOnly.FromDateTime(DateTime.Today);
    var typeCache = GetTypeCache();

    if (date < today) {
      return "non si può spostare una seduta nel passato";
    }

    if (strict && cadence != null && cadence.Count > 0 && !cadence.Contains(date.DayOfWeek)) {
      return "giorno fuori dalla cadenza della terapia";
    }

    var blockIds = block.Select(s => s.Id).ToHashSet();
    var draftIds = drafts.Keys.ToList();
    var draftSlots = draftIds.Count == 0
      ? new List<TherapySlot>()
      : _db.TherapySlots.Where(s => draftIds.Contains(s.Id)).ToList();

    foreach (var slot in block) {
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      if (part == null || type == null) {
        return "seduta non valida";
      }

      var span = GetSpanSlots(type);
      string? reason;

      if (slot.TherapistId.HasValue) {
        reason = DescribeTherapistBlock(slot.TherapistId.Value, date, slot.TimeSlot, span);
      } else if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) {
        reason = "il centro è chiuso";
      } else {
        var holiday = FindBlockingVacation(0, date, slot.TimeSlot, span);
        reason = holiday != null ? "giorno festivo" + (string.IsNullOrEmpty(holiday.Name) ? "" : " (" + holiday.Name + ")") : null;
      }

      reason ??= DescribePatientConflict(therapy.PatientId, date, slot.TimeSlot, span, part.TherapyTypeId);

      if (reason != null) {
        return type.Name + ": " + reason;
      }

      // Other drafts already placed on that day.
      foreach (var other in draftSlots.Where(o => !blockIds.Contains(o.Id) && drafts[o.Id] == date)) {
        var otherPart = _db.TherapyParts.Find(other.TherapyPartId);
        var otherType = otherPart != null && typeCache.ContainsKey(otherPart.TherapyTypeId) ? typeCache[otherPart.TherapyTypeId] : null;
        var otherSpan = otherType != null ? GetSpanSlots(otherType) : 1;
        var overlaps = slot.TimeSlot < other.TimeSlot + otherSpan && slot.TimeSlot + span > other.TimeSlot;

        if (otherPart != null && otherPart.TherapyTypeId == part.TherapyTypeId) {
          return type.Name + ": il paziente ha già " + type.Name + " in questo giorno";
        }
        if (overlaps) {
          return type.Name + ": il paziente ha già " + (otherType?.Name ?? "una seduta") + " alle " + FormatSlotTime(other.TimeSlot);
        }
      }
    }

    if (strict) {
      // Sessions per week of each type (Mezieres 1, Isocinetica 3...), counting
      // the therapy's sessions in that Monday-Sunday week at their draft position.
      var weekStart = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
      var weekEnd = weekStart.AddDays(6);
      var partIds = _db.TherapyParts.Where(p => p.TherapyId == therapy.Id).Select(p => p.Id).ToList();
      var therapySlots = _db.TherapySlots
        .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
        .ToList()
        .Where(s => !blockIds.Contains(s.Id))
        .ToList();

      foreach (var group in block.GroupBy(s => s.TherapyPartId)) {
        var part = _db.TherapyParts.Find(group.Key);
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        if (part == null || type == null || type.TherapyWeeklyFrequency <= 0) {
          continue;
        }

        var inWeek = therapySlots.Count(s => {
          if (s.TherapyPartId != part.Id) {
            return false;
          }
          var d = drafts.ContainsKey(s.Id) ? drafts[s.Id] : s.Date;
          return d >= weekStart && d <= weekEnd;
        });

        if (inWeek + group.Count() > type.TherapyWeeklyFrequency) {
          return type.Name + ": massimo " + type.TherapyWeeklyFrequency + " a settimana";
        }
      }
    }

    return null;
  }

  private Dictionary<int, DateOnly> ToDraftMap(List<SpostaSeduteDraft>? drafts, IEnumerable<int> excludeIds) {
    var exclude = excludeIds.ToHashSet();
    var map = new Dictionary<int, DateOnly>();
    foreach (var d in drafts ?? new List<SpostaSeduteDraft>()) {
      if (!exclude.Contains(d.SlotId)) {
        map[d.SlotId] = d.Date;
      }
    }
    return map;
  }

  // "Sposta alla fine": the first valid day after the therapy's last session.
  [HttpPost("/SpostaSedute/{patientId}/Propose")]
  public IActionResult SpostaSedutePropose(int patientId, [FromBody] SpostaSeduteBlockRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var loaded = LoadSpostaBlock(patientId, request.SlotIds, out var error);
    if (loaded == null) {
      return BadRequest(new { message = error });
    }

    var (therapy, block) = loaded.Value;
    var drafts = ToDraftMap(request.Drafts, block.Select(s => s.Id));
    _replanExcludedSlotIds = drafts.Keys.Concat(block.Select(s => s.Id)).ToList();

    var partIds = _db.TherapyParts.Where(p => p.TherapyId == therapy.Id).Select(p => p.Id).ToList();
    var therapySlots = _db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    // Cadence = the weekdays this therapy's sessions were planned on.
    var cadence = therapySlots.Select(s => s.Date.DayOfWeek).ToHashSet();
    var lastDate = therapySlots.Max(s => drafts.ContainsKey(s.Id) ? drafts[s.Id] : s.Date);
    var today = DateOnly.FromDateTime(DateTime.Today);
    var start = lastDate.AddDays(1) > today ? lastDate.AddDays(1) : today;

    // First with the cadence; if nothing fits, any working day.
    foreach (var useCadence in new[] { true, false }) {
      for (var d = start; d <= start.AddDays(180); d = d.AddDays(1)) {
        var reason = DescribeSpostaBlock(therapy, block, d, drafts, true, useCadence ? cadence : null);
        if (reason == null) {
          return Ok(new { date = d.ToString("yyyy-MM-dd") });
        }
      }
    }

    return BadRequest(new { message = "Nessun giorno disponibile nei prossimi 6 mesi." });
  }

  // Manual drag of a draft block to another day (same time, therapist free).
  [HttpPost("/SpostaSedute/{patientId}/Check")]
  public IActionResult SpostaSeduteCheck(int patientId, [FromBody] SpostaSeduteBlockRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    if (!request.Date.HasValue) {
      return BadRequest(new { message = "Giorno mancante." });
    }

    var loaded = LoadSpostaBlock(patientId, request.SlotIds, out var error);
    if (loaded == null) {
      return BadRequest(new { message = error });
    }

    var (therapy, block) = loaded.Value;
    var drafts = ToDraftMap(request.Drafts, block.Select(s => s.Id));
    _replanExcludedSlotIds = drafts.Keys.Concat(block.Select(s => s.Id)).ToList();

    var reason = DescribeSpostaBlock(therapy, block, request.Date.Value, drafts, false, null);
    if (reason != null) {
      return BadRequest(new { message = "Spostamento non possibile - " + WithDate(request.Date.Value, reason) + "." });
    }

    return Ok();
  }

  // "Accetta cambiamenti": every move written at once (all or nothing). Each
  // moved session gets a new row at the new date (same time, therapist and
  // Ginnastica Attiva); the original stays as Rescheduled -> RescheduledToId,
  // remembering an absence (WasAbsent). Isocinetica's extra 15 minutes are then
  // re-applied to the first/last sessions.
  [HttpPost("/SpostaSedute/{patientId}/Accept")]
  public IActionResult SpostaSeduteAccept(int patientId, [FromBody] SpostaSeduteAcceptRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var currentUserId = GetCurrentTherapistId();
    if (currentUserId == null) {
      return Forbid();
    }

    var moves = (request.Moves ?? new List<SpostaSeduteDraft>())
      .GroupBy(m => m.SlotId)
      .Select(g => g.Last())
      .ToList();

    if (moves.Count == 0) {
      return BadRequest(new { message = "Nessun cambiamento da salvare." });
    }

    var slotIds = moves.Select(m => m.SlotId).ToList();
    var slots = _db.TherapySlots.Where(s => slotIds.Contains(s.Id)).ToList();
    if (slots.Count != slotIds.Count || slots.Any(s => !IsMovableStatus(s.Status))) {
      return BadRequest(new { message = "Alcune sedute sono cambiate nel frattempo. Ricarica la pagina." });
    }

    var openTherapyIds = GetOpenTherapies(patientId).Select(t => t.Id).ToHashSet();
    var typeCache = GetTypeCache();
    var today = DateOnly.FromDateTime(DateTime.Today);
    var affectedTherapyIds = new HashSet<int>();

    // Originals don't block anything while their new positions are checked.
    _replanExcludedSlotIds = slotIds;

    using var transaction = _db.Database.BeginTransaction();

    foreach (var move in moves.OrderBy(m => m.Date)) {
      var slot = slots.First(s => s.Id == move.SlotId);
      var part = _db.TherapyParts.Find(slot.TherapyPartId);
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

      if (part == null || type == null || !openTherapyIds.Contains(part.TherapyId)) {
        return BadRequest(new { message = "Seduta non valida." });
      }

      if (move.Date == slot.Date) {
        continue;
      }

      if (move.Date < today) {
        return BadRequest(new { message = "Non si può spostare una seduta nel passato." });
      }

      var span = GetSpanSlots(type);
      var reason = slot.TherapistId.HasValue
        ? DescribeTherapistBlock(slot.TherapistId.Value, move.Date, slot.TimeSlot, span)
        : null;
      reason ??= DescribePatientConflict(GetTherapyCache()[part.TherapyId].PatientId, move.Date, slot.TimeSlot, span, part.TherapyTypeId);

      if (reason != null) {
        return BadRequest(new { message = type.Name + " del " + FormatShortDate(move.Date) + " non più valida - " + reason + "." });
      }

      var newSlot = BuildTherapySlot(part.Id, move.Date, slot.TimeSlot, slot.TherapistId);
      newSlot.GinnasticaAttivaSlots = slot.GinnasticaAttivaSlots;
      newSlot.ModDate = DateTime.Now;
      newSlot.ModUser = currentUserId.Value;
      _db.TherapySlots.Add(newSlot);
      _db.SaveChanges();

      slot.WasAbsent = slot.Status == TherapySlotStatus.PatientAbsent;
      slot.Status = TherapySlotStatus.Rescheduled;
      slot.RescheduledToId = newSlot.Id;
      slot.ModDate = DateTime.Now;
      slot.ModUser = currentUserId.Value;
      _db.SaveChanges();

      affectedTherapyIds.Add(part.TherapyId);
    }

    var warnings = new List<string>();
    foreach (var therapyId in affectedTherapyIds) {
      var therapy = _db.Therapies.Find(therapyId);
      if (therapy != null) {
        warnings.AddRange(ApplyIsocineticaExtraSlots(therapy));
        therapy.ModDate = DateTime.Now;
        therapy.ModUser = currentUserId.Value;
      }
    }
    _db.SaveChanges();

    // Budget ASL (CPU): block when a month the sessions moved into goes over
    // its max limit; warn at 95% of its normal limit.
    var movedDatesByTherapy = moves
      .Where(m => m.Date != slots.First(s => s.Id == m.SlotId).Date)
      .GroupBy(m => _db.TherapyParts.Find(slots.First(s => s.Id == m.SlotId).TherapyPartId)!.TherapyId)
      .ToDictionary(g => g.Key, g => g.Select(m => m.Date).ToList());

    foreach (var entry in movedDatesByTherapy) {
      var therapy = _db.Therapies.Find(entry.Key);
      if (therapy == null) {
        continue;
      }

      var budgetError = CheckBudgetLimitForTherapy(therapy, entry.Value);
      if (budgetError != null) {
        return BadRequest(new { message = budgetError });
      }

      warnings.AddRange(GetBudgetWarningsForTherapy(therapy, entry.Value));
    }

    transaction.Commit();

    return Ok(new { warnings = warnings.Distinct().ToList() });
  }
}
