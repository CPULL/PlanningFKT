using Microsoft.EntityFrameworkCore;

namespace minerva.planningfkt.models;

// Every seed of the app in one place (CPU's call), except the Admin user which
// stays in Program.cs.
// - ApplyModelSeeds: catalog data written by the migrations (EF HasData) -
//   TherapyType and PrCode. Called from AppDbContext.OnModelCreating.
// - SeedRuntime: rows checked and inserted at every startup, only when missing
//   (existing rows are never touched) - Settings and fixed national holidays.
//   Called from Program.cs.
public static class DbSeeding {
  public static void ApplyModelSeeds(ModelBuilder modelBuilder) {
    SeedTherapyTypes(modelBuilder);
    SeedPrCodes(modelBuilder);
  }

  public static void SeedRuntime(AppDbContext db) {
    SeedSettings(db);
    SeedFixedHolidays(db);
  }

  private static void SeedTherapyTypes(ModelBuilder modelBuilder) {
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

  // AIOP Campania "Allegato 3 - Pacchetti riabilitativi" (pr-codes.json), with
  // typos fixed and standard capitalization.
  private static void SeedPrCodes(ModelBuilder modelBuilder) {
    var seedDate = new DateTime(2026, 10, 3);
    const int seedUser = 1;

    modelBuilder.Entity<PrCode>().HasData(
      new PrCode { Id = 1, Code = "PR1", Description = "Paresi del nervo facciale", Cost = 202.30m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 2, Code = "PR2", Description = "Mononeuriti dell'arto superiore e inferiore di origine meccanica e non meccanica e neuriti multiple", Cost = 202.30m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 3, Code = "PR3", Description = "Monoartriti solo in caso di riacutizzazioni, comprese le riacutizzazioni localizzate in corso di artrite reumatoide", Cost = 195.10m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 4, Code = "PR4A", Description = "Frattura della clavicola (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 5, Code = "PR4B", Description = "Frattura della scapola (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 6, Code = "PR4C", Description = "Frattura omero compreso paletta omerale (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 7, Code = "PR4D", Description = "Frattura radio, ulna (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 8, Code = "PR4E", Description = "Frattura ossa carpo e metacarpo (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 9, Code = "PR4F", Description = "Frattura di una o più falangi della mano (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 10, Code = "PR5A", Description = "Lussazione della spalla (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 11, Code = "PR5B", Description = "Lussazione del gomito (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 12, Code = "PR5C", Description = "Lussazione del polso (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 13, Code = "PR5D", Description = "Lussazione delle dita della mano (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 14, Code = "PR6A", Description = "Distorsione della spalla e del braccio (compreso instabilità gleno omerale) (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 15, Code = "PR6B", Description = "Distorsione del gomito e dell'avambraccio (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 16, Code = "PR6C", Description = "Distorsione del polso e della mano (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 17, Code = "PR7", Description = "Lesioni della spalla (compreso rottura atraumatica della cuffia dei rotatori)", Cost = 214.50m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 18, Code = "PR8", Description = "Traumatismo dei nervi della spalla e del braccio (compreso avambraccio, polso e mano) - esiti funzionali", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 19, Code = "PR9", Description = "Traumatismo dei muscoli e dei tendini della spalla e del braccio (compreso avambraccio, polso e mano) esiti funzionali", Cost = 214.50m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 20, Code = "PR10", Description = "Frattura collo femore e di altre parti del femore di tipo traumatico (in soggetto < 65 anni) - esiti funzionali", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 21, Code = "PR11A", Description = "Frattura della rotula", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 22, Code = "PR11B", Description = "Frattura della tibia e del perone", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 23, Code = "PR11C", Description = "Frattura della caviglia", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 24, Code = "PR11D", Description = "Frattura di una o più ossa del tarso e metatarso", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 25, Code = "PR11E", Description = "Frattura di una o più falangi del piede", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 26, Code = "PR12A", Description = "Lussazione di ginocchio (esiti funzionali)", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 27, Code = "PR12B", Description = "Lussazione della caviglia (esiti funzionali)", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 28, Code = "PR12C", Description = "Lussazione del piede (esiti funzionali)", Cost = 228.20m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 29, Code = "PR13A", Description = "Distorsione dell'anca e della coscia (esiti funzionali)", Cost = 230.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 30, Code = "PR13B", Description = "Distorsione del ginocchio e della gamba (esiti funzionali)", Cost = 230.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 31, Code = "PR13C", Description = "Distorsione della caviglia e del piede (esiti funzionali)", Cost = 230.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 32, Code = "PR14", Description = "Disturbi della rotula, lesioni capsulo-legamentose del ginocchio e del menisco (trattamento post chirurgico)", Cost = 215.80m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 33, Code = "PR15", Description = "Traumatismo di nervi dell'anca e della coscia - compreso gamba, caviglia e piede - (esiti funzionali)", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 34, Code = "PR16", Description = "Traumatismo di muscoli e tendini della gamba (esiti funzionali)", Cost = 215.80m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 35, Code = "PR17", Description = "Frattura della colonna vertebrale senza menzione di lesione del midollo spinale", Cost = 221.30m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 36, Code = "PR18", Description = "Frattura del bacino (esiti funzionali)", Cost = 221.30m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 37, Code = "PR19", Description = "Lussazione della mandibola (esiti funzionali)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 38, Code = "PR20", Description = "Distorsione del rachide, della regione sacroiliaca e di altre parti non specificate del dorso (esiti funzionali)", Cost = 221.30m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 39, Code = "PR21", Description = "Disturbi dei plessi e delle radici nervose (associata a stenosi del canale vertebrale cervicale o lombare) solo in presenza di limitazione algofunzionale", Cost = 255.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 40, Code = "PR22", Description = "Compressione dei plessi e delle radici nervose (associata a sindrome brachialgica o sciatalgica) (solo in presenza di limitazione algofunzionale persistente)", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 41, Code = "PR23", Description = "Limitazione algofunzionale da mal di schiena acuto/postacuto persistente (2-3 settimane) e/o recidivante", Cost = 255.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 42, Code = "PR24", Description = "Alterazioni di continuità dell'osso (mancata consolidazione)", Cost = 214.50m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 43, Code = "PR25A", Description = "Calcificazione e ossificazione dei muscoli (come complicanza di eventi traumatici recenti)", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 44, Code = "PR25B", Description = "Malattie dei tessuti molli correlate ad uso eccessivo, pressione", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 45, Code = "PR26A", Description = "Esiti di intervento per impianto di artroprotesi ginocchio", Cost = 255.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 46, Code = "PR26B", Description = "Esiti di intervento per impianto di artroprotesi spalla", Cost = 255.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 47, Code = "PR26C", Description = "Esiti di intervento per impianto di artroprotesi anca", Cost = 255.40m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 48, Code = "PR27", Description = "Trauma da parto del sistema nervoso periferico senza lesione complessa del tronco nervoso (paralisi ostetrica)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 49, Code = "PR28", Description = "Deformità congenita dell'anca seguito da trattamento ortopedico (displasia dell'anca)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 50, Code = "PR29", Description = "Deformazione dei piedi (piede torto congenito)", Cost = 231.60m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 51, Code = "PR30", Description = "Torcicollo miogeno congenito del neonato", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 52, Code = "PR31I", Description = "Disturbi della voce di origine funzionale (compresi noduli, polipi, edemi e prolassi) e negli esiti di cordectomia semplice e paralisi cordale ricorrenziale (terapia individuale)", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 53, Code = "PR31G", Description = "Disturbi della voce di origine funzionale (compresi noduli, polipi, edemi e prolassi) e negli esiti di cordectomia semplice e paralisi cordale ricorrenziale (terapia di gruppo)", Cost = 51.00m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 54, Code = "PR32I", Description = "Disturbi semplici del linguaggio (late talkers, disturbi fonetici semplici) individuale", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 55, Code = "PR32G", Description = "Disturbi semplici del linguaggio (late talkers, disturbi fonetici semplici) di gruppo", Cost = 51.00m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 56, Code = "PR33I", Description = "Disturbi miofunzionali odontofacciali (inclusa deglutizione atipica) terapia individuale", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 57, Code = "PR33G", Description = "Disturbi miofunzionali odontofacciali (inclusa deglutizione atipica) (terapia di gruppo costituita da 3 a max 5 persone)", Cost = 51.00m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 58, Code = "PR34I", Description = "Disturbo respiratorio semplice senza insufficienza respiratoria (terapia individuale)", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 59, Code = "PR34G", Description = "Disturbo respiratorio semplice senza insufficienza respiratoria (terapia di gruppo)", Cost = 51.00m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 60, Code = "PR35A", Description = "Linfedemi degli arti (post mastectomia arto superiore o per altre condizioni patologiche arto inferiore) (costo per n. 5 accessi di 60 min.)", Cost = 152.90m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 61, Code = "PR35B", Description = "Incontinenza urinaria e/o fecale", Cost = 162.30m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 62, Code = "PR36A", Description = "Disturbi delle sinovie con limitazione algofunzionale", Cost = 195.10m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 63, Code = "PR36B", Description = "Borsopatie con limitazione algofunzionale", Cost = 195.10m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 64, Code = "PR36C", Description = "Entesopatie con limitazione algofunzionale", Cost = 195.10m, ModDate = seedDate, ModUser = seedUser },
      new PrCode { Id = 65, Code = "PR37", Description = "Attività motoria per esiti stabilizzati da patologie croniche (tariffa per 1 pacchetto di 20 accessi)", Cost = 60.00m, ModDate = seedDate, ModUser = seedUser }
    );
  }

  private static void SeedSettings(AppDbContext db) {
    if (!db.Settings.Any(s => s.Key == "AvailabilityStart")) {
      db.Settings.Add(new Setting { Key = "AvailabilityStart", Value = 28, Sorting = 10, ModDate = DateTime.Now, ModUser = 1 }); // 07:00
    }

    if (!db.Settings.Any(s => s.Key == "AvailabilityEnd")) {
      db.Settings.Add(new Setting { Key = "AvailabilityEnd", Value = 76, Sorting = 11, ModDate = DateTime.Now, ModUser = 1 }); // 19:00
    }

    if (!db.Settings.Any(s => s.Key == "RepartoTherapyStartingTime")) {
      db.Settings.Add(new Setting { Key = "RepartoTherapyStartingTime", Value = 5, Sorting = 20, ModDate = DateTime.Now, ModUser = 1 });
    }

    if (!db.Settings.Any(s => s.Key == "PalestraCoveringRepartoStartingTime")) {
      db.Settings.Add(new Setting { Key = "PalestraCoveringRepartoStartingTime", Value = 5, Sorting = 21, ModDate = DateTime.Now, ModUser = 1 });
    }

    if (!db.Settings.Any(s => s.Key == "RepartoCapacityWarningThreshold")) {
      db.Settings.Add(new Setting { Key = "RepartoCapacityWarningThreshold", Value = 75, Sorting = 30, ModDate = DateTime.Now, ModUser = 1 });
    }

    if (!db.Settings.Any(s => s.Key == "PacchettoScontoMinimo")) {
      db.Settings.Add(new Setting { Key = "PacchettoScontoMinimo", Value = 5, Sorting = 40, ModDate = DateTime.Now, ModUser = 1 });
    }

    if (!db.Settings.Any(s => s.Key == "PacchettoScontoMassimo")) {
      db.Settings.Add(new Setting { Key = "PacchettoScontoMassimo", Value = 25, Sorting = 41, ModDate = DateTime.Now, ModUser = 1 });
    }

    // Monthly ceiling (euro) for Convenzione revenue - 0 = not set, no budget label.
    if (!db.Settings.Any(s => s.Key == "FatturatoMassimoASL")) {
      db.Settings.Add(new Setting { Key = "FatturatoMassimoASL", Value = 0, Sorting = 50, ModDate = DateTime.Now, ModUser = 1 });
    }

    db.SaveChanges();
  }

  private static void SeedFixedHolidays(AppDbContext db) {
    var fixedHolidays = new (string Name, int Month, int Day)[] {
      ("Capodanno", 1, 1),
      ("Epifania", 1, 6),
      ("Liberazione", 4, 25),
      ("Festa del Lavoro", 5, 1),
      ("Festa della Repubblica", 6, 2),
      ("Ferragosto", 8, 15),
      ("San Gennaro", 9, 19),
      ("Ognissanti", 11, 1),
      ("Immacolata", 12, 8),
      ("Natale", 12, 25),
      ("Santo Stefano", 12, 26)
    };

    foreach (var holiday in fixedHolidays) {
      var exists = db.Vacations.Any(v =>
        v.IsYearIndependent == 1 && v.Month == holiday.Month && v.Day == holiday.Day);

      if (!exists) {
        db.Vacations.Add(new Vacation {
          Name = holiday.Name,
          IsYearIndependent = 1,
          Month = holiday.Month,
          Day = holiday.Day,
          IsSeeded = 1
        });
      }
    }

    db.SaveChanges();
  }
}
