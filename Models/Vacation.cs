using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class Vacation {
  public int Id { get; set; }

  public int? TherapistId { get; set; }

  // Used only for Festività rows (fixed or movable) - Assenze rows are identified by
  // the therapist's own name instead.
  [MaxLength(200)]
  public string? Name { get; set; }

  public int? AMPM { get; set; }

  public int? Malattia { get; set; }

  public int? IsYearIndependent { get; set; }

  public int? Month { get; set; }

  public int? Day { get; set; }

  // Used by Assenza terapista (range, EndDate always equal to StartDate for a single
  // day - never left null) and by Festività variabile (a fixed date range).
  public DateOnly? StartDate { get; set; }

  public DateOnly? EndDate { get; set; }

  // Protects the seeded fixed national holidays from removal - anything created
  // through the UI (including other Festività) is always 0 and stays removable.
  public int IsSeeded { get; set; }
}
