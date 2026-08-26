namespace minerva.planningfkt.models;

public class TherapyPart {
  public int Id { get; set; }

  public int TherapyId { get; set; }

  public int TherapyTypeId { get; set; }

  public int SessionCount { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
