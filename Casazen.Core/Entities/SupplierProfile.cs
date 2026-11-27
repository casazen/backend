using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("SupplierProfiles")]
public class SupplierProfile
{
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

    [Column(TypeName = "jsonb")]
    public string CategoriesJson { get; set; } = "[]";

    [Column(TypeName = "jsonb")]
    public string ComuniJson { get; set; } = "[]";

    /// <summary>
    /// JSON array of ISTAT codes (6 digits) of the comuni the supplier chose from the official list (<c>Comuni</c>, SU-04),
    /// each one validated against it. The matching with the property is by code.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string ComuneIstatCodesJson { get; set; } = "[]";

    [MaxLength(2000)]
    public string? Bio { get; set; }

    [Column(TypeName = "jsonb")]
    public string PhotoUrlsJson { get; set; } = "[]";

    public DateTime? TosAcceptedAt { get; set; }

    [MaxLength(100)]
    public string? TosVersion { get; set; }

    /// <summary>
    /// Step (1-5) of the activation wizard the supplier reached, saved by the server so that it resumes where it stopped
    /// on any device (SU-05, A4-09). Null until the wizard saved a step: the first incomplete step is shown.
    /// </summary>
    public int? ActivationStep { get; set; }

    public DateTime? SuspendedAt { get; set; }

    [MaxLength(500)]
    public string? SuspensionReason { get; set; }

    public CalendarSyncType CalendarSyncType { get; set; } = CalendarSyncType.None;

    [MaxLength(2048)]
    public string? IcalFeedUrl { get; set; }

    [MaxLength(512)]
    public string? GoogleCalendarRefreshToken { get; set; }

    public DateTime? CalendarLastSyncAt { get; set; }

    [MaxLength(500)]
    public string? CalendarSyncError { get; set; }

    public SupplierCalendarSyncStatus CalendarSyncStatus { get; set; } = SupplierCalendarSyncStatus.None;

    [MaxLength(100)]
    public string? ShowcaseSlug { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the claim token handed out by an anonymous self-serve registration
    /// (<see cref="Casazen.Core.Suppliers.SupplierClaimTokens"/>, SU-02): the account that signs up afterwards links
    /// itself to this profile with it. The token itself is never stored. Null for profiles created by a signed-in
    /// user (linked at once) or before SU-02. Kept after the claim, so a reused token is recognized as used.
    /// </summary>
    [MaxLength(64)]
    public string? ClaimTokenHash { get; set; }

    public DateTime? ClaimTokenExpiresAt { get; set; }

    /// <summary>
    /// The commission CasaZen keeps on this supplier's services paid inside CasaZen, as a percentage (0 to 50), instead of the
    /// platform's <c>SupplierPayments:CommissionPercent</c> (SP-15a, decision D3): e.g. 0 for a free period. Null = the platform
    /// percentage. Read when a payment is created and snapshotted on it; set by an admin
    /// (<c>PUT api/admin/suppliers/{orgId}/commission</c>, SP-15b), with <see cref="CommissionOverrideUntil"/> when it is a period.
    /// </summary>
    [Column(TypeName = "numeric(5,2)")]
    public decimal? CommissionPercentOverride { get; set; }

    public DateTime? CommissionOverrideUntil { get; set; }

    public bool GuestPortalMarketplaceEnabled { get; set; }

    public DateTime? GuestPortalMarketplaceFirstPublishedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
