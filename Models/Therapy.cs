using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class Therapy {
  public int Id { get; set; }

  [MaxLength(255)]
  public string? Name { get; set; }

  public int Status { get; set; }

  public int PatientId { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
