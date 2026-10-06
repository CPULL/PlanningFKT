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
    // 0 = Light (passiva), 1 = Active - Settimana's Reparto count badge splits on it.
    public int? TherapyTypeExecutionType { get; set; }
    public int? TherapyTypeColor { get; set; }
    public string PatientName { get; set; } = "?";
    public int? TherapistId { get; set; }
    public string? TherapistName { get; set; }
    public int? PatientId { get; set; }
    // Patient has a "specialistica" PDF on disk - Giorno shows a quick open button.
    public bool HasDocument { get; set; }
    // TherapySlotStatus - Settimana "Gestisci paziente" shows it like Presenze.
    public int Status { get; set; }
    // Moved Assente session (see TherapySlot.WasAbsent) and where it went.
    public bool MovedAbsent { get; set; }
    public string? MovedToDate { get; set; }
  }

  private class DayAvailabilityRangeDto {
    public int StartTime { get; set; }
    public int EndTime { get; set; }
    // Non-null = this window is pinned to one pure area, different from the
    // therapist's main OperatingArea (CPU's call) - used by Giorno/Settimana to
    // show a "not normal" texture on empty cells in that window.
    public int? OverrideOperatingArea { get; set; }
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
          g => g.OrderBy(a => a.StartTime).Select(a => new DayAvailabilityRangeDto { StartTime = a.StartTime, EndTime = a.EndTime, OverrideOperatingArea = a.OverrideOperatingArea }).ToList());
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

      var span = GetSlotSpan(s, type);

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
    public int? TherapyTypeExecutionType { get; set; }
    public int? TherapyTypeColor { get; set; }
    public string PatientName { get; set; } = "?";
    public int? PatientId { get; set; }
    // Patient has a "specialistica" PDF on disk - Giorno shows a quick open button.
    public bool HasDocument { get; set; }
    // TherapySlotStatus - a Fatto/Assente session can't be dragged.
    public int Status { get; set; }
    // Moved Assente session (see TherapySlot.WasAbsent) and where it went.
    public bool MovedAbsent { get; set; }
    public string? MovedToDate { get; set; }
  }

  // "Does this patient's specialistica PDF exist on disk" - memoized per request,
  // so a day/week grid checks each patient's file once, not once per session.
  private Dictionary<int, bool>? _patientHasDocumentCache;

  private bool PatientHasDocument(Patient? patient) {
    if (patient == null || string.IsNullOrEmpty(patient.DocumentFileName)) {
      return false;
    }

    _patientHasDocumentCache ??= new Dictionary<int, bool>();

    if (!_patientHasDocumentCache.TryGetValue(patient.Id, out var exists)) {
      exists = System.IO.File.Exists(System.IO.Path.Combine(GetPatientDocumentFolder(), patient.DocumentFileName));
      _patientHasDocumentCache[patient.Id] = exists;
    }

    return exists;
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
        var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId)
          ? patientCache[slotTherapy.PatientId]
          : null;
        var patientName = patient != null ? patient.Name : "?";

        return new TherapistDaySlotDto {
          Id = s.Id,
          TimeSlot = s.TimeSlot,
          DurationSlots = GetSlotSpan(s, type),
          TherapyTypeId = part != null ? part.TherapyTypeId : (int?)null,
          TherapyTypeName = type != null ? type.Name : "?",
          TherapyTypeCategory = type != null ? type.Category : (int?)null,
          TherapyTypeExecutionType = type != null ? type.Type : (int?)null,
          TherapyTypeColor = type != null ? type.Color : (int?)null,
          PatientName = patientName,
          PatientId = patient?.Id,
          HasDocument = PatientHasDocument(patient),
          Status = s.Status
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
        DurationSlots = GetSlotSpan(x.Slot, x.Type),
        TherapyTypeId = x.Part!.TherapyTypeId,
        TherapyTypeName = x.Type!.Name,
        TherapyTypeCategory = x.Type!.Category,
        TherapyTypeExecutionType = x.Type!.Type,
        TherapyTypeColor = x.Type!.Color,
        TherapistId = x.Slot.TherapistId,
        TherapistName = x.Slot.TherapistId.HasValue && therapistNameCache.ContainsKey(x.Slot.TherapistId.Value)
          ? therapistNameCache[x.Slot.TherapistId.Value]
          : null,
        PatientName = x.Patient!.Name,
        PatientId = x.Patient!.Id,
        HasDocument = PatientHasDocument(x.Patient),
        Status = x.Slot.Status
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
        DurationSlots = GetSlotSpan(x.Slot, x.Type),
        TherapyTypeId = x.Part!.TherapyTypeId,
        TherapyTypeName = x.Type!.Name,
        TherapyTypeCategory = x.Type!.Category,
        TherapyTypeExecutionType = x.Type!.Type,
        TherapyTypeColor = x.Type!.Color,
        TherapistId = x.Slot.TherapistId,
        TherapistName = x.Slot.TherapistId.HasValue && therapistNameCache.ContainsKey(x.Slot.TherapistId.Value)
          ? therapistNameCache[x.Slot.TherapistId.Value]
          : null,
        PatientName = x.Patient!.Name,
        PatientId = x.Patient!.Id,
        HasDocument = PatientHasDocument(x.Patient)
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
    var moved = GetMovedAbsentSlotsInDateRange(dates);
    var movedTo = GetMovedToDates(moved);
    slots.AddRange(BuildTherapistSlotsFromBatch(moved, therapistId, date).Select(d => MarkMovedAbsent(d, movedTo)));

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
    var moved = GetMovedAbsentSlotsInDateRange(dates);
    var movedTo = GetMovedToDates(moved);
    slots.AddRange(BuildRepartoSlotsFromBatch(moved, sex, date).Select(d => MarkMovedAbsent(d, movedTo)));

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
    public int? PatientId { get; set; }
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
    // Only meaningful when the TherapyType allows Ginnastica Attiva - null
    // means "this type doesn't support it at all" (hide the UI entirely),
    // 0 means "supported but not attached to this slot."
    public bool AllowsGinnasticaAttiva { get; set; }
    public int GinnasticaAttivaSlots { get; set; }
    public bool HasDocument { get; set; }
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

    // Any authenticated user can VIEW any slot's details and jump to the
    // patient - only the modify actions (reassign/status/delete) stay
    // restricted to admin or the assigned therapist, enforced client-side and
    // on each of those endpoints individually (CPU: a therapist "should be able
    // to see the details and also jump to the client").

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;
    var therapy = part != null ? _db.Therapies.Find(part.TherapyId) : null;
    var patient = therapy != null ? _db.Patients.Find(therapy.PatientId) : null;
    var therapistName = slot.TherapistId.HasValue ? _db.Therapists.Find(slot.TherapistId.Value)?.Name : null;

    var hasDocument = patient != null && !string.IsNullOrEmpty(patient.DocumentFileName)
      && System.IO.File.Exists(System.IO.Path.Combine(GetPatientDocumentFolder(), patient.DocumentFileName));

    return Ok(new SlotDetailDto {
      Id = slot.Id,
      TherapyId = therapy?.Id,
      PatientId = therapy?.PatientId,
      PatientName = patient?.Name ?? "?",
      TherapyTypeName = type?.Name ?? "?",
      TherapyTypeColor = type?.Color,
      TherapyTypeCategory = type?.Category,
      TherapistId = slot.TherapistId,
      TherapistName = therapistName,
      Date = slot.Date,
      TimeSlot = slot.TimeSlot,
      DurationSlots = GetSlotSpan(slot, type),
      Status = slot.Status,
      AllowsGinnasticaAttiva = type != null && type.AllowsGinnasticaAttiva != 0,
      GinnasticaAttivaSlots = slot.GinnasticaAttivaSlots,
      HasDocument = hasDocument
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

    slot.WasAbsent = slot.Status == TherapySlotStatus.PatientAbsent;
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

    var span = GetSlotSpan(slot, type);
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
      // the "aiuto" bit. Evaluated against the EFFECTIVE area at a given moment
      // (CPU's call: a per-window override can pin part of the day to a single
      // pure area, different from the therapist's main OperatingArea).
      bool CategoryMatchesAt(int effectiveArea) {
        var effIsReparto = (effectiveArea & TherapistOperatingArea.Reparto) != 0;
        var effHelpsOtherArea = (effectiveArea & TherapistOperatingArea.HelpsOtherArea) != 0;
        return type.Category == TherapyCategory.Palestra
          ? effectiveArea != TherapistOperatingArea.Reparto
          : effIsReparto || effHelpsOtherArea;
      }

      var effectiveAreaNow = GetEffectiveOperatingArea(t, slot.Date, slot.TimeSlot);
      var categoryMatches = CategoryMatchesAt(effectiveAreaNow);

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
        if (!CategoryMatchesAt(GetEffectiveOperatingArea(t, slot.Date, s))) {
          continue;
        }
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
      var span = GetSlotSpan(slot, type);

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

    var span = GetSlotSpan(slot, slotType);

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
        durationSlots = GetSlotSpan(s, itemType),
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

    var span = GetSlotSpan(slot, type);

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

    var therapyId = _db.TherapyParts.Where(p => p.Id == slot.TherapyPartId).Select(p => p.TherapyId).FirstOrDefault();

    DetachRescheduledReferences(new List<int> { slot.Id });
    _db.TherapySlots.Remove(slot);
    _db.SaveChanges();

    RevertToBeScheduledIfEmpty(therapyId);

    return Ok();
  }

  // A moved session ("Sposta a fine terapia", Gestisci assenza) stays in the DB as
  // Rescheduled with RescheduledToId pointing at its replacement, and that FK is
  // Restrict - deleting the replacement while something still points at it made
  // MySQL reject the delete (500). Clear those pointers first (the original keeps
  // its Rescheduled status, it just no longer links anywhere).
  private void DetachRescheduledReferences(List<int> slotIdsToDelete) {
    if (slotIdsToDelete.Count == 0) {
      return;
    }

    var referencing = _db.TherapySlots
      .Where(s => s.RescheduledToId.HasValue && slotIdsToDelete.Contains(s.RescheduledToId.Value))
      .ToList();

    if (referencing.Count == 0) {
      return;
    }

    foreach (var s in referencing) {
      s.RescheduledToId = null;
    }

    _db.SaveChanges();
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

    // Any logged-in therapist can act on ANY therapist's slot (CPU: one shared
    // tablet for ~20 people, nobody logs out/in between patients). The
    // currentTherapistId null check above already rejects anonymous calls.
    var isAdmin = IsCurrentUserAccettazione();

    var allowedStatuses = isAdmin
      ? new[] { TherapySlotStatus.ToBeDone, TherapySlotStatus.Done, TherapySlotStatus.PatientAbsent }
      : new[] { TherapySlotStatus.Done, TherapySlotStatus.PatientAbsent };

    if (!allowedStatuses.Contains(request.Status)) {
      return BadRequest();
    }

    // A moved session (incl. a moved Assente one) is read-only.
    if (slot.Status == TherapySlotStatus.Rescheduled) {
      return BadRequest(new { message = "Seduta spostata: non si può modificare." });
    }

    var wasDone = slot.Status == TherapySlotStatus.Done;
    slot.Status = request.Status;
    _db.SaveChanges();
    RecomputeTherapyCompletion(slot.TherapyPartId, true, wasDone && slot.Status != TherapySlotStatus.Done);

    return Ok();
  }

  public class GiornoSlotSetGinnasticaAttivaRequest {
    // Duration in 15-min slots (0 = remove); sign is carried separately via
    // IsBefore so the frontend UI can stay a plain duration + before/after
    // toggle rather than asking the person to think in signed numbers.
    public int DurationSlots { get; set; }
    public bool IsBefore { get; set; }
  }

  // Manual override for Ginnastica Attiva - both Accettazione and the assigned
  // therapist can change it at any time (CPU's call), same permission shape as
  // ChangeStatus. Only valid for a slot whose TherapyType actually allows GA.
  [HttpPost("/Giorno/Slot/{id}/SetGinnasticaAttiva")]
  public IActionResult GiornoSlotSetGinnasticaAttiva(int id, [FromBody] GiornoSlotSetGinnasticaAttivaRequest request) {
    var currentTherapistId = GetCurrentTherapistId();

    if (currentTherapistId == null) {
      return Forbid();
    }

    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    // Any logged-in therapist can act on ANY therapist's slot (CPU: one shared
    // tablet for ~20 people, nobody logs out/in between patients). The
    // currentTherapistId null check above already rejects anonymous calls.

    var part = _db.TherapyParts.Find(slot.TherapyPartId);
    var type = part != null ? _db.TherapyTypes.Find(part.TherapyTypeId) : null;

    if (type == null || type.AllowsGinnasticaAttiva == 0) {
      return BadRequest(new { message = "Questo tipo di terapia non prevede Ginnastica Attiva." });
    }

    if (request.DurationSlots < 0) {
      return BadRequest();
    }

    slot.GinnasticaAttivaSlots = request.DurationSlots == 0
      ? 0
      : (request.IsBefore ? -request.DurationSlots : request.DurationSlots);

    _db.SaveChanges();

    return Ok(new { ginnasticaAttivaSlots = slot.GinnasticaAttivaSlots });
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

    // CPU: a session marked Fatto or Assente can't be dragged.
    if (slot.Status != TherapySlotStatus.ToBeDone) {
      return BadRequest(new { message = "Una seduta segnata come Fatto o Assente non si può spostare." });
    }

    var span = GetSlotSpan(slot, type);

    // Dropped back where it already was - nothing to do, nothing to report.
    if (request.Date == slot.Date && request.TimeSlot == slot.TimeSlot) {
      return BadRequest();
    }

    // Every other rejection carries a message the client shows (CPU: never fail
    // silently). The slot's own current booking is excluded, so shifting it by
    // less than its own length isn't rejected as a conflict with itself.
    string? reason;

    if (slot.TherapistId.HasValue) {
      reason = DescribeTherapistBlock(slot.TherapistId.Value, request.Date, request.TimeSlot, span, slot.Id);
    } else {
      var (repartoRate, coveringRate) = GetRepartoRates();
      var relevantTherapists = GetRepartoCapableAndCoveringTherapists();
      reason = DescribeRepartoSaturation(request.Date, request.TimeSlot, span, repartoRate, coveringRate, relevantTherapists);
    }

    if (reason != null) {
      return BadRequest(new { message = "Spostamento non possibile - " + WithDate(request.Date, reason) + "." });
    }

    slot.Date = request.Date;
    slot.TimeSlot = request.TimeSlot;
    _db.SaveChanges();

    return Ok();
  }

  // Diagnostic "full view" of a single cell (date + 15-minute slot) - Admin only,
  // opened by clicking any cell in Giorno/Settimana/Pianifica while "Mostra
  // informazioni celle" is on. Lists EVERY active therapist (not just the
  // Reparto-relevant ones) with what they're doing at that moment: on vacation,
  // outside availability, free, generic Reparto work (Presenze), or the patient +
  // therapy they're booked with. For the Reparto-relevant ones it also re-runs the
  // exact same per-therapist checks as ComputeRepartoCapacityAndDemand
  // (PianificazioneReparto.cs) and records whether/why each is counted.
  private class CapacityDebugTherapistDto {
    public int Id { get; set; }
    public string Name { get; set; } = "?";
    public string AreaLabel { get; set; } = "";
    public bool IsRelevant { get; set; }
    public bool IsRepartoCapable { get; set; }
    public bool Counted { get; set; }
    public string Status { get; set; } = "";
    public List<string> Activities { get; set; } = new();
    public string CapacityNote { get; set; } = "";
  }

  [HttpGet("/Giorno/SlotCapacityDebug")]
  public IActionResult GiornoSlotCapacityDebug(DateOnly date, int timeSlot) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var relevantTherapists = GetRepartoCapableAndCoveringTherapists();
    var relevantIds = relevantTherapists.Select(t => t.Id).ToHashSet();
    var (repartoRate, coveringRate) = GetRepartoRates();

    var allTherapists = _db.Therapists
      .Where(t => t.IsActive == 1 && (t.OperatingArea & TherapistOperatingArea.Accettazione) == 0)
      .OrderBy(t => t.Name)
      .ToList();

    var typeCache = GetTypeCache();
    var partCache = GetPartCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();

    var slotsOnDate = _db.TherapySlots
      .Where(s => s.Date == date && s.Status != TherapySlotStatus.Rescheduled)
      .ToList();

    var genericRepartoIds = _db.GenericRepartoWorks
      .Where(g => g.Date == date && g.TimeSlot == timeSlot)
      .Select(g => g.TherapistId)
      .ToHashSet();

    // Every session covering this slot, as "Patient - Therapy", keyed by therapist.
    var activitiesByTherapist = new Dictionary<int, List<string>>();
    var existingCount = 0;

    // Every Reparto session covering this slot (Donne and Uomini together, the same
    // set existingCount/demand are computed from) - debug listing only.
    var repartoSessions = new List<(int Sex, string PatientName, string TypeLabel, bool IsLight)>();

    foreach (var s in slotsOnDate) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      var span = GetSlotSpan(s, type);

      if (timeSlot < s.TimeSlot || timeSlot >= s.TimeSlot + span) {
        continue;
      }

      // existingCount matches the grid's own badge semantics (all Reparto-category
      // sessions, Light + Active) - not the same as `demand`, which is Light-only.
      if (type != null && type.Category == TherapyCategory.Reparto) {
        existingCount++;

        var repartoTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        var repartoPatient = repartoTherapy != null && patientCache.ContainsKey(repartoTherapy.PatientId) ? patientCache[repartoTherapy.PatientId] : null;
        repartoSessions.Add((
          repartoPatient?.Sex ?? -1,
          repartoPatient?.Name ?? "?",
          string.IsNullOrEmpty(type.Abbreviazione) ? type.Name : type.Abbreviazione,
          type.Type == TherapyExecutionType.Light));
      }

      if (!s.TherapistId.HasValue) {
        continue;
      }

      var therapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      var patientName = therapy != null && patientCache.ContainsKey(therapy.PatientId) ? patientCache[therapy.PatientId].Name : "?";
      var typeName = type != null ? type.Name : "?";
      var kind = type == null ? "" : (type.Type == TherapyExecutionType.Active ? " (attiva)" : " (leggera)");

      if (!activitiesByTherapist.TryGetValue(s.TherapistId.Value, out var list)) {
        list = new List<string>();
        activitiesByTherapist[s.TherapistId.Value] = list;
      }
      list.Add(patientName + " - " + typeName + kind);
    }

    var therapistBreakdown = allTherapists.Select(t => {
      var effectiveArea = GetEffectiveOperatingArea(t, date, timeSlot);
      var isRepartoCapable = (t.OperatingArea & TherapistOperatingArea.Reparto) != 0;
      var dto = new CapacityDebugTherapistDto {
        Id = t.Id,
        Name = t.Name,
        AreaLabel = TherapistOperatingArea.ToRoleLabel(effectiveArea) + (effectiveArea != t.OperatingArea ? " (override)" : ""),
        IsRelevant = relevantIds.Contains(t.Id),
        IsRepartoCapable = isRepartoCapable
      };

      var onVacation = IsBlockedByVacation(t.Id, date, timeSlot, 1);
      var available = IsWithinAvailability(t.Id, date, timeSlot, 1);
      var busyActive = IsTherapistBusyWithActiveSession(t.Id, date, timeSlot);

      dto.Status = onVacation ? "In ferie/chiusura" : (!available ? "Fuori disponibilità" : "Disponibile");

      if (activitiesByTherapist.TryGetValue(t.Id, out var acts)) {
        dto.Activities.AddRange(acts);
      }
      if (genericRepartoIds.Contains(t.Id)) {
        dto.Activities.Add("Reparto (generico, da Presenze)");
      }

      if (!dto.IsRelevant) {
        dto.CapacityNote = "Non conteggiato (non Reparto/aiuto)";
      } else if (onVacation) {
        dto.CapacityNote = "Escluso: in ferie/chiusura";
      } else if (!available) {
        dto.CapacityNote = "Escluso: fuori disponibilità";
      } else if (busyActive) {
        dto.CapacityNote = "Escluso: in seduta attiva";
      } else {
        var rate = isRepartoCapable ? repartoRate : coveringRate;
        dto.Counted = true;
        dto.CapacityNote = "+" + (15.0 / rate).ToString("0.##");
      }

      return dto;
    }).ToList();

    var (capacity, demand) = ComputeRepartoCapacityAndDemand(date, timeSlot, repartoRate, coveringRate, relevantTherapists);

    return Ok(new {
      date,
      timeSlot,
      capacity,
      demand,
      existingCount,
      repartoRate,
      coveringRate,
      therapists = therapistBreakdown,
      repartoSessions = repartoSessions
        .OrderBy(r => r.Sex)
        .ThenBy(r => r.PatientName)
        .Select(r => new {
          patientName = r.PatientName,
          typeLabel = r.TypeLabel,
          reparto = r.Sex == PatientSex.Female ? "Donne" : r.Sex == PatientSex.Male ? "Uomini" : "?",
          isLight = r.IsLight
        })
        .ToList()
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

    var moved = GetMovedAbsentSlotsInDateRange(dates);
    var movedTo = GetMovedToDates(moved);

    var days = !hasAvailability ? new List<object>() : dates.Select((d, i) => (object)new {
      date = d,
      availability = perDayAvailability[i],
      capacityGrid = BuildCapacityGridInMemory(ctx, d, gridStart, gridEnd, includeExistingCount: true),
      slots = BuildTherapistSlotsFromBatch(slotsInRange, therapistId, d)
        .Concat(BuildTherapistSlotsFromBatch(moved, therapistId, d).Select(x => MarkMovedAbsent(x, movedTo)))
        .ToList()
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

    var moved = GetMovedAbsentSlotsInDateRange(dates);
    var movedTo = GetMovedToDates(moved);

    // No existingCount here either, same reasoning as GiornoRepartoData above.
    var days = dates.Select(d => new {
      date = d,
      capacityGrid = BuildCapacityGridInMemory(ctx, d, clinicStart, clinicEnd, includeExistingCount: false),
      slots = BuildRepartoSlotsFromBatch(slotsInRange, sex, d)
        .Concat(BuildRepartoSlotsFromBatch(moved, sex, d).Select(x => MarkMovedAbsent(x, movedTo)))
        .ToList()
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

  // ---- Settimana "Gestisci paziente" (Accettazione only) ------------------------
  //
  // CPU: one patient's whole week - every session of theirs, whoever the therapist
  // (highlighted, with the Presenze status marks), on top of the selected
  // therapist's availability and other bookings, so a session can be moved or
  // re-planned without landing on something already there. Moves go through the
  // normal Giorno/Slot/{id}/Move, which checks the session's own therapist.

  // Patients whose name contains the text - each with where its current therapy
  // starts, so the view can jump to that week.
  [HttpGet("/Settimana/PatientLookup")]
  public IActionResult SettimanaPatientLookup(string? name, int? patientId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    List<Patient> patients;

    if (patientId.HasValue) {
      patients = _db.Patients.Where(p => p.Id == patientId.Value).ToList();
    } else {
      var text = (name ?? string.Empty).Trim();
      if (text.Length == 0) {
        return BadRequest(new { message = "Scrivi almeno una parte del nome." });
      }
      patients = _db.Patients.Where(p => p.Name.Contains(text)).OrderBy(p => p.Name).Take(20).ToList();
    }

    var results = patients.Select(p => {
      var therapy = GetCurrentTherapy(p.Id);
      DateOnly? firstDate = null;

      if (therapy != null) {
        var partIds = _db.TherapyParts.Where(tp => tp.TherapyId == therapy.Id).Select(tp => tp.Id).ToList();
        var first = _db.TherapySlots
          .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
          .OrderBy(s => s.Date)
          .Select(s => (DateOnly?)s.Date)
          .FirstOrDefault();
        firstDate = first;
      }

      return new {
        id = p.Id,
        name = p.Name,
        firstSessionDate = firstDate?.ToString("yyyy-MM-dd")
      };
    }).ToList();

    return Ok(results);
  }

  [HttpGet("/Settimana/PatientData")]
  public IActionResult SettimanaPatientData(int patientId, DateOnly weekStart, int? therapistId) {
    if (!IsCurrentUserAccettazione()) {
      return Forbid();
    }

    var patient = _db.Patients.Find(patientId);

    if (patient == null) {
      return NotFound();
    }

    var dates = GetWeekdayDates(weekStart);
    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();

    var therapist = therapistId.HasValue ? _db.Therapists.Find(therapistId.Value) : null;
    var vacations = therapist != null ? GetTherapistVacations(therapist.Id) : GetGlobalVacations();

    var slotsInRange = GetSlotsInDateRange(dates);
    var ctx = BuildCapacityComputationContext(slotsInRange);
    var capacityWarningThreshold = GetRepartoCapacityWarningThreshold();

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    DaySlotDto ToDto(TherapySlot s) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
      var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      var slotPatient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : null;

      return new DaySlotDto {
        Id = s.Id,
        TimeSlot = s.TimeSlot,
        DurationSlots = GetSlotSpan(s, type),
        TherapyTypeId = part?.TherapyTypeId,
        TherapyTypeName = type?.Name ?? "?",
        TherapyTypeCategory = type?.Category,
        TherapyTypeExecutionType = type?.Type,
        TherapyTypeColor = type?.Color,
        TherapistId = s.TherapistId,
        TherapistName = s.TherapistId.HasValue && therapistNameCache.ContainsKey(s.TherapistId.Value)
          ? therapistNameCache[s.TherapistId.Value]
          : null,
        PatientName = slotPatient?.Name ?? "?",
        PatientId = slotPatient?.Id,
        HasDocument = PatientHasDocument(slotPatient),
        Status = s.Status
      };
    }

    bool IsPatients(TherapySlot s) {
      var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
      var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
      return slotTherapy != null && slotTherapy.PatientId == patientId;
    }

    var moved = GetMovedAbsentSlotsInDateRange(dates);
    var movedTo = GetMovedToDates(moved);

    var days = dates.Select(d => new {
      date = d,
      // The selected therapist's windows (to see where a session can go); without
      // a therapist the whole clinic day is open.
      availability = therapist != null ? GetTherapistDayAvailability(therapist.Id, d) : null,
      capacityGrid = BuildCapacityGridInMemory(ctx, d, clinicStart, clinicEnd, includeExistingCount: false),
      slots = slotsInRange
        .Where(s => s.Date == d && (IsPatients(s) || (therapist != null && s.TherapistId == therapist.Id)))
        .Select(ToDto)
        .Concat(moved
          .Where(s => s.Date == d && IsPatients(s))
          .Select(s => MarkMovedAbsent(ToDto(s), movedTo)))
        .ToList()
    }).ToList();

    return Ok(new {
      requestWeekStart = weekStart.ToString("yyyy-MM-dd"),
      requestPatientId = patientId,
      name = patient.Name,
      patientId = patient.Id,
      therapistName = therapist?.Name,
      gridStart = clinicStart,
      gridEnd = clinicEnd,
      vacations,
      capacityWarningThreshold,
      days
    });
  }
}
