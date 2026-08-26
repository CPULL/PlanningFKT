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

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<AlertDismissal>().HasKey(d => d.Key);
    modelBuilder.Entity<AlertMarkedImportant>().HasKey(m => m.Key);

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

    // TherapyType catalog seed data (17 entries, final CPU-curated list)
    // Category: 0 = Reparto, 1 = Palestra
    // Type: 0 = Light, 1 = Active
    // Color: packed 24-bit RGB int (0xRRGGBB)
    // ModUser = 1 assumes Admin is the first Therapist row ever created (Id = 1) on a fresh DB
    var seedDate = new DateTime(2026, 1, 1);
    const int seedUser = 1;

    const int reparto = 0;
    const int palestra = 1;
    const int light = 0;
    const int active = 1;

    modelBuilder.Entity<TherapyType>().HasData(
      new TherapyType { Id = 1, Name = "Elettroterapia", Abbreviazione = "Elettr", Duration = 15, Category = reparto, Type = light, Color = 9489145, MaxParallelFemale = 4, MaxParallelMale = 3, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 2, Name = "Ionoforesi", Abbreviazione = "Ionof", Duration = 20, Category = reparto, Type = light, Color = 16764032, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 3, Name = "Infrarossi", Abbreviazione = "Infrar", Duration = 15, Category = reparto, Type = light, Color = 14842205, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 4, Name = "Massoterapia 15m", Abbreviazione = "Masso15", Duration = 15, Category = reparto, Type = active, Color = 13538264, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 5, Name = "Massoterapia 30m", Abbreviazione = "Masso30", Duration = 30, Category = reparto, Type = active, Color = 13538264, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 6, Name = "Mezieres", Abbreviazione = "Mez", Duration = 60, Category = palestra, Type = active, Color = 16635957, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 1, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 7, Name = "Isocinetica", Abbreviazione = "Isocin", Duration = 30, Category = palestra, Type = active, Color = 13214247, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 1, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 8, Name = "Rieducazione mot 15m", Abbreviazione = "Ried15", Duration = 15, Category = palestra, Type = active, Color = 2541274, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 9, Name = "Rieducazione mot 30m", Abbreviazione = "Ried30", Duration = 30, Category = palestra, Type = active, Color = 2541274, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 10, Name = "Rieducazione mot 45m", Abbreviazione = "Ried45", Duration = 45, Category = palestra, Type = active, Color = 2541274, MaxParallelFemale = null, MaxParallelMale = null, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 11, Name = "Laser", Abbreviazione = "Laser", Duration = 15, Category = reparto, Type = light, Color = 8421376, MaxParallelFemale = 2, MaxParallelMale = 2, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 12, Name = "Laser YAG", Abbreviazione = "YAG", Duration = 10, Category = reparto, Type = active, Color = 16485376, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 13, Name = "Magneto", Abbreviazione = "Magneto", Duration = 30, Category = reparto, Type = light, Color = 7901340, MaxParallelFemale = 2, MaxParallelMale = 2, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 14, Name = "Ultrasuoni", Abbreviazione = "UltraS", Duration = 15, Category = reparto, Type = light, Color = 12433259, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 15, Name = "Tecar", Abbreviazione = "Tecar", Duration = 30, Category = reparto, Type = active, Color = 9415055, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 16, Name = "Onde d'urto", Abbreviazione = "OndeU", Duration = 15, Category = palestra, Type = active, Color = 15094016, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 2, IsActive = 1, ModDate = seedDate, ModUser = seedUser },
      new TherapyType { Id = 17, Name = "Shock termico", Abbreviazione = "ShockT", Duration = 15, Category = reparto, Type = active, Color = 13990251, MaxParallelFemale = 1, MaxParallelMale = 1, TherapyWeeklyFrequency = 7, IsActive = 1, ModDate = seedDate, ModUser = seedUser }
    );
  }
}
