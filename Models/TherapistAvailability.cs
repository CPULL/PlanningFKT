namespace minerva.planningfkt.models;

public class TherapistAvailability {
  public int Id { get; set; }

  public int TherapistId { get; set; }

  public int DayOfWeek { get; set; }

  public int StartTime { get; set; }

  public int EndTime { get; set; }
}
