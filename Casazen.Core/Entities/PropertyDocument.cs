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

    public Guid OrgId { get; set; }

    [Required, MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string StorageUrl { get; set; } = string.Empty;

    [Required]
    public DocumentType DocumentType { get; set; }

    public bool IsTestData { get; set; }

    [Required, MaxLength(255)]
    public string UploadedBy { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(ApeDocumentLimits.CodeMaxLength)]
    public string? ApeCode { get; set; }

    [MaxLength(ApeDocumentLimits.EnergyClassMaxLength)]
    public string? ApeEnergyClass { get; set; }

    public bool HasApeIdentification => IsApeIdentified(ApeCode, ApeEnergyClass);

    public static bool IsApeIdentified(string? code, string? energyClass) =>
        !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(energyClass);
}

public static class ApeDocumentLimits
{
    public const int CodeMaxLength = 100;
    public const int EnergyClassMaxLength = 3;
}
