using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PropertyDocuments")]
public class PropertyDocument
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [Required, MaxLength(100)]
    public string DocumentType { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string FileUrl { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
