using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class Patient {
  public int Id { get; set; }

  [Required]
  [MaxLength(150)]
  public string Name { get; set; } = string.Empty;

  [Required]
  [MaxLength(20)]
  public string Phone { get; set; } = string.Empty;

  public DateTime? DateOfBirth { get; set; }

  public int Sex { get; set; }

  [MaxLength(128)]
  public string? DocumentFileName { get; set; }

  public DateTime? DocumentAttachedDate { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
