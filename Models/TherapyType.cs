using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class TherapyType {
  public int Id { get; set; }

  [Required]
  [MaxLength(32)]
  public string Name { get; set; } = string.Empty;

  [MaxLength(8)]
  public string? Abbreviazione { get; set; }

  public int Duration { get; set; }

  public int Category { get; set; }

  public int Type { get; set; }

  public int Color { get; set; }

  public int? MaxParallelMale { get; set; }

  public int? MaxParallelFemale { get; set; }

  public int TherapyWeeklyFrequency { get; set; }

  // Ginnastica Attiva is an autonomous-exercise extension of a Rieducazione
  // Motoria/Isocinetica session - manually flagged true only for those two
  // TherapyType rows (CPU's convention, avoids fragile name-matching).
  public int AllowsGinnasticaAttiva { get; set; }

  public int IsActive { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
