namespace minerva.planningfkt.models;

public class TherapyPacketItem {
  public int Id { get; set; }

  public int TherapyPacketId { get; set; }

  public int TherapyTypeId { get; set; }

  public int SessionCount { get; set; }

  // Snapshotted at save time from TherapyPacketConfig, so an old Pacchetto keeps its
  // original numbers even if the rate card changes later.
  public decimal UnitPrice { get; set; }

  public decimal DiscountPercent { get; set; }
}
