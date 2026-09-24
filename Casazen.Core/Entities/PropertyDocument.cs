using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("PropertyDocuments")]
public class PropertyDocument : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    [Required, MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string StorageUrl { get; set; } = string.Empty;

    [Required]
    public DocumentType DocumentType { get; set; }

    [Required, MaxLength(255)]
    public string UploadedBy { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
