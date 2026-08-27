using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // --- Giorno / Settimana (Day / Week read-only schedule views) --------------
  //
  // Three mutually-exclusive modes, each with a single-day (Giorno) and a 5-day
  // Mon-Fri (Settimana) endpoint: a selected Therapist (grid range = availability
  // min-max), a selected Patient (grid range = whichever half-day, AM/PM/full,
  // actually has booked sessions - 9:00-12:00 fallback if none), or a Reparto sex
  // (grid range = fixed clinic-wide hours). Both only ever show real, already-
  // placed TherapySlot rows (no speculative/available slots), plus the same
  // always-visible Reparto occupancy background used elsewhere.
  //
  // Perf note (see conversation history): this used to call the shared per-slot
  // Pianificazione helpers (ComputeRepartoCapacityAndDemand & co), which - even
  // memoized - still ran their check loop fresh for every single 15-minute slot.
  // This file now fetches ALL data it needs for the requested date range (1 day
  // for Giorno, 5 for Settimana) up front, in a small fixed number of queries
  // regardless of range size, and computes the whole capacity grid in pure
  // in-memory C# from there. This is deliberately self-contained - it does NOT
  // touch or reuse ComputeRepartoCapacityAndDemand/IsBlockedByVacation/
  // IsWithinAvailability/IsTherapistBusyWithActiveSession/CountLightRepartoDemand/
  // CountAllRepartoSessions in AppController.Pianificazione*.cs, which Pianificazione
  // Palestra/Reparto/Mixed still use as-is - keeping those untouched avoids any risk
  // of regressing those already-working screens.

  private const int GiornoNoonSlot = 52; // 13:00, matches the existing AM/PM vacation convention

  // Per-request memoization, local to this file (nothing here is used elsewhere) -
  // same "brand new AppController per request" reasoning as the caches in AppController.cs.
  private Dictionary<int, Dictionary<int, List<DayAvailabilityRangeDto>>>? _weeklyAvailabilityCache;

  private class DaySlotDto {
    public int Id { get; set; }
    public int TimeSlot { get; set; }
    public int DurationSlots { get; set; }
    public int? TherapyTypeId { get; set; }
    public string TherapyTypeName { get; set; } = "?";
    public int? TherapyTypeCategory { get; set; }
    public int? TherapyTypeColor { get; set; }
    public string PatientName { get; set; } = "?";
    public int? TherapistId { get; set; }
    public string? TherapistName { get; set; }
  }

  private class DayAvailabilityRangeDto {
    public int StartTime { get; set; }
    public int EndTime { get; set; }
  }

  private class DayCapacityEntryDto {
    public int TimeSlot { get; set; }
    public double Capacity { get; set; }
    public int Demand { get; set; }
    public int ExistingCount { get; set; }
  }

  private class VacationDto {
    public int? TherapistId { get; set; }
    public string? Name { get; set; }
    public int? AMPM { get; set; }
    public int? IsYearIndependent { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
  }

  // Everything needed to compute the Reparto capacity/demand grid for a whole
  // requested date range, loaded once and then consumed purely in-memory - see
  // BuildCapacityComputationContext for how it's populated and
  // ComputeCapacityAndDemandInMemory for how it's read.
  private class CapacityComputationContext {
    public List<Therapist> RelevantTherapists = new();
    public int RepartoRate;
    public int CoveringRate;
    // Vacations relevant to RelevantTherapists (their own + global/company-wide ones).
    // Kept as a flat list - small enough that a linear scan per check is cheap and
    // simpler than pre-grouping, same as the original IsBlockedByVacation did per-call.
    public List<Vacation> Vacations = new();
    public Dictionary<(int TherapistId, int DayOfWeek), List<(int StartTime, int EndTime)>> Availability = new();
    // A therapist's own Active-type sessions that date (start slot, span) - "busy".
    public Dictionary<(int TherapistId, DateOnly Date), List<(int Start, int Span)>> ActiveSlotsByTherapistDate = new();
    // ALL Light Reparto sessions that date, any therapist/patient - "demand".
    public Dictionary<DateOnly, List<(int Start, int Span)>> LightRepartoSlotsByDate = new();
    // ALL Reparto-category sessions that date (Light or Active) - "existingCount" badge.
    public Dictionary<DateOnly, List<(int Start, int Span)>> AllRepartoSlotsByDate = new();
  }

  // ---- Shared small helpers ---------------------------------------------------

  private int GetClinicHoursStart() {
    return _settingsCache.Get("AvailabilityStart", 28);
  }

  private int GetClinicHoursEnd() {
    return _settingsCache.Get("AvailabilityEnd", 76);
  }

  private static List<DateOnly> GetWeekdayDates(DateOnly weekStart) {
    return Enumerable.Range(0, 5).Select(i => weekStart.AddDays(i)).ToList();
  }

  private static VacationDto ToVacationDto(Vacation v) {
    return new VacationDto {
      TherapistId = v.TherapistId,
      Name = v.Name,
      AMPM = v.AMPM,
      IsYearIndependent = v.IsYearIndependent,
      Month = v.Month,
      Day = v.Day,
      StartDate = v.StartDate,
      EndDate = v.EndDate
    };
  }

  private List<VacationDto> GetTherapistVacations(int therapistId) {
    return _db.Vacations
      .Where(v => v.TherapistId == null || v.TherapistId == therapistId)
      .ToList()
      .Select(ToVacationDto)
      .ToList();
  }

  private List<VacationDto> GetVacationsForTherapists(List<int> therapistIds) {
    return _db.Vacations
      .Where(v => v.TherapistId == null || therapistIds.Contains(v.TherapistId.Value))
      .ToList()
      .Select(ToVacationDto)
      .ToList();
  }

  private List<VacationDto> GetGlobalVacations() {
    return _db.Vacations
      .Where(v => v.TherapistId == null)
      .ToList()
      .Select(ToVacationDto)
      .ToList();
  }

  // A therapist's whole weekly availability schedule (at most one small row per
  // weekday), grouped by DayOfWeek - fetched once per therapist regardless of how
  // many dates in the requested range end up needing it (1 for Giorno, 5 for
  // Settimana, all sharing this single cached fetch).
  private Dictionary<int, List<DayAvailabilityRangeDto>> GetTherapistWeeklyAvailability(int therapistId) {
    _weeklyAvailabilityCache ??= new Dictionary<int, Dictionary<int, List<DayAvailabilityRangeDto>>>();

    if (!_weeklyAvailabilityCache.TryGetValue(therapistId, out var weekly)) {
      weekly = _db.TherapistAvailabilities
        .Where(a => a.TherapistId == therapistId)
        .ToList()
        .GroupBy(a => a.DayOfWeek)
        .ToDictionary(
          g => g.Key,
          g => g.OrderBy(a => a.StartTime).Select(a => new DayAvailabilityRangeDto { StartTime = a.StartTime, EndTime = a.EndTime }).ToList());
      _weeklyAvailabilityCache[therapistId] = weekly;
    }

    return weekly;
  }

  private List<DayAvailabilityRangeDto> GetTherapistDayAvailability(int therapistId, DateOnly date) {
    var weekly = GetTherapistWeeklyAvailability(therapistId);
    var dayOfWeek = (int)date.DayOfWeek; // Sunday=0...Saturday=6, matches JS Date.getDay()
    return weekly.TryGetValue(dayOfWeek, out var ranges) ? ranges : new List<DayAvailabilityRangeDto>();
  }

  // ---- Batch data loading (one shot for the whole requested date range) -------

  // Every TherapySlot in the requested date range, regardless of therapist/patient -
  // the single query every other batch-loading step below slices in memory.
  private List<TherapySlot> GetSlotsInDateRange(List<DateOnly> dates) {
    return _db.TherapySlots
      .AsNoTracking()
      .Where(s => dates.Contains(s.Date) && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();
  }

  private CapacityComputationContext BuildCapacityComputationContext(List<TherapySlot> slotsInRange) {
    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();
    var (repartoRate, coveringRate) = GetRepartoRates();
    var therapistIds = relevantTherapists.Select(t => t.Id).ToList();

    var vacations = _db.Vacations
      .Where(v => v.TherapistId == null || therapistIds.Contains(v.TherapistId.Value))
      .ToList();

    var availability = _db.TherapistAvailabilities
      .Where(a => therapistIds.Contains(a.TherapistId))
      .ToList()
      .GroupBy(a => (a.TherapistId, a.DayOfWeek))
      .ToDictionary(g => g.Key, g => g.Select(a => (a.StartTime, a.EndTime)).ToList());

    var typeCache = GetTypeCache();
    var partCache = GetPartCache();

    var activeSlotsByTherapistDate = new Dictionary<(int, DateOnly), List<(int Start, int Span)>>();
    var lightRepartoSlotsByDate = new Dictionary<DateOnly, List<(int Start, int Span)>>();
    var allRepartoSlotsByDate = new Dictionary<DateOnly, List<(int Start, int Span)>>();

    foreach (var s in slotsInRange) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

      if (type == null) {
        continue;
      }

      var span = GetSpanSlots(type);

      if (type.Type == TherapyExecutionType.Active && s.TherapistId.HasValue) {
        var busyKey = (s.TherapistId.Value, s.Date);
        if (!activeSlotsByTherapistDate.TryGetValue(busyKey, out var activeList)) {
          activeList = new List<(int, int)>();
          activeSlotsByTherapistDate[busyKey] = activeList;
        }
        activeList.Add((s.TimeSlot, span));
      }

      if (type.Category == TherapyCategory.Reparto) {
        if (!allRepartoSlotsByDate.TryGetValue(s.Date, out var allList)) {
          allList = new List<(int, int)>();
          allRepartoSlotsByDate[s.Date] = allList;
        }
        allList.Add((s.TimeSlot, span));

        if (type.Type == TherapyExecutionType.Light) {
          if (!lightRepartoSlotsByDate.TryGetValue(s.Date, out var lightList)) {
            lightList = new List<(int, int)>();
            lightRepartoSlotsByDate[s.Date] = lightList;
          }
          lightList.Add((s.TimeSlot, span));
        }
      }
    }

    return new CapacityComputationContext {
      RelevantTherapists = relevantTherapists,
      RepartoRate = repartoRate,
      CoveringRate = coveringRate,
      Vacations = vacations,
      Availability = availability,
      ActiveSlotsByTherapistDate = activeSlotsByTherapistDate,
      LightRepartoSlotsByDate = lightRepartoSlotsByDate,
      AllRepartoSlotsByDate = allRepartoSlotsByDate
    };
  }

  // Mirrors the original IsBlockedByVacation's logic exactly (see
  // AppController.Pianificazione.cs), just reading from an already-fetched list
  // instead of querying - always called with a 1-slot span, same as the original
  // call site in ComputeRepartoCapacityAndDemand.
  private static bool IsBlockedByVacationInMemory(List<Vacation> vacations, int therapistId, DateOnly date, int timeSlot) {
    var newEnd = timeSlot + 1;

    foreach (var v in vacations) {
      if (v.TherapistId != null && v.TherapistId != therapistId) {
        continue;
      }

      bool matches;

      if (v.IsYearIndependent == 1 && v.Month.HasValue && v.Day.HasValue) {
        matches = date.Month == v.Month.Value && date.Day == v.Day.Value;
      } else if (v.StartDate.HasValue && v.EndDate.HasValue) {
        matches = date >= v.StartDate.Value && date <= v.EndDate.Value;
      } else {
        matches = false;
      }

      if (!matches) {
        continue;
      }

      var isSingleDay = v.StartDate.HasValue && v.EndDate.HasValue && v.StartDate.Value == v.EndDate.Value;
      var coverStart = 0;
      var coverEnd = 96;

      if (isSingleDay && v.AMPM == 0) {
        coverEnd = GiornoNoonSlot;
      } else if (isSingleDay && v.AMPM == 1) {
        coverStart = GiornoNoonSlot;
      }

      if (timeSlot < coverEnd && newEnd > coverStart) {
        return true;
      }
    }

    return false;
  }

  private static (double capacity, int demand) ComputeCapacityAndDemandInMemory(CapacityComputationContext ctx, DateOnly date, int slot) {
    double capacity = 0;
    var dayOfWeek = (int)date.DayOfWeek;

    foreach (var t in ctx.RelevantTherapists) {
      if (IsBlockedByVacationInMemory(ctx.Vacations, t.Id, date, slot)) {
        continue;
      }

      var isAvailable = ctx.Availability.TryGetValue((t.Id, dayOfWeek), out var ranges)
        && ranges.Any(r => slot >= r.StartTime && slot + 1 <= r.EndTime);

      if (!isAvailable) {
        continue;
      }

      var isBusy = ctx.ActiveSlotsByTherapistDate.TryGetValue((t.Id, date), out var activeSlots)
        && activeSlots.Any(a => slot >= a.Start && slot < a.Start + a.Span);

      if (isBusy) {
        continue;
      }

      var isRepartoCapable = (t.OperatingArea & TherapistOperatingArea.Reparto) != 0;
      var rate = isRepartoCapable ? ctx.RepartoRate : ctx.CoveringRate;
      capacity += 15.0 / rate;
    }

    var demand = ctx.LightRepartoSlotsByDate.TryGetValue(date, out var lightSlots)
      ? lightSlots.Count(s => slot >= s.Start && slot < s.Start + s.Span)
      : 0;

    return (Math.Floor(capacity), demand);
  }

  private static List<DayCapacityEntryDto> BuildCapacityGridInMemory(
    CapacityComputationContext ctx, DateOnly date, int start, int end, bool includeExistingCount) {
    var grid = new List<DayCapacityEntryDto>();

    for (var slot = start; slot < end; slot++) {
      var (capacity, demand) = ComputeCapacityAndDemandInMemory(ctx, date, slot);
      var existingCount = 0;

      if (includeExistingCount && ctx.AllRepartoSlotsByDate.TryGetValue(date, out var allSlots)) {
        existingCount = allSlots.Count(s => slot >= s.Start && slot < s.Start + s.Span);
      }

      grid.Add(new DayCapacityEntryDto {
        TimeSlot = slot,
        Capacity = capacity,
        Demand = demand,
        ExistingCount = existingCount
      });
    }

    return grid;
  }

  // ---- "Real booked sessions to display" builders, sliced from the same batch --

  // Therapist-mode slots never carry TherapistId/TherapistName - every slot here is
  // already known to belong to the requested therapist (that's the query filter
  // below), and therapistModeLabel doesn't read those fields anyway. A slimmer DTO
  // than DaySlotDto (used by Reparto/Mixed, which do need those fields) avoids
  // shipping two always-null properties in every Therapist-mode response.
  private class TherapistDaySlotDto {
    public int Id { get; set; }
    public int TimeSlot { get; set; }
    public int DurationSlots { get; set; }
    public int? TherapyTypeId { get; set; }
    public string TherapyTypeName { get; set; } = "?";
    public int? TherapyTypeCategory { get; set; }
    public int? TherapyTypeColor { get; set; }
    public string PatientName { get; set; } = "?";
  }

  private List<TherapistDaySlotDto> BuildTherapistSlotsFromBatch(List<TherapySlot> slotsInRange, int therapistId, DateOnly date) {
    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();

    return slotsInRange
      .Where(s => s.TherapistId == therapistId && s.Date == date)
      .Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        var patientName = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId)
          ? patientCache[slotTherapy.PatientId].Name
          : "?";

        return new TherapistDaySlotDto {
          Id = s.Id,
          TimeSlot = s.TimeSlot,
          DurationSlots = type != null ? GetSpanSlots(type) : 1,
          TherapyTypeId = part != null ? part.TherapyTypeId : (int?)null,
          TherapyTypeName = type != null ? type.Name : "?",
          TherapyTypeCategory = type != null ? type.Category : (int?)null,
          TherapyTypeColor = type != null ? type.Color : (int?)null,
          PatientName = patientName
        };
      })
      .ToList();
  }

  private List<DaySlotDto> BuildRepartoSlotsFromBatch(List<TherapySlot> slotsInRange, int sex, DateOnly date) {
    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    return slotsInRange
      .Where(s => s.Date == date)
      .Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : null;
        return new { Slot = s, Part = part, Type = type, Patient = patient };
      })
      .Where(x => x.Type != null && x.Type.Category == TherapyCategory.Reparto && x.Patient != null && x.Patient.Sex == sex)
      .Select(x => new DaySlotDto {
        Id = x.Slot.Id,
        TimeSlot = x.Slot.TimeSlot,
        DurationSlots = GetSpanSlots(x.Type!),
        TherapyTypeId = x.Part!.TherapyTypeId,
        TherapyTypeName = x.Type!.Name,
        TherapyTypeCategory = x.Type!.Category,
        TherapyTypeColor = x.Type!.Color,
        TherapistId = x.Slot.TherapistId,
        TherapistName = x.Slot.TherapistId.HasValue && therapistNameCache.ContainsKey(x.Slot.TherapistId.Value)
          ? therapistNameCache[x.Slot.TherapistId.Value]
          : null,
        PatientName = x.Patient!.Name
      })
      .ToList();
  }

  // Not scoped to a sex or a category (unlike BuildRepartoSlotsFromBatch) - returns
  // every session, Palestra or Reparto, covering one exact date+timeslot - backs
  // the on-demand SlotDetails endpoint used by the existingCount badge popup.
  private List<DaySlotDto> BuildSlotDetailsForSlot(List<TherapySlot> slotsInRange, DateOnly date, int timeSlot) {
    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    return slotsInRange
      .Where(s => s.Date == date)
      .Select(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : null;
        return new { Slot = s, Part = part, Type = type, Patient = patient };
      })
      .Where(x => x.Type != null && x.Patient != null)
      .Select(x => new DaySlotDto {
        Id = x.Slot.Id,
        TimeSlot = x.Slot.TimeSlot,
        DurationSlots = GetSpanSlots(x.Type!),
        TherapyTypeId = x.Part!.TherapyTypeId,
        TherapyTypeName = x.Type!.Name,
        TherapyTypeCategory = x.Type!.Category,
        TherapyTypeColor = x.Type!.Color,
        TherapistId = x.Slot.TherapistId,
        TherapistName = x.Slot.TherapistId.HasValue && therapistNameCache.ContainsKey(x.Slot.TherapistId.Value)
          ? therapistNameCache[x.Slot.TherapistId.Value]
          : null,
        PatientName = x.Patient!.Name
      })
      .Where(s => timeSlot >= s.TimeSlot && timeSlot < s.TimeSlot + s.DurationSlots)
      .ToList();
  }

  // ---- Giorno endpoints (single day) ------------------------------------------

  [HttpGet("/Giorno/TherapistData")]
  public IActionResult GiornoTherapistData(int therapistId, DateOnly date) {
    if (GetCurrentTherapistId() == null) {
      return Forbid();
    }

    var therapist = _db.Therapists.Find(therapistId);

    if (therapist == null) {
      return NotFound();
    }

    var availability = GetTherapistDayAvailability(therapistId, date);
    var hasAvailability = availability.Count > 0;
    var gridStart = hasAvailability ? availability.Min(a => a.StartTime) : 0;
    var gridEnd = hasAvailability ? availability.Max(a => a.EndTime) : 0;

    var vacations = GetTherapistVacations(therapistId);

    var dates = new List<DateOnly> { date };
    var slotsInRange = GetSlotsInDateRange(dates);
    var ctx = BuildCapacityComputationContext(slotsInRange);
    var capacityGrid = BuildCapacityGridInMemory(ctx, date, gridStart, gridEnd, includeExistingCount: true);
    var capacityWarningThreshold = GetRepartoCapacityWarningThreshold();

    var slots = BuildTherapistSlotsFromBatch(slotsInRange, therapistId, date);

    return Ok(new {
      requestDate = date.ToString("yyyy-MM-dd"),
      requestTherapistId = therapistId,
      name = therapist.Name,
      hasAvailability,
      gridStart,
      gridEnd,
      availability,
      vacations,
      capacityGrid,
      capacityWarningThreshold,
      slots
    });
  }

  [HttpGet("/Giorno/RepartoData")]
  public IActionResult GiornoRepartoData(int sex, DateOnly date) {
    if (GetCurrentTherapistId() == null) {
      return Forbid();
    }

    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();
    var vacations = GetGlobalVacations();

    var dates = new List<DateOnly> { date };
    var slotsInRange = GetSlotsInDateRange(dates);
    var ctx = BuildCapacityComputationContext(slotsInRange);

    // No existingCount here (unlike Therapist mode) - the badge is deliberately
    // omitted for this mode since every session is already shown directly as a slot.
    var capacityGrid = BuildCapacityGridInMemory(ctx, date, clinicStart, clinicEnd, includeExistingCount: false);
    var capacityWarningThreshold = GetRepartoCapacityWarningThreshold();

    var slots = BuildRepartoSlotsFromBatch(slotsInRange, sex, date);

    return Ok(new {
      requestDate = date.ToString("yyyy-MM-dd"),
      requestSex = sex,
      name = sex == PatientSex.Female ? "Reparto Donne" : "Reparto Uomini",
      gridStart = clinicStart,
      gridEnd = clinicEnd,
      vacations,
      capacityGrid,
      capacityWarningThreshold,
      slots
    });
  }

  // On-demand detail lookup for a single date+timeslot's existingCount badge -
  // deliberately NOT included in the capacityGrid payload above (which only ever
  // carries the aggregate ExistingCount) to keep the default Giorno/Settimana
  // response lightweight; the client calls this only when the badge is clicked.
  // Returns every session at that timeslot, Palestra or Reparto.
  [HttpGet("/Giorno/SlotDetails")]
  public IActionResult GiornoSlotDetails(DateOnly date, int timeSlot) {
    if (GetCurrentTherapistId() == null) {
      return Forbid();
    }

    var slotsInRange = GetSlotsInDateRange(new List<DateOnly> { date });
    var sessions = BuildSlotDetailsForSlot(slotsInRange, date, timeSlot);

    return Ok(sessions);
  }

  // ---- Single-slot detail popup + actions (Giorno/Settimana) -------------------
  //
  // Opened by clicking a slot directly, or a line in the SlotDetails popup above.
  // Read access: Admin (Accettazione) can view any slot; a plain Therapist can only
  // view a slot currently assigned to them (a soft/Light Reparto slot has no
  // TherapistId, so it's Admin-only, matching CPU's "2 cases" framing). Actions:
  // ChangeTherapist/Remove are Admin-only; ChangeStatus is allowed for Admin (any of
  // ToBeDone/Done/PatientAbsent) or the assigned Therapist (Done/PatientAbsent only -
  // no reverting to ToBeDone). Rescheduled is deliberately never settable here - it's
  // reserved for the future Ripianifica flow, which is a no-op on the client for now.

  private class SlotDetailDto {
    public int Id { get; set; }
    public int? TherapyId { get; set; }
    public string PatientName { get; set; } = "?";
    public string TherapyTypeName { get; set; } = "?";
    public int? TherapyTypeColor { get; set; }
    public int? TherapyTypeCategory { get; set; }
    public int? TherapistId { get; set; }
    public string? TherapistName { get; set; }
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
    public int DurationSlots { get; set; }
    public int Status { get; set; }
  }

  [HttpGet("/Giorno/Slot/{id}")]
  public IActionResult GiornoSlotDetail(int id) {
    var currentTherapistId = GetCurrentTherapistId();

    if (currentTherapistId == null) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var isAdmin = IsCurrentUserAccettazione();

    if (!isAdmin && slot.TherapistId != currentTherapistId) {
      return Forbid();
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
    var therapy = part != null ? _db.Therapies.Find(part.TherapyId) : null;
    var patient = therapy != null ? _db.Patients.Find(therapy.PatientId) : null;
    var therapistName = slot.TherapistId.HasValue ? _db.Therapists.Find(slot.TherapistId.Value)?.Name : null;

    return Ok(new SlotDetailDto {
      Id = slot.Id,
      TherapyId = therapy?.Id,
      PatientName = patient?.Name ?? "?",
      TherapyTypeName = type?.Name ?? "?",
      TherapyTypeColor = type?.Color,
      TherapyTypeCategory = type?.Category,
      TherapistId = slot.TherapistId,
      TherapistName = therapistName,
      Date = slot.Date,
      TimeSlot = slot.TimeSlot,
      DurationSlots = type != null ? GetSpanSlots(type) : 1,
      Status = slot.Status
    });
  }

  public class RipianificaProposeDto {
    public int OriginalSlotId { get; set; }
    public int TherapyPartId { get; set; }
    public DateOnly ProposedDate { get; set; }
    public int ProposedTimeSlot { get; set; }
    public int TherapistId { get; set; }
    public string TherapistName { get; set; } = "?";
    public int DurationSlots { get; set; }
    public string PatientName { get; set; } = "?";
    public string TherapyTypeName { get; set; } = "?";
    public int? TherapyTypeColor { get; set; }
  }

  // Ripianifica step 1: compute a proposed date/time, nothing is written yet.
  // Logic (CPU): from the clicked slot's own Part, every non-Rescheduled slot from
  // that slot's date onward - take the most common TimeSlot (mode, ties -> earliest),
  // then walk forward day by day from the day after the last of those slots until a
  // day passes the normal placement checks for that same therapist.
  [HttpGet("/Giorno/Slot/{id}/RipianificaPropose")]
  public IActionResult GiornoSlotRipianificaPropose(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    if (!slot.TherapistId.HasValue) {
      return BadRequest(new { message = "Sposta a fine terapia richiede un terapista assegnato." });
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
    var therapy = part != null ? _db.Therapies.Find(part.TherapyId) : null;
    var therapist = _db.Therapists.Find(slot.TherapistId.Value);

    if (part == null || type == null || therapy == null || therapist == null) {
      return NotFound();
    }

    var patient = _db.Patients.Find(therapy.PatientId);
    var span = GetSpanSlots(type);

    var relevantSlots = _db.TherapySlots
      .Where(s => s.TherapyPartId == part.Id && s.Date >= slot.Date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    if (relevantSlots.Count == 0) {
      relevantSlots.Add(slot);
    }

    var modeTimeSlot = relevantSlots
      .GroupBy(s => s.TimeSlot)
      .OrderByDescending(g => g.Count())
      .ThenBy(g => g.Key)
      .First().Key;

    var lastDate = relevantSlots.Max(s => s.Date);

    var searchDate = lastDate.AddDays(1);
    var maxSearchDate = lastDate.AddDays(180); // safety cap against an infinite scan
    DateOnly? foundDate = null;

    while (searchDate <= maxSearchDate) {
      if (IsTherapistFreeForSpan(therapist.Id, searchDate, modeTimeSlot, span)
          && !HasPatientConflict(therapy.PatientId, searchDate, modeTimeSlot, span, part.TherapyTypeId)) {
        foundDate = searchDate;
        break;
      }
      searchDate = searchDate.AddDays(1);
    }

    if (foundDate == null) {
      return BadRequest(new { message = "Nessuno slot disponibile trovato per il terapista nei prossimi 6 mesi." });
    }

    return Ok(new RipianificaProposeDto {
      OriginalSlotId = slot.Id,
      TherapyPartId = part.Id,
      ProposedDate = foundDate.Value,
      ProposedTimeSlot = modeTimeSlot,
      TherapistId = therapist.Id,
      TherapistName = therapist.Name,
      DurationSlots = span,
      PatientName = patient?.Name ?? "?",
      TherapyTypeName = type.Name,
      TherapyTypeColor = type.Color
    });
  }

  public class RipianificaConfirmRequest {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
  }

  // Ripianifica step 2: the user may have dragged the proposal to a different
  // cell - re-validate the FINAL date/time from scratch (never trusts the
  // proposal from step 1), then create the new slot and mark the original
  // Rescheduled -> RescheduledToId, same convention as every other reschedule path.
  [HttpPost("/Giorno/Slot/{id}/RipianificaConfirm")]
  public IActionResult GiornoSlotRipianificaConfirm(int id, [FromBody] RipianificaConfirmRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    if (!slot.TherapistId.HasValue) {
      return BadRequest(new { message = "Sposta a fine terapia richiede un terapista assegnato." });
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
    var therapy = part != null ? _db.Therapies.Find(part.TherapyId) : null;

    if (part == null || type == null || therapy == null) {
      return NotFound();
    }

    var span = GetSpanSlots(type);

    if (!IsTherapistFreeForSpan(slot.TherapistId.Value, request.Date, request.TimeSlot, span)
        || HasPatientConflict(therapy.PatientId, request.Date, request.TimeSlot, span, part.TherapyTypeId)) {
      return BadRequest(new { message = "Lo slot scelto non è più disponibile." });
    }

    var newSlot = BuildTherapySlot(part.Id, request.Date, request.TimeSlot, slot.TherapistId);
    _db.TherapySlots.Add(newSlot);
    _db.SaveChanges();

    slot.Status = TherapySlotStatus.Rescheduled;
    slot.RescheduledToId = newSlot.Id;
    _db.SaveChanges();

    return Ok(new { newSlotId = newSlot.Id });
  }

  [HttpGet("/Giorno/Slot/{id}/AvailableTherapists")]
  public IActionResult GiornoSlotAvailableTherapists(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

    if (type == null) {
      return NotFound();
    }

    var span = GetSpanSlots(type);
    var patientCache = GetPatientEntityCache();
    var partCache = GetPartCache();
    var therapyCache = GetTherapyCache();

    const int halfDayBoundary = 52; // 13:00 - same AM/PM convention Vacation.AMPM uses
    var isMorning = slot.TimeSlot < halfDayBoundary;
    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();
    var halfStart = isMorning ? clinicStart : Math.Max(clinicStart, halfDayBoundary);
    var halfEnd = isMorning ? Math.Min(clinicEnd, halfDayBoundary) : clinicEnd;

    // CPU's call: show every active therapist, not just the category-matched
    // pool, so a mismatched one still appears (grayed, with its own reason)
    // instead of silently not being listed at all.
    var allTherapists = _db.Therapists
      .Where(t => t.IsActive == 1 && (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0)
      .OrderBy(t => t.Name)
      .ToList();

    // status: "current" (assigned already, no button) | "green" (free at the
    // exact same time - Riassegna) | "yellow" (not free right now, but has some
    // other free moment this half-day - Ripianifica) | "red" (category mismatch,
    // or genuinely nothing free all half-day).
    var result = allTherapists.Select(t => {
      if (t.Id == slot.TherapistId) {
        return new { t.Id, t.Name, status = "current", reason = (string?)null };
      }

      // Same category-match rules as GetPalestraDropdownTherapists /
      // GetRepartoCapableAndCoveringTherapists: pure-Reparto (2) is Reparto-only,
      // everyone else (0/4/6) can do Palestra; Reparto needs the Reparto bit or
      // the "aiuto" bit.
      var isReparto = (t.OperatingArea & TherapistOperatingArea.Reparto) != 0;
      var helpsOtherArea = (t.OperatingArea & TherapistOperatingArea.HelpsOtherArea) != 0;
      var categoryMatches = type.Category == TherapyCategory.Palestra
        ? t.OperatingArea != TherapistOperatingArea.Reparto
        : isReparto || helpsOtherArea;

      if (!categoryMatches) {
        var mismatchReason = type.Category == TherapyCategory.Palestra ? "Non fa palestra" : "Non fa reparto";
        return new { t.Id, t.Name, status = "red", reason = (string?)mismatchReason };
      }

      if (IsTherapistFreeForSpan(t.Id, slot.Date, slot.TimeSlot, span)) {
        return new { t.Id, t.Name, status = "green", reason = (string?)null };
      }

      string blockedReason;
      if (IsBlockedByVacation(t.Id, slot.Date, slot.TimeSlot, span)) {
        blockedReason = "In vacanza";
      } else if (!IsWithinAvailability(t.Id, slot.Date, slot.TimeSlot, span)) {
        blockedReason = "Fuori orario normale di lavoro";
      } else {
        var conflictSlot = FindTherapistConflictSlot(t.Id, slot.Date, slot.TimeSlot, span);
        var conflictPart = conflictSlot != null && partCache.ContainsKey(conflictSlot.TherapyPartId) ? partCache[conflictSlot.TherapyPartId] : null;
        var conflictTherapy = conflictPart != null && therapyCache.ContainsKey(conflictPart.TherapyId) ? therapyCache[conflictPart.TherapyId] : null;
        var conflictPatientName = conflictTherapy != null && patientCache.ContainsKey(conflictTherapy.PatientId)
          ? patientCache[conflictTherapy.PatientId].Name
          : "?";
        blockedReason = "Impegnato con " + conflictPatientName;
      }

      // Strict hours only (no overtime margin) when looking for a fallback slot
      // elsewhere in the half-day - CPU's call.
      var hasOtherFreeSlot = false;
      for (var s = halfStart; s + span <= halfEnd; s++) {
        if (IsWithinDeclaredAvailability(t.Id, slot.Date, s, span)
            && !IsBlockedByVacation(t.Id, slot.Date, s, span)
            && !HasTherapistConflict(t.Id, slot.Date, s, span)) {
          hasOtherFreeSlot = true;
          break;
        }
      }

      return new { t.Id, t.Name, status = hasOtherFreeSlot ? "yellow" : "red", reason = (string?)blockedReason };
    }).ToList();

    return Ok(result);
  }

  public class GiornoSlotChangeTherapistRequest {
    public int TherapistId { get; set; }
  }

  [HttpPost("/Giorno/Slot/{id}/ChangeTherapist")]
  public IActionResult GiornoSlotChangeTherapist(int id, [FromBody] GiornoSlotChangeTherapistRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

    if (type == null) {
      return NotFound();
    }

    if (request.TherapistId != slot.TherapistId) {
      var span = GetSpanSlots(type);

      if (!IsTherapistFreeForSpan(request.TherapistId, slot.Date, slot.TimeSlot, span)) {
        return BadRequest(new { message = "Il terapista non è disponibile in questo orario." });
      }
    }

    slot.TherapistId = request.TherapistId;
    _db.SaveChanges();

    return Ok();
  }

  // Compact same-half-day (morning/afternoon, split at 13:00 - same AM/PM
  // convention Vacation.AMPM already uses) schedule for a candidate therapist,
  // shown when "Ripianifica" is picked - lets staff see what else that
  // therapist has going on and choose a genuinely free spot (strict declared
  // hours only, no overtime margin - CPU's call) to move this session into.
  [HttpGet("/Giorno/Slot/{id}/TherapistHalfDayPreview")]
  public IActionResult GiornoSlotTherapistHalfDayPreview(int id, int therapistId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var therapist = _db.Therapists.Find(therapistId);

    if (therapist == null) {
      return NotFound();
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var slotType = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

    if (slotType == null) {
      return NotFound();
    }

    var span = GetSpanSlots(slotType);

    const int halfDayBoundary = 52; // 13:00
    var isMorning = slot.TimeSlot < halfDayBoundary;
    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();
    var rangeStart = isMorning ? clinicStart : Math.Max(clinicStart, halfDayBoundary);
    var rangeEnd = isMorning ? Math.Min(clinicEnd, halfDayBoundary) : clinicEnd;

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();

    var daySlots = _db.TherapySlots
      .Where(s => s.TherapistId == therapistId && s.Date == slot.Date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    var items = daySlots.Select(s => {
      var itemPart = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var itemType = itemPart != null && typeCache.ContainsKey(itemPart.TherapyTypeId) ? typeCache[itemPart.TherapyTypeId] : null;
      var itemTherapy = itemPart != null && therapyCache.ContainsKey(itemPart.TherapyId) ? therapyCache[itemPart.TherapyId] : null;
      var patientName = itemTherapy != null && patientCache.ContainsKey(itemTherapy.PatientId) ? patientCache[itemTherapy.PatientId].Name : "?";

      return new {
        slotId = s.Id,
        timeSlot = s.TimeSlot,
        durationSlots = itemType != null ? GetSpanSlots(itemType) : 1,
        patientName,
        therapyTypeLabel = itemType != null ? (string.IsNullOrEmpty(itemType.Abbreviazione) ? itemType.Name : itemType.Abbreviazione) : "?",
        isOriginalSlot = s.Id == id
      };
    }).ToList();

    // Free cells: within the therapist's STRICT declared hours (no overtime),
    // not vacation-blocked, and not already covered by one of daySlots above -
    // computed here rather than trusting raw availability windows alone.
    var freeSlots = new List<int>();
    for (var s = rangeStart; s + span <= rangeEnd; s++) {
      if (IsWithinDeclaredAvailability(therapistId, slot.Date, s, span)
          && !IsBlockedByVacation(therapistId, slot.Date, s, span)
          && !HasTherapistConflict(therapistId, slot.Date, s, span)) {
        freeSlots.Add(s);
      }
    }

    return Ok(new {
      rangeStart,
      rangeEnd,
      spanSlots = span,
      freeSlots,
      items
    });
  }

  public class ReassignRequest {
    public int TherapistId { get; set; }
    public int TimeSlot { get; set; }
  }

  // Reassign a slot's therapist, and optionally its time (via the half-day
  // preview above). No previous-slot history is kept anywhere - the same
  // TherapySlot row is just updated in place (CPU's call: "it is not a
  // rescheduled, it is reassigned"). If this changes BOTH the therapist and the
  // time together, marks it important with Type=TherapistChangeNotification -
  // the one alert type that only ever exists as a stored row (a same-time swap
  // or a same-therapist move alone doesn't need the patient told).
  [HttpPost("/Giorno/Slot/{id}/Reassign")]
  public IActionResult GiornoSlotReassign(int id, [FromBody] ReassignRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

    if (type == null || part == null) {
      return NotFound();
    }

    var newTherapist = _db.Therapists.Find(request.TherapistId);

    if (newTherapist == null) {
      return NotFound();
    }

    var span = GetSpanSlots(type);

    // Excludes the slot's own current booking, same reasoning as
    // AvailableTherapists - reassigning a slot shouldn't false-positive against
    // itself.
    var conflictSlot = FindTherapistConflictSlot(request.TherapistId, slot.Date, request.TimeSlot, span);
    if (conflictSlot != null && conflictSlot.Id != slot.Id) {
      return BadRequest(new { message = "Il terapista ha già un impegno in quell'orario." });
    }

    var oldTherapistId = slot.TherapistId;
    var oldTimeSlot = slot.TimeSlot;

    slot.TherapistId = request.TherapistId;
    slot.TimeSlot = request.TimeSlot;
    _db.SaveChanges();

    if (oldTherapistId.HasValue && oldTherapistId.Value != request.TherapistId && oldTimeSlot != request.TimeSlot) {
      var key = ComputeAlertKey("therapistChangeNotification|" + slot.Id + "|" + DateTime.Now.Ticks);
      _db.AlertMarkedImportants.Add(new AlertMarkedImportant {
        Key = key,
        MarkedAt = DateTime.Now,
        Type = AlertType.TherapistChangeNotification,
        SlotId = slot.Id
      });
      _db.SaveChanges();
    }

    return Ok();
  }

  // Deliberately no ConfirmName check here (unlike the other Remove endpoints) -
  // CPU wants this one to be a lightweight Sì/No confirm on the client instead.
  [HttpPost("/Giorno/Slot/Remove/{id}")]
  public IActionResult GiornoSlotRemove(int id) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    _db.TherapySlots.Remove(slot);
    _db.SaveChanges();

    return Ok();
  }

  public class GiornoSlotChangeStatusRequest {
    public int Status { get; set; }
  }

  [HttpPost("/Giorno/Slot/{id}/ChangeStatus")]
  public IActionResult GiornoSlotChangeStatus(int id, [FromBody] GiornoSlotChangeStatusRequest request) {
    var currentTherapistId = GetCurrentTherapistId();

    if (currentTherapistId == null) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var isAdmin = IsCurrentUserAccettazione();
    var isAssignedTherapist = !isAdmin && slot.TherapistId == currentTherapistId;

    if (!isAdmin && !isAssignedTherapist) {
      return Forbid();
    }

    var allowedStatuses = isAdmin
      ? new[] { TherapySlotStatus.ToBeDone, TherapySlotStatus.Done, TherapySlotStatus.PatientAbsent }
      : new[] { TherapySlotStatus.Done, TherapySlotStatus.PatientAbsent };

    if (!allowedStatuses.Contains(request.Status)) {
      return BadRequest();
    }

    slot.Status = request.Status;
    _db.SaveChanges();

    return Ok();
  }

  public class GiornoSlotMoveRequest {
    public DateOnly Date { get; set; }
    public int TimeSlot { get; set; }
  }

  // Drag-and-drop target for Giorno/Settimana (Admin only). Full restrictions, same
  // as Pianificazione: Palestra/Reparto-Active (therapist assigned) re-checks
  // vacation+availability+conflict via IsTherapistFreeForSpan; Reparto-Light (no
  // therapist) re-checks capacity saturation the same way RepartoAutoFill's hard
  // block does. Any rejection (including dropping back onto the slot's own current
  // position) makes no DB change at all - the client always just redraws from
  // truth after this call, so a rejection reads as the overlay silently sliding
  // back to where it was.
  [HttpPost("/Giorno/Slot/{id}/Move")]
  public IActionResult GiornoSlotMove(int id, [FromBody] GiornoSlotMoveRequest request) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

    if (type == null) {
      return NotFound();
    }

    var span = GetSpanSlots(type);

    if (slot.TherapistId.HasValue) {
      if (!IsTherapistFreeForSpan(slot.TherapistId.Value, request.Date, request.TimeSlot, span)) {
        return BadRequest();
      }
    } else {
      var (repartoRate, coveringRate) = GetRepartoRates();
      var relevantTherapists = GetRepartoCapableAndCoveringTherapists();

      for (var s = request.TimeSlot; s < request.TimeSlot + span; s++) {
        if (IsRepartoSlotSaturated(request.Date, s, repartoRate, coveringRate, relevantTherapists)) {
          return BadRequest();
        }
      }
    }

    slot.Date = request.Date;
    slot.TimeSlot = request.TimeSlot;
    _db.SaveChanges();

    return Ok();
  }

  // Diagnostic for the Reparto capacity numbers shown on empty cells in
  // Giorno/Settimana - Admin only. Re-runs the exact same per-therapist checks as
  // ComputeRepartoCapacityAndDemand (PianificazioneReparto.cs), but records *why*
  // each relevant therapist is or isn't counted, instead of just the final sum -
  // CPU: "clicking a cell in week view should give us all info to debug."
  private class CapacityDebugTherapistDto {
    public int Id { get; set; }
    public string Name { get; set; } = "?";
    public bool IsRepartoCapable { get; set; }
    public bool Counted { get; set; }
    public string Reason { get; set; } = "";
  }

  [HttpGet("/Giorno/SlotCapacityDebug")]
  public IActionResult GiornoSlotCapacityDebug(DateOnly date, int timeSlot) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();
    var (repartoRate, coveringRate) = GetRepartoRates();

    var therapistBreakdown = relevantTherapists.Select(t => {
      var isRepartoCapable = (t.OperatingArea & TherapistOperatingArea.Reparto) != 0;
      var rate = isRepartoCapable ? repartoRate : coveringRate;

      if (IsBlockedByVacation(t.Id, date, timeSlot, 1)) {
        return new CapacityDebugTherapistDto { Id = t.Id, Name = t.Name, IsRepartoCapable = isRepartoCapable, Counted = false, Reason = "In ferie/chiusura" };
      }

      if (!IsWithinAvailability(t.Id, date, timeSlot, 1)) {
        return new CapacityDebugTherapistDto { Id = t.Id, Name = t.Name, IsRepartoCapable = isRepartoCapable, Counted = false, Reason = "Fuori disponibilità settimanale" };
      }

      if (IsTherapistBusyWithActiveSession(t.Id, date, timeSlot)) {
        return new CapacityDebugTherapistDto { Id = t.Id, Name = t.Name, IsRepartoCapable = isRepartoCapable, Counted = false, Reason = "Occupato in seduta attiva" };
      }

      return new CapacityDebugTherapistDto { Id = t.Id, Name = t.Name, IsRepartoCapable = isRepartoCapable, Counted = true, Reason = "Conteggiato (" + (15.0 / rate).ToString("0.##") + ")" };
    }).ToList();

    var (capacity, demand) = ComputeRepartoCapacityAndDemand(date, timeSlot, repartoRate, coveringRate, relevantTherapists);

    // existingCount here matches the grid's own badge semantics (all Reparto-category
    // sessions, Light + Active) - not the same as `demand` above, which is Light-only.
    var typeCache = GetTypeCache();
    var partCache = GetPartCache();
    var existingCount = _db.TherapySlots
      .Where(s => s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList()
      .Count(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        if (type == null || type.Category != TherapyCategory.Reparto) {
          return false;
        }
        var span = GetSpanSlots(type);
        return timeSlot >= s.TimeSlot && timeSlot < s.TimeSlot + span;
      });

    return Ok(new {
      date,
      timeSlot,
      capacity,
      demand,
      existingCount,
      repartoRate,
      coveringRate,
      therapists = therapistBreakdown
    });
  }

  // ---- Settimana endpoints (Mon-Fri, shared time axis) -------------------------

  [HttpGet("/Settimana/TherapistData")]
  public IActionResult SettimanaTherapistData(int therapistId, DateOnly weekStart) {
    if (GetCurrentTherapistId() == null) {
      return Forbid();
    }

    var therapist = _db.Therapists.Find(therapistId);

    if (therapist == null) {
      return NotFound();
    }

    var dates = GetWeekdayDates(weekStart);
    var perDayAvailability = dates.Select(d => GetTherapistDayAvailability(therapistId, d)).ToList();

    var hasAvailability = perDayAvailability.Any(a => a.Count > 0);
    int gridStart = 0, gridEnd = 0;

    if (hasAvailability) {
      var allRanges = perDayAvailability.Where(a => a.Count > 0).SelectMany(a => a).ToList();
      gridStart = allRanges.Min(a => a.StartTime);
      gridEnd = allRanges.Max(a => a.EndTime);
    }

    var vacations = GetTherapistVacations(therapistId);

    var slotsInRange = GetSlotsInDateRange(dates);
    var ctx = BuildCapacityComputationContext(slotsInRange);
    var capacityWarningThreshold = GetRepartoCapacityWarningThreshold();

    var days = !hasAvailability ? new List<object>() : dates.Select((d, i) => (object)new {
      date = d,
      availability = perDayAvailability[i],
      capacityGrid = BuildCapacityGridInMemory(ctx, d, gridStart, gridEnd, includeExistingCount: true),
      slots = BuildTherapistSlotsFromBatch(slotsInRange, therapistId, d)
    }).ToList();

    return Ok(new {
      requestWeekStart = weekStart.ToString("yyyy-MM-dd"),
      requestTherapistId = therapistId,
      name = therapist.Name,
      hasAvailability,
      gridStart,
      gridEnd,
      vacations,
      capacityWarningThreshold,
      days
    });
  }

  [HttpGet("/Settimana/RepartoData")]
  public IActionResult SettimanaRepartoData(int sex, DateOnly weekStart) {
    if (GetCurrentTherapistId() == null) {
      return Forbid();
    }

    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();
    var vacations = GetGlobalVacations();
    var dates = GetWeekdayDates(weekStart);

    var slotsInRange = GetSlotsInDateRange(dates);
    var ctx = BuildCapacityComputationContext(slotsInRange);
    var capacityWarningThreshold = GetRepartoCapacityWarningThreshold();

    // No existingCount here either, same reasoning as GiornoRepartoData above.
    var days = dates.Select(d => new {
      date = d,
      capacityGrid = BuildCapacityGridInMemory(ctx, d, clinicStart, clinicEnd, includeExistingCount: false),
      slots = BuildRepartoSlotsFromBatch(slotsInRange, sex, d)
    }).ToList();

    return Ok(new {
      requestWeekStart = weekStart.ToString("yyyy-MM-dd"),
      requestSex = sex,
      name = sex == PatientSex.Female ? "Reparto Donne" : "Reparto Uomini",
      gridStart = clinicStart,
      gridEnd = clinicEnd,
      vacations,
      capacityWarningThreshold,
      days
    });
  }
}
