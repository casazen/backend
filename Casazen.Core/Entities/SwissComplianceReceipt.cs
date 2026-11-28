using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("SwissComplianceReceipts")]
public class SwissComplianceReceipt : ITenantOwned
{
    public const int TypeMaxLength = 40;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [Required, MaxLength(TypeMaxLength)]
    public string Type { get; set; } = string.Empty;

    public DateTime ReportDate { get; set; }

    [Required, MaxLength(200)]
    public string Reference { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string UploadedFileHash { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
