using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Encrypted storage of guest identity document images for OCR (NF-10, ADR-009-R21, ADR-013-R3).
/// NEVER stored for Ticino (CantonCode = TI) properties per RL 942.120 art. 6 (ADR-013-R6).
/// Deleted after Alloggiati receipt or at decided retention per NF-10. Images are encrypted at rest.
/// </summary>
[Table("DocumentImages")]
public class DocumentImage : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (AC2). Inherited from the booking's property; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>
    /// Encrypted URL to the front of the identity document in private storage (ADR-009-R21).
    /// Encrypted at rest with purpose DocumentImagePurpose.
    /// </summary>
    [Required]
    public string FrontImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted URL to the back of the identity document in private storage (ADR-009-R21).
    /// Nullable for documents with single-sided content. Encrypted at rest with purpose DocumentImagePurpose.
    /// </summary>
    public string? BackImageUrl { get; set; }

    /// <summary>When the document images were uploaded by the guest (UTC).</summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When OCR/MRZ extraction completed (UTC). Null while processing or never processed.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// When the images were deleted per NF-10 retention rules (Alloggiati receipt received).
    /// Null while they exist. Once set, the URLs no longer point to accessible storage.
    /// </summary>
    public DateTime? DeletedAt { get; set; }
}
