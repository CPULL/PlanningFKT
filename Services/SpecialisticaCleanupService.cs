using Microsoft.Extensions.DependencyInjection;
using minerva.planningfkt.models;

namespace minerva.planningfkt.services;

// Deletes specialistica PDFs that are no longer needed (CPU's rule: gone once
// they're more than 1 year old). Runs a first time shortly after startup, then
// once a day for as long as the app stays up. Under IIS the process can be
// stopped/recycled, which just means the next run happens at the next startup -
// fine for a once-a-day job.
//
// For each patient with a document:
// - a therapy still open (Da pianificare / Pianificata) -> never touched;
// - otherwise the reference date is the latest of: the patient's last session
//   (any therapy, rescheduled ones excluded) and the document's upload date - so
//   a fresh specialistica uploaded for a therapy not created yet is kept;
// - reference date older than 1 year -> the file is deleted and the patient's
//   DocumentFileName/DocumentAttachedDate are cleared. A document whose file is
//   already missing on disk is just cleared the same way.
public class SpecialisticaCleanupService : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IConfiguration _config;
  private readonly ILogger<SpecialisticaCleanupService> _logger;

  public SpecialisticaCleanupService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<SpecialisticaCleanupService> logger) {
    _scopeFactory = scopeFactory;
    _config = config;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      // Let startup (migrations, seeding) finish first.
      await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

      while (!stoppingToken.IsCancellationRequested) {
        try {
          var removed = RunOnce();
          if (removed > 0) {
            _logger.LogInformation("Specialistiche cleanup: {Count} document(s) removed.", removed);
          }
        } catch (Exception ex) {
          _logger.LogError(ex, "Specialistiche cleanup failed.");
        }

        await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
      }
    } catch (OperationCanceledException) {
      // App shutting down.
    }
  }

  public int RunOnce() {
    var folder = _config["DocumentStoragePath"];

    if (string.IsNullOrEmpty(folder)) {
      return 0;
    }

    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var cutoff = DateOnly.FromDateTime(DateTime.Today.AddYears(-1));

    var patients = db.Patients.Where(p => p.DocumentFileName != null && p.DocumentFileName != "").ToList();

    if (patients.Count == 0) {
      return 0;
    }

    var patientIds = patients.Select(p => p.Id).ToList();
    var therapies = db.Therapies.Where(t => patientIds.Contains(t.PatientId)).ToList();
    var therapyIds = therapies.Select(t => t.Id).ToList();
    var parts = db.TherapyParts.Where(p => therapyIds.Contains(p.TherapyId)).ToList();
    var partIds = parts.Select(p => p.Id).ToList();

    var lastSlotDateByPart = db.TherapySlots
      .Where(s => partIds.Contains(s.TherapyPartId) && s.Status != TherapySlotStatus.Rescheduled)
      .GroupBy(s => s.TherapyPartId)
      .Select(g => new { PartId = g.Key, LastDate = g.Max(s => s.Date) })
      .ToList()
      .ToDictionary(x => x.PartId, x => x.LastDate);

    var therapyById = therapies.ToDictionary(t => t.Id);
    var removed = 0;

    foreach (var patient in patients) {
      var patientTherapies = therapies.Where(t => t.PatientId == patient.Id).ToList();

      if (patientTherapies.Any(t => t.Status == TherapyStatus.ToBeScheduled || t.Status == TherapyStatus.Scheduled)) {
        continue;
      }

      DateOnly? referenceDate = patient.DocumentAttachedDate.HasValue
        ? DateOnly.FromDateTime(patient.DocumentAttachedDate.Value)
        : null;

      foreach (var part in parts.Where(p => therapyById.ContainsKey(p.TherapyId) && therapyById[p.TherapyId].PatientId == patient.Id)) {
        if (lastSlotDateByPart.TryGetValue(part.Id, out var lastDate) && (referenceDate == null || lastDate > referenceDate.Value)) {
          referenceDate = lastDate;
        }
      }

      // No upload date and no session at all: nothing to measure the year from.
      if (referenceDate == null || referenceDate.Value >= cutoff) {
        continue;
      }

      var path = Path.Combine(folder, patient.DocumentFileName!);

      try {
        if (File.Exists(path)) {
          File.Delete(path);
        }
      } catch (Exception ex) {
        _logger.LogWarning(ex, "Specialistiche cleanup: could not delete {Path}.", path);
        continue; // keep the reference so the next run retries
      }

      patient.DocumentFileName = null;
      patient.DocumentAttachedDate = null;
      removed++;
    }

    if (removed > 0) {
      db.SaveChanges();
    }

    return removed;
  }
}
