using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  // "Presenze" - a quick daily attendance page for therapists: mark who showed up
  // and who didn't. Palestra-only therapists see just their own assigned slots that
  // day; anyone with a Reparto side (pure Reparto, or "aiuto") also sees every
  // Reparto-category slot that day, since Reparto is a shared pool. There's also a
  // Reparto Uomini/Donne mode (no specific therapist) showing every Reparto slot
  // for that day split by patient sex, same idea as Giorno's own Reparto view.
  // Deliberately NOT Accettazione-gated - every therapist needs this for their own day.

  private class PresenzeSlotDto {
    public int Id { get; set; }
    public int TimeSlot { get; set; }
    public int DurationSlots { get; set; }
    public int? TherapyTypeId { get; set; }
    public string TherapyTypeLabel { get; set; } = "?"; // Abbreviazione, falls back to Name
    public int? TherapyTypeColor { get; set; }
    public string PatientName { get; set; } = "?";
    public int Status { get; set; }
    public string? ModUserName { get; set; }
    public DateTime? ModDate { get; set; }
  }

  private PresenzeSlotDto BuildPresenzeSlotDto(TherapySlot s, Dictionary<int, TherapyPart> partCache,
      Dictionary<int, TherapyType> typeCache, Dictionary<int, Therapy> therapyCache,
      Dictionary<int, Patient> patientCache, Dictionary<int, string> therapistNameCache) {
    var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
    var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
    var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
    var patientName = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId)
      ? patientCache[slotTherapy.PatientId].Name
      : "?";

    return new PresenzeSlotDto {
      Id = s.Id,
      TimeSlot = s.TimeSlot,
      DurationSlots = type != null ? GetSpanSlots(type) : 1,
      TherapyTypeId = part?.TherapyTypeId,
      TherapyTypeLabel = type != null ? (string.IsNullOrEmpty(type.Abbreviazione) ? type.Name : type.Abbreviazione) : "?",
      TherapyTypeColor = type?.Color,
      PatientName = patientName,
      Status = s.Status,
      ModUserName = s.ModUser.HasValue && therapistNameCache.ContainsKey(s.ModUser.Value) ? therapistNameCache[s.ModUser.Value] : null,
      ModDate = s.ModDate
    };
  }

  [HttpGet("/Presenze/TherapistData")]
  public IActionResult PresenzeTherapistData(int therapistId, DateOnly date) {
    var therapist = _db.Therapists.Find(therapistId);

    if (therapist == null || therapist.IsActive != 1 || (therapist.OperatingArea & TherapistOperatingArea.Accettazione) != 0) {
      return NotFound();
    }

    var availability = GetTherapistDayAvailability(therapistId, date);
    var hasAvailability = availability.Count > 0;
    var gridStart = hasAvailability ? availability.Min(a => a.StartTime) : 0;
    var gridEnd = hasAvailability ? availability.Max(a => a.EndTime) : 0;

    var isPurePalestra = therapist.OperatingArea == 0; // no Reparto bit, no HelpsOtherArea bit

    // Reparto slots (own or the shared pool) aren't bounded by this specific
    // therapist's own availability window - widen the grid to the clinic's full
    // hours whenever Reparto is in play, or a Reparto/other-therapist slot outside
    // this person's own hours would fall off the grid entirely and never render.
    if (!isPurePalestra) {
      var clinicStart = GetClinicHoursStart();
      var clinicEnd = GetClinicHoursEnd();
      gridStart = hasAvailability ? Math.Min(gridStart, clinicStart) : clinicStart;
      gridEnd = hasAvailability ? Math.Max(gridEnd, clinicEnd) : clinicEnd;
      hasAvailability = true;
    }

    var vacations = GetTherapistVacations(therapistId);

    var dates = new List<DateOnly> { date };
    var slotsInRange = GetSlotsInDateRange(dates).Where(s => s.Date == date).ToList();

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    // "Reparto e aiuto a palestra" (OperatingArea == 6) helps across both pools, so
    // they see all Palestra activity too, same reasoning as everyone non-Palestra-only
    // already seeing the full Reparto pool.
    var isRepartoHelpingPalestra = therapist.OperatingArea == (TherapistOperatingArea.Reparto | TherapistOperatingArea.HelpsOtherArea);

    var slots = slotsInRange
      .Where(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;

        var isOwn = s.TherapistId == therapistId;
        var isReparto = type != null && type.Category == TherapyCategory.Reparto;
        var isPalestra = type != null && type.Category == TherapyCategory.Palestra;

        return isOwn
          || (!isPurePalestra && isReparto)
          || (isRepartoHelpingPalestra && isPalestra);
      })
      .Select(s => BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache))
      .ToList();

    return Ok(new {
      requestDate = date.ToString("yyyy-MM-dd"),
      requestTherapistId = therapistId,
      name = therapist.Name,
      hasAvailability,
      gridStart,
      gridEnd,
      availability,
      vacations,
      slots
    });
  }

  [HttpGet("/Presenze/RepartoData")]
  public IActionResult PresenzeRepartoData(int sex, DateOnly date) {
    var clinicStart = GetClinicHoursStart();
    var clinicEnd = GetClinicHoursEnd();
    var vacations = GetGlobalVacations();

    var dates = new List<DateOnly> { date };
    var slotsInRange = GetSlotsInDateRange(dates).Where(s => s.Date == date).ToList();

    var partCache = GetPartCache();
    var typeCache = GetTypeCache();
    var therapyCache = GetTherapyCache();
    var patientCache = GetPatientEntityCache();
    var therapistNameCache = GetTherapistNameCache();

    var slots = slotsInRange
      .Where(s => {
        var part = partCache.ContainsKey(s.TherapyPartId) ? partCache[s.TherapyPartId] : null;
        var type = part != null && typeCache.ContainsKey(part.TherapyTypeId) ? typeCache[part.TherapyTypeId] : null;
        var slotTherapy = part != null && therapyCache.ContainsKey(part.TherapyId) ? therapyCache[part.TherapyId] : null;
        var patient = slotTherapy != null && patientCache.ContainsKey(slotTherapy.PatientId) ? patientCache[slotTherapy.PatientId] : null;

        return type != null && type.Category == TherapyCategory.Reparto && patient != null && patient.Sex == sex;
      })
      .Select(s => BuildPresenzeSlotDto(s, partCache, typeCache, therapyCache, patientCache, therapistNameCache))
      .ToList();

    return Ok(new {
      requestDate = date.ToString("yyyy-MM-dd"),
      requestSex = sex,
      name = sex == PatientSex.Female ? "Reparto Donne" : "Reparto Uomini",
      hasAvailability = true,
      gridStart = clinicStart,
      gridEnd = clinicEnd,
      availability = new List<object>(),
      vacations,
      slots
    });
  }

  [HttpPost("/Presenze/Slot/{id}/CycleStatus")]
  public IActionResult PresenzeCycleStatus(int id) {
    var slot = _db.TherapySlots.Find(id);

    if (slot == null) {
      return NotFound();
    }

    if (slot.Status == TherapySlotStatus.ToBeDone) {
      slot.Status = TherapySlotStatus.Done;
    } else if (slot.Status == TherapySlotStatus.Done) {
      slot.Status = TherapySlotStatus.PatientAbsent;
    } else {
      slot.Status = TherapySlotStatus.ToBeDone;
    }

    slot.ModDate = DateTime.Now;
    slot.ModUser = GetCurrentTherapistId();

    _db.SaveChanges();

    var therapistNameCache = GetTherapistNameCache();

    return Ok(new {
      status = slot.Status,
      modUserName = slot.ModUser.HasValue && therapistNameCache.ContainsKey(slot.ModUser.Value) ? therapistNameCache[slot.ModUser.Value] : null,
      modDate = slot.ModDate
    });
  }
}
