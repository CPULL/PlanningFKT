using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class Therapy {
  public int Id { get; set; }

  [MaxLength(255)]
  public string? Name { get; set; }

  public int Status { get; set; }

  public int PatientId { get; set; }

  // Set at creation, never changes - see TherapyBillingCategory.
  public int BillingCategory { get; set; }

  // Meaningless when BillingCategory == Privata - the UI just doesn't show this
  // for private therapies rather than tracking a "not needed" value for them.
  public int FoglioFirmaStatus { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
