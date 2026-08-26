namespace minerva.planningfkt.models;

// Rate card: one row per TherapyType, used only to prefill a new TherapyPacketItem's
// UnitPrice/DiscountPercent when building a Pacchetto. TherapyType itself is never
// touched by Pacchetti - this is where their price/discount info actually lives.
public class TherapyPacketConfig {
  public int Id { get; set; }

  public int TherapyTypeId { get; set; }

  public decimal UnitPrice { get; set; }

  public decimal DefaultDiscountPercent { get; set; }

  public DateTime? ModDate { get; set; }

  public int? ModUser { get; set; }
}
