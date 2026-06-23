using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// Supplier professional profile linked 1-to-1 to an <see cref="Org"/> with
/// <c>OrgType = Supplier</c>. Stores identity, service categories, and activation state (US-022 / #292).
/// </summary>
[Table("SupplierProfiles")]
public class SupplierProfile
{
    /// <summary>Primary key — same as the owning <see cref="Org.Id"/>.</summary>
    [Key]
    public Guid OrgId { get; set; }

    [Required]
    public SupplierStatus Status { get; set; } = SupplierStatus.Pending;

    [Required, MaxLength(300)]
    public string LegalName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? VatNumber { get; set; }

    [Required, MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>JSON array of service category codes (e.g. ["cleaning","maintenance"]).</summary>
    [Column(TypeName = "jsonb")]
    public string CategoriesJson { get; set; } = "[]";

    /// <summary>JSON array of Italian comune codes where the supplier operates.</summary>
    [Column(TypeName = "jsonb")]
    public string ComuniJson { get; set; } = "[]";

    [MaxLength(2000)]
    public string? Bio { get; set; }

    /// <summary>JSON array of photo URLs.</summary>
    [Column(TypeName = "jsonb")]
    public string PhotoUrlsJson { get; set; } = "[]";

    public DateTime? TosAcceptedAt { get; set; }

    // Calendar sync
    public CalendarSyncType CalendarSyncType { get; set; } = CalendarSyncType.None;

    [MaxLength(2048)]
    public string? IcalFeedUrl { get; set; }

    [MaxLength(512)]
    public string? GoogleCalendarRefreshToken { get; set; }

    public DateTime? CalendarLastSyncAt { get; set; }

    [MaxLength(500)]
    public string? CalendarSyncError { get; set; }

    /// <summary>URL-friendly slug for the public showcase page at /s/{slug}.</summary>
    [MaxLength(100)]
    public string? ShowcaseSlug { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
