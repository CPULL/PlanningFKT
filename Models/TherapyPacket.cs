using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class TherapyPacket {
  public int Id { get; set; }

  [MaxLength(100)]
  public string? Name { get; set; }

  public int? PatientId { get; set; }

  // Free-text name, used only when the packet is "per un paziente specifico" but
  // the person isn't a real Patient record - PatientId and PersonName are mutually
  // exclusive in practice (frontend clears one when the other is set), both null
  // means Generico.
  [MaxLength(150)]
  public string? PersonName { get; set; }

  [MaxLength(100)]
  public string? Approvatore { get; set; }

  public decimal TotalPrice { get; set; }

  public DateTime CreatedAt { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
