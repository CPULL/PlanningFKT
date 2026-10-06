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

  // Pagato / Da pagare toggle (patient page and Pagamenti page) - applies to
  // every therapy, whatever its BillingCategory (CPU's call).
  public bool Pagato { get; set; }

  // "Non rinnova" checkbox (patient page): the patient won't renew - hides the
  // Rinnova button and the "Terapia in scadenza" alert (CPU's call).
  public bool NonRinnova { get; set; }

  // Private/Assicurazione therapy priced by one of the patient's Pacchetti (CPU):
  // the therapy is worth the pacchetto's final price. Null = no pacchetto (PR
  // codes for Convenzione, otherwise the Tariffario unit prices).
  public int? TherapyPacketId { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
