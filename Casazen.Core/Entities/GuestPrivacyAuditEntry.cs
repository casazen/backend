using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Audit of an operation on the personal data of a guest (CO-15): export, anonymization, erasure, retention, marketing
/// withdrawal. Holds <b>no personal data of the guest</b>: only the guest id (pseudonymous, and meaningless once the
/// record is anonymized), the host who acted, the category and counters. No foreign key to <see cref="Guest"/>, so the
/// entry survives the removal of a guest without bookings (accountability, art. 5.2 GDPR).
/// </summary>
[Table("GuestPrivacyAuditEntries")]
public class GuestPrivacyAuditEntry : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the guest (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid GuestId { get; set; }

    public GuestPrivacyAuditAction Action { get; set; }

    /// <summary>Data category of a <see cref="GuestPrivacyAuditAction.RetentionApplied"/> entry; null otherwise.</summary>
    public GuestDataCategory? Category { get; set; }

    /// <summary>User id (Auth0 <c>sub</c>) of the host; null for the retention job.</summary>
    [MaxLength(200)]
    public string? ActorUserId { get; set; }

    /// <summary>Guests of the stays (<see cref="StayGuest"/>) anonymized by the operation.</summary>
    public int StayGuestsAnonymized { get; set; }

    /// <summary>Objects deleted from the private storage (document scans).</summary>
    public int FilesDeleted { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Operation of a <see cref="GuestPrivacyAuditEntry"/>. Stored as an integer: never renumber.</summary>
public enum GuestPrivacyAuditAction
{
    /// <summary>Data exported for the guest (art. 15 and 20 GDPR).</summary>
    Exported = 1,

    /// <summary>Record anonymized on the host's request, the row kept for its bookings.</summary>
    Anonymized = 2,

    /// <summary>Erasure (art. 17 GDPR): anonymized and marked deleted, or removed when nothing referenced it.</summary>
    Erased = 3,

    /// <summary>A retention period of <see cref="GuestPrivacyAuditEntry.Category"/> expired and was applied by the job.</summary>
    RetentionApplied = 4,

    /// <summary>Marketing consent withdrawn by the host on the guest's documented request.</summary>
    MarketingConsentWithdrawn = 5,
}

/// <summary>
/// Categories of guest data with their own retention period (<c>Gdpr:Retention:*</c>, CO-15). Stored as an integer:
/// never renumber.
/// </summary>
public enum GuestDataCategory
{
    /// <summary>Files of the identity document scans in the private storage.</summary>
    DocumentScans = 1,

    /// <summary>
    /// Data of the Alloggiati Web registration (art. 109 TULPS): birth, citizenship, sex and document of the booker and every
    /// guest of the stay (<see cref="StayGuest"/>), plus the document scan.
    /// </summary>
    AlloggiatiData = 2,

    /// <summary>Marketing consent: ends after the period from the date it was given.</summary>
    Marketing = 3,

    /// <summary>
    /// Identity and contacts of the booker kept with bookings and payments (fiscal and accounting records): when the
    /// period expires the whole record is anonymized.
    /// </summary>
    FiscalData = 4,
}
