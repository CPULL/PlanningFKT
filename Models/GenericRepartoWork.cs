namespace minerva.planningfkt.models;

// A non-reparto therapist marking themselves as spontaneously helping Reparto
// right now (CPU's call) - purely a log for the monthly report, no capacity or
// scheduling effect. One row per (TherapistId, Date, TimeSlot) actually marked;
// toggling off just deletes the row.
public class GenericRepartoWork {
  public int Id { get; set; }

  public int TherapistId { get; set; }

  public DateOnly Date { get; set; }

  public int TimeSlot { get; set; }
}
