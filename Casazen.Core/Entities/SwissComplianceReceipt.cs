using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Archive of Swiss compliance file submissions (Ticino police notification, OTR tourist tax CSV) and of the documents the Ticino rules keep five
/// years (the sheet of a group of six or more, the written authorization of the parents of an unaccompanied minor: decision D11).
/// Proves that the host fulfilled the obligation (ADR-016). The documents of the police are deleted five years after their date by the daily
/// retention job (NF-128); the OTR reports are not.
/// </summary>
[Table("SwissComplianceReceipts")]
public class SwissComplianceReceipt : ITenantOwned
{
    /// <summary>Longest <see cref="Type"/>: «ParentalAuthorization» has 21 characters, the column was 20 until NF-128.</summary>
    public const int TypeMaxLength = 40;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (AC2). Inherited from the property; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>
    /// Type of the document (<c>SwissComplianceReceipts.StoredType</c>): "Police" (the receipt of a Ticino police notification), "OTR" (overnight tax
    /// CSV), "ParentalAuthorization" (the parents' authorization of an unaccompanied minor), "GroupSheet" (the sheet of a group of six or more).
    /// </summary>
    [Required, MaxLength(TypeMaxLength)]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Date of the report (Europe/Zurich calendar date): the arrival day of a police notification, of a group sheet and of a parents' authorization,
    /// the first day of an OTR period. The five years of the retention run from it.
    /// </summary>
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
