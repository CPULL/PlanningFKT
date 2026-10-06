namespace minerva.planningfkt.models;

// PR codes of a Convenzione therapy's prescription (CPU: at least one, usually
// 1-2, up to 5). The therapy's value is the sum of their costs, spread evenly
// over the therapy's sessions (see AppController.Fatturato).
public class TherapyPrCode {
  public int Id { get; set; }

  public int TherapyId { get; set; }

  public int PrCodeId { get; set; }
}
