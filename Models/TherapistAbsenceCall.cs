namespace minerva.planningfkt.models;

// "Gestisci assenza" tracking - one row per (therapist, patient) pair for today's
// urgent absence-call list. A therapist "is in today's list" simply by having any
// row here; each row also carries whether that specific patient has been called.
// Pruned automatically (rows older than 24h deleted on every fetch) - this is a
// same-day tool, nothing here should ever need to outlive the day.
public class TherapistAbsenceCall {
  public int Id { get; set; }

  public int TherapistId { get; set; }

  public int PatientId { get; set; }

  public DateTime CreatedAt { get; set; }

  public bool Called { get; set; }
}
