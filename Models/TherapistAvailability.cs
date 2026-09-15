namespace minerva.planningfkt.models;

public class TherapistAvailability {
  public int Id { get; set; }

  public int TherapistId { get; set; }

  public int DayOfWeek { get; set; }

  public int StartTime { get; set; }

  public int EndTime { get; set; }

  // Null = this window behaves per the therapist's main OperatingArea, as
  // before. Set = this specific window is pinned to ONE pure area (Palestra=0
  // or Reparto=2 only - never a "helping" composite), overriding whatever the
  // main area would otherwise imply for that time (CPU's call: a therapist can
  // be, say, mostly-Palestra-plus-aiuto in the morning and pure Reparto in the
  // afternoon, on a fixed weekly schedule).
  public int? OverrideOperatingArea { get; set; }
}
