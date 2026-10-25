using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Archive of Swiss compliance file submissions (Ticino police notification XML, OTR tourist tax CSV).
/// Proves that the host fulfilled the obligation (ADR-016).
/// </summary>
[Table("SwissComplianceReceipts")]
public class SwissComplianceReceipt : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (AC2). Inherited from the property; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Type of submission: "Police" (Ticino XML), "OTR" (overnight tax CSV).</summary>
    [Required, MaxLength(20)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Date of the report (Europe/Zurich calendar date).</summary>
    public DateTime ReportDate { get; set; }

    /// <summary>Reference identifier of the submission (filename, batch ID, or host-chosen reference).</summary>
    [Required, MaxLength(200)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the uploaded file content (hex string) for integrity verification.</summary>
    [Required, MaxLength(64)]
    public string UploadedFileHash { get; set; } = string.Empty;

    /// <summary>When the file was uploaded (UTC).</summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
