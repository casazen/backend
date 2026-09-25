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

    /// <summary>
    /// Identification code of the APE as printed on it (LT-10, template data of LT-03); only for
    /// <see cref="DocumentType.Ape"/>. Free text within a length: its format varies by region and is not validated.
    /// </summary>
    [MaxLength(ApeDocumentLimits.CodeMaxLength)]
    public string? ApeCode { get; set; }

    /// <summary>Energy class printed on the APE (e.g. "A4", "G"), stored upper-case; only for <see cref="DocumentType.Ape"/>.</summary>
    [MaxLength(ApeDocumentLimits.EnergyClassMaxLength)]
    public string? ApeEnergyClass { get; set; }

    /// <summary>Code and energy class are both set.</summary>
    public bool HasApeIdentification =>
        !string.IsNullOrWhiteSpace(ApeCode) && !string.IsNullOrWhiteSpace(ApeEnergyClass);
}

/// <summary>Lengths of the APE identification (LT-10).</summary>
public static class ApeDocumentLimits
{
    public const int CodeMaxLength = 100;
    public const int EnergyClassMaxLength = 3;
}
