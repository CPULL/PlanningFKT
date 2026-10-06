using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

// Regional rehabilitation package codes (AIOP Campania "Allegato 3 - Pacchetti
// riabilitativi"): code, description and tariff. Seeded from the published list
// (see DbSeeding); how codes get attached to therapies is still to be defined.
public class PrCode {
  public int Id { get; set; }

  [MaxLength(10)]
  public string Code { get; set; } = string.Empty;

  [MaxLength(300)]
  public string Description { get; set; } = string.Empty;

  public decimal Cost { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
