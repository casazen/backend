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

    /// <summary>
    /// JSON array of the comuni where the supplier operates as the supplier or an admin wrote them: free text or old codes
    /// (<c>H501</c>, <c>Roma</c>). Kept for what was typed before the official list (SU-04); the comuni chosen from it are in
    /// <see cref="ComuneIstatCodesJson"/>. A supplier covers the union of the two.
    /// </summary>
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

    /// <summary>JSON array of photo URLs.</summary>
    [Column(TypeName = "jsonb")]
    public string PhotoUrlsJson { get; set; } = "[]";

    public DateTime? TosAcceptedAt { get; set; }

    /// <summary>
    /// Version of the Terms of Service (<c>Legal:Documents:Tos:Version</c>) the supplier accepted at <see cref="TosAcceptedAt"/>
    /// (SU-05, A4-31). Null for a supplier that accepted before the version was recorded: it accepted a text it could not
    /// read, so the console asks it to accept the current version (without blocking its work, see the runbook). The full
    /// history is in the <c>ConsentRecords</c> of the supplier org.
    /// </summary>
    [MaxLength(100)]
    public string? TosVersion { get; set; }

    /// <summary>
    /// Step (1-5) of the activation wizard the supplier reached, saved by the server so that it resumes where it stopped
    /// on any device (SU-05, A4-09). Null until the wizard saved a step: the first incomplete step is shown.
    /// </summary>
    public int? ActivationStep { get; set; }

    /// <summary>UTC moment a platform admin suspended the profile (SU-12); null while it is not suspended.</summary>
    public DateTime? SuspendedAt { get; set; }

    /// <summary>
    /// Why the admin suspended the profile (SU-12): an internal note, shown to admins only. Null while it is not
    /// suspended; the full history is in <see cref="SupplierAdminAuditEntry"/>.
    /// </summary>
    [MaxLength(500)]
    public string? SuspensionReason { get; set; }

    // Calendar sync
    public CalendarSyncType CalendarSyncType { get; set; } = CalendarSyncType.None;

    [MaxLength(2048)]
    public string? IcalFeedUrl { get; set; }

    [MaxLength(512)]
    public string? GoogleCalendarRefreshToken { get; set; }

    public DateTime? CalendarLastSyncAt { get; set; }

    [MaxLength(500)]
    public string? CalendarSyncError { get; set; }

    /// <summary>State of the iCal sync (SU-15): <c>Syncing</c> while its Hangfire job is queued or running.</summary>
    public SupplierCalendarSyncStatus CalendarSyncStatus { get; set; } = SupplierCalendarSyncStatus.None;

    /// <summary>URL-friendly slug for the public showcase page at /s/{slug}.</summary>
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

    /// <summary>UTC expiry of <see cref="ClaimTokenHash"/>.</summary>
    public DateTime? ClaimTokenExpiresAt { get; set; }

    /// <summary>
    /// The commission CasaZen keeps on this supplier's services paid inside CasaZen, as a percentage (0 to 50), instead of the
    /// platform's <c>SupplierPayments:CommissionPercent</c> (SP-15a, decision D3): e.g. 0 for a free period. Null = the platform
    /// percentage. Read when a payment is created and snapshotted on it; set by an admin
    /// (<c>PUT api/admin/suppliers/{orgId}/commission</c>, SP-15b), with <see cref="CommissionOverrideUntil"/> when it is a period.
    /// </summary>
    [Column(TypeName = "numeric(5,2)")]
    public decimal? CommissionPercentOverride { get; set; }

    /// <summary>
    /// When the override stops (SP-15b, the "periodo gratuito" of decision D3): the override applies to the payments created
    /// before this instant, and from then on the platform percentage applies again without anybody having to remember to take it
    /// off. Null = no end (the override stays until an admin removes it). Only meaningful together with
    /// <see cref="CommissionPercentOverride"/>.
    /// </summary>
    public DateTime? CommissionOverrideUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
