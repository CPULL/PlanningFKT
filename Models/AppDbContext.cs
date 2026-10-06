using Microsoft.EntityFrameworkCore;

namespace minerva.planningfkt.models;

public class AppDbContext : DbContext {
  public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) {
  }

  public DbSet<Patient> Patients => Set<Patient>();
  public DbSet<Therapist> Therapists => Set<Therapist>();
  public DbSet<TherapyType> TherapyTypes => Set<TherapyType>();
  public DbSet<Therapy> Therapies => Set<Therapy>();
  public DbSet<TherapyPart> TherapyParts => Set<TherapyPart>();
  public DbSet<TherapySlot> TherapySlots => Set<TherapySlot>();
  public DbSet<TherapistAvailability> TherapistAvailabilities => Set<TherapistAvailability>();
  public DbSet<Vacation> Vacations => Set<Vacation>();
  public DbSet<Setting> Settings => Set<Setting>();
  public DbSet<TherapyPacket> TherapyPackets => Set<TherapyPacket>();
  public DbSet<TherapyPacketItem> TherapyPacketItems => Set<TherapyPacketItem>();
  public DbSet<TherapyPacketConfig> TherapyPacketConfigs => Set<TherapyPacketConfig>();
  public DbSet<AlertDismissal> AlertDismissals => Set<AlertDismissal>();
  public DbSet<AlertMarkedImportant> AlertMarkedImportants => Set<AlertMarkedImportant>();
  public DbSet<TherapistAbsenceCall> TherapistAbsenceCalls => Set<TherapistAbsenceCall>();
  public DbSet<FoglioFirmaChecklistItem> FoglioFirmaChecklistItems => Set<FoglioFirmaChecklistItem>();
  public DbSet<GenericRepartoWork> GenericRepartoWorks => Set<GenericRepartoWork>();
  public DbSet<PrCode> PrCodes => Set<PrCode>();
  public DbSet<TherapyPrCode> TherapyPrCodes => Set<TherapyPrCode>();

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<AlertDismissal>().HasKey(d => d.Key);
    modelBuilder.Entity<AlertMarkedImportant>().HasKey(m => m.Key);
    modelBuilder.Entity<FoglioFirmaChecklistItem>().HasKey(c => c.Key);
    modelBuilder.Entity<GenericRepartoWork>().HasIndex(g => new { g.TherapistId, g.Date, g.TimeSlot }).IsUnique();

    // Patient -> Therapy (cascade: deleting a patient deletes all their therapies)
    modelBuilder.Entity<Therapy>()
      .HasOne<Patient>()
      .WithMany()
      .HasForeignKey(t => t.PatientId)
      .OnDelete(DeleteBehavior.Cascade);

    // Therapy -> TherapyPart (cascade: part of the same patient-deletion chain)
    modelBuilder.Entity<TherapyPart>()
      .HasOne<Therapy>()
      .WithMany()
      .HasForeignKey(p => p.TherapyId)
      .OnDelete(DeleteBehavior.Cascade);

    // TherapyPart -> TherapySlot (cascade: same chain)
    modelBuilder.Entity<TherapySlot>()
      .HasOne<TherapyPart>()
      .WithMany()
      .HasForeignKey(s => s.TherapyPartId)
      .OnDelete(DeleteBehavior.Cascade);

    // Every scheduling view (Giorno/Settimana/Presenze/Alerts/Mese) filters
    // TherapySlots by Date on every single request via GetSlotsInDateRange -
    // without an index this is a full table scan, which is fine on test data but
    // becomes the dominant cost once years of real slots accumulate (CPU: Giorno/
    // Settimana taking ~5s). The composite covers the common "date range for one
    // specific therapist" queries too.
    modelBuilder.Entity<TherapySlot>()
      .HasIndex(s => s.Date);
    modelBuilder.Entity<TherapySlot>()
      .HasIndex(s => new { s.Date, s.TherapistId });

    // TherapyType is never deleted (only marked inactive) -> restrict, don't allow accidental cascade
    modelBuilder.Entity<TherapyPart>()
      .HasOne<TherapyType>()
      .WithMany()
      .HasForeignKey(p => p.TherapyTypeId)
      .OnDelete(DeleteBehavior.Restrict);

    modelBuilder.Entity<TherapyPacketItem>()
      .HasOne<TherapyType>()
      .WithMany()
      .HasForeignKey(i => i.TherapyTypeId)
      .OnDelete(DeleteBehavior.Restrict);

    // TherapyType is never deleted (only marked inactive) -> restrict here too
    modelBuilder.Entity<TherapyPacketConfig>()
      .HasOne<TherapyType>()
      .WithMany()
      .HasForeignKey(c => c.TherapyTypeId)
      .OnDelete(DeleteBehavior.Restrict);

    // One rate-card row per TherapyType
    modelBuilder.Entity<TherapyPacketConfig>()
      .HasIndex(c => c.TherapyTypeId)
      .IsUnique();

    // Therapist is never deleted (only marked inactive) -> restrict everywhere it's referenced
    modelBuilder.Entity<TherapySlot>()
      .HasOne<Therapist>()
      .WithMany()
      .HasForeignKey(s => s.TherapistId)
      .OnDelete(DeleteBehavior.Restrict);

    modelBuilder.Entity<TherapistAvailability>()
      .HasOne<Therapist>()
      .WithMany()
      .HasForeignKey(a => a.TherapistId)
      .OnDelete(DeleteBehavior.Restrict);

    modelBuilder.Entity<Vacation>()
      .HasOne<Therapist>()
      .WithMany()
      .HasForeignKey(v => v.TherapistId)
      .OnDelete(DeleteBehavior.Restrict);

    // TherapySlot self-reference (Rescheduled -> new slot) -> restrict, avoids cascade-path issues
    modelBuilder.Entity<TherapySlot>()
      .HasOne<TherapySlot>()
      .WithMany()
      .HasForeignKey(s => s.RescheduledToId)
      .OnDelete(DeleteBehavior.Restrict);

    // Patient -> TherapyPacket (cascade only for patient-linked packets; global ones have PatientId = null)
    modelBuilder.Entity<TherapyPacket>()
      .HasOne<Patient>()
      .WithMany()
      .HasForeignKey(p => p.PatientId)
      .OnDelete(DeleteBehavior.Cascade);

    // TherapyPacket -> TherapyPacketItem (cascade: item goes with its packet)
    modelBuilder.Entity<TherapyPacketItem>()
      .HasOne<TherapyPacket>()
      .WithMany()
      .HasForeignKey(i => i.TherapyPacketId)
      .OnDelete(DeleteBehavior.Cascade);

    // Login name must be unique (Therapist.Name doubles as login credential)
    modelBuilder.Entity<Therapist>()
      .HasIndex(t => t.Name)
      .IsUnique();

    // Settings are a Key/Value store, Key must be unique
    modelBuilder.Entity<Setting>()
      .HasIndex(s => s.Key)
      .IsUnique();

    // Money fields precision
    modelBuilder.Entity<TherapyPacket>()
      .Property(p => p.TotalPrice)
      .HasPrecision(18, 2);

    modelBuilder.Entity<TherapyPacketItem>()
      .Property(i => i.UnitPrice)
      .HasPrecision(18, 2);

    modelBuilder.Entity<TherapyPacketItem>()
      .Property(i => i.DiscountPercent)
      .HasPrecision(18, 2);

    modelBuilder.Entity<TherapyPacketConfig>()
      .Property(c => c.UnitPrice)
      .HasPrecision(18, 2);

    modelBuilder.Entity<TherapyPacketConfig>()
      .Property(c => c.DefaultDiscountPercent)
      .HasPrecision(18, 2);

    modelBuilder.Entity<PrCode>()
      .Property(c => c.Cost)
      .HasPrecision(18, 2);

    modelBuilder.Entity<PrCode>()
      .HasIndex(c => c.Code)
      .IsUnique();

    // Therapy -> its PR codes (cascade: they go with the therapy); a PR code in
    // use can't be deleted.
    modelBuilder.Entity<TherapyPrCode>()
      .HasOne<Therapy>()
      .WithMany()
      .HasForeignKey(c => c.TherapyId)
      .OnDelete(DeleteBehavior.Cascade);

    modelBuilder.Entity<TherapyPrCode>()
      .HasOne<PrCode>()
      .WithMany()
      .HasForeignKey(c => c.PrCodeId)
      .OnDelete(DeleteBehavior.Restrict);

    // Therapy -> Pacchetto used for its price; deleting the pacchetto just
    // clears the link.
    modelBuilder.Entity<Therapy>()
      .HasOne<TherapyPacket>()
      .WithMany()
      .HasForeignKey(t => t.TherapyPacketId)
      .OnDelete(DeleteBehavior.SetNull);

    // Seed data (TherapyType catalog, PR codes) - see DbSeeding.
    DbSeeding.ApplyModelSeeds(modelBuilder);
  }
}
