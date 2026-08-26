using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class Therapist {
  public int Id { get; set; }

  [Required]
  [MaxLength(64)]
  public string Name { get; set; } = string.Empty;

  [Required]
  [MaxLength(20)]
  public string Phone { get; set; } = string.Empty;

  public int IsActive { get; set; }

  public int OperatingArea { get; set; }

  public int OvertimeAllowed { get; set; }

  [MaxLength(255)]
  public string? PasswordHash { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
