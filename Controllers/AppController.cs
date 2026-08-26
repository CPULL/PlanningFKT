using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;
using minerva.planningfkt.services;

namespace minerva.planningfkt.controllers;

[ApiController]
[Route("[controller]")]
[Authorize]
public partial class AppController : ControllerBase {
  private readonly AppDbContext _db;
  private readonly IConfiguration _config;
  private readonly SettingsCache _settingsCache;

  public AppController(AppDbContext db, IConfiguration config, SettingsCache settingsCache) {
    _db = db;
    _config = config;
    _settingsCache = settingsCache;
  }

  // --- Per-request memoization for the hot Reparto capacity helpers -----------
  //
  // ASP.NET Core creates a brand-new AppController instance for every HTTP request
  // (default, non-shared controller lifetime), so these are plain instance fields,
  // never static - nothing here ever survives past the response that populated it,
  // and no two requests (or users) ever share a cache. This exists purely to stop
  // IsWithinAvailability/IsBlockedByVacation/IsTherapistBusyWithActiveSession/
  // CountLightRepartoDemand from re-querying identical data on every one of the
  // dozens of 15-minute slots in a capacity grid - see AppController.Pianificazione.cs
  // and AppController.PianificazioneReparto.cs for where these are populated/used.
  private Dictionary<int, List<Vacation>>? _vacationsByTherapistCache;
  private Dictionary<(int TherapistId, int DayOfWeek), List<TherapistAvailability>>? _availabilityByTherapistDayCache;
  private Dictionary<int, bool>? _overtimeAllowedCache;
  private Dictionary<(int TherapistId, DateOnly Date), List<TherapySlot>>? _therapistSlotsByDateCache;
  private Dictionary<DateOnly, List<TherapySlot>>? _allSlotsByDateCache;
  private Dictionary<int, TherapyPart>? _partCache;
  private Dictionary<int, TherapyType>? _typeCache;
  private Dictionary<int, Therapy>? _therapyCache;
  private Dictionary<int, Patient>? _patientEntityCache;
  private Dictionary<int, string>? _therapistNameCache;

  // AsNoTracking: these are pure read-only lookup dictionaries, never mutated -
  // skipping EF's change tracking cuts real overhead now that these tables hold
  // years of real data, without touching any of the many call sites that share
  // these caches (CPU: Giorno/Settimana/Alerts/Presenze/Mese all felt slow).
  private Dictionary<int, TherapyPart> GetPartCache() {
    return _partCache ??= _db.TherapyParts.AsNoTracking().ToDictionary(p => p.Id, p => p);
  }

  private Dictionary<int, TherapyType> GetTypeCache() {
    return _typeCache ??= _db.TherapyTypes.AsNoTracking().ToDictionary(t => t.Id, t => t);
  }

  private Dictionary<int, Therapy> GetTherapyCache() {
    return _therapyCache ??= _db.Therapies.AsNoTracking().ToDictionary(t => t.Id, t => t);
  }

  private Dictionary<int, Patient> GetPatientEntityCache() {
    return _patientEntityCache ??= _db.Patients.AsNoTracking().ToDictionary(p => p.Id, p => p);
  }

  private Dictionary<int, string> GetTherapistNameCache() {
    return _therapistNameCache ??= _db.Therapists.ToDictionary(t => t.Id, t => t.Name);
  }

  [HttpGet("ping")]
  [AllowAnonymous]
  public IActionResult Ping() {
    return Ok(new { status = "ok" });
  }

  private int? GetCurrentTherapistId() {
    var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (idClaim == null || !int.TryParse(idClaim, out var id)) {
      return null;
    }

    return id;
  }

  private bool IsCurrentUserAccettazione() {
    var id = GetCurrentTherapistId();

    if (id == null) {
      return false;
    }

    var therapist = _db.Therapists.Find(id.Value);

    if (therapist == null) {
      return false;
    }

    return (therapist.OperatingArea & TherapistOperatingArea.Accettazione) != 0;
  }
}
