namespace minerva.planningfkt.models;

public class TherapySlot {
  public int Id { get; set; }

  public int TherapyPartId { get; set; }

  public DateOnly Date { get; set; }

  public int TimeSlot { get; set; }

  public int? TherapistId { get; set; }

  public int Status { get; set; }

  // 0 = none. Positive N = N x 15 min of autonomous exercise AFTER this session;
  // negative N = N x 15 min BEFORE it. Only meaningful when this slot's
  // TherapyType.AllowsGinnasticaAttiva - same slot record, no separate status/
  // attendance/session-count effect (CPU's call).
  public int GinnasticaAttivaSlots { get; set; }

  public int? RescheduledToId { get; set; }

  // Set only by Presenze's status-cycling action for now (CPU's scope call) - other
  // places that touch Status/TherapistId don't populate these yet.
  public DateTime? ModDate { get; set; }

  public int? ModUser { get; set; }
}
