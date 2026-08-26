using System.ComponentModel.DataAnnotations;

namespace minerva.planningfkt.models;

public class Setting {
  public int Id { get; set; }

  [Required]
  [MaxLength(100)]
  public string Key { get; set; } = string.Empty;

  public int Value { get; set; }

  public DateTime ModDate { get; set; }

  public int ModUser { get; set; }
}
