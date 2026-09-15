namespace minerva.planningfkt.models;

public class TherapyPart {
  public int Id { get; set; }

  public int TherapyId { get; set; }

  public int TherapyTypeId { get; set; }

  public int SessionCount { get; set; }

  // 0 = Ginnastica Attiva not requested for this part. Otherwise the default
  // duration (in 15-min slots) each newly-placed session gets auto-attached
  // with - only meaningful when TherapyType.AllowsGinnasticaAttiva. Set once
  // at creation ("Include Ginnastica Attiva" checkbox); each individual
  // TherapySlot's own value can still be adjusted afterward.
  public int DefaultGinnasticaAttivaSlots { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
