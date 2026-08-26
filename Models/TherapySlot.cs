namespace minerva.planningfkt.models;

public class TherapySlot {
  public int Id { get; set; }

  public int TherapyPartId { get; set; }

  public DateOnly Date { get; set; }

  public int TimeSlot { get; set; }

  public int? TherapistId { get; set; }

  public int Status { get; set; }

  public int? RescheduledToId { get; set; }

  // Set only by Presenze's status-cycling action for now (CPU's scope call) - other
  // places that touch Status/TherapistId don't populate these yet.
  public DateTime? ModDate { get; set; }

  public int? ModUser { get; set; }
}
