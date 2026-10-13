using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("HighTensionAreaComuni")]
public class HighTensionAreaComune
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(100)]
    public string Comune { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Region { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string SourceReference { get; set; } = string.Empty;

    public bool VerifiedDirectly { get; set; }

    public DateTime? LastVerifiedAt { get; set; }
}
