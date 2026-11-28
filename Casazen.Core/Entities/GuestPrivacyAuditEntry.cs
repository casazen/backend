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

    public Guid OrgId { get; set; }

    public Guid GuestId { get; set; }

    public GuestPrivacyAuditAction Action { get; set; }

    /// <summary>Data category of a <see cref="GuestPrivacyAuditAction.RetentionApplied"/> entry; null otherwise.</summary>
    public GuestDataCategory? Category { get; set; }

    [MaxLength(200)]
    public string? ActorUserId { get; set; }

    public int StayGuestsAnonymized { get; set; }

    public int FilesDeleted { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public enum GuestPrivacyAuditAction
{
    Exported = 1,

    Anonymized = 2,

    /// <summary>Erasure (art. 17 GDPR): anonymized and marked deleted, or removed when nothing referenced it.</summary>
    Erased = 3,

    RetentionApplied = 4,

    MarketingConsentWithdrawn = 5,
}

/// <summary>
/// Categories of guest data with their own retention period (<c>Gdpr:Retention:*</c>, CO-15). Stored as an integer:
/// never renumber.
/// </summary>
public enum GuestDataCategory
{
    DocumentScans = 1,

    /// <summary>
    /// Data of the Alloggiati Web registration (art. 109 TULPS): birth, citizenship, sex and document of the booker and every
    /// guest of the stay (<see cref="StayGuest"/>), plus the document scan.
    /// </summary>
    AlloggiatiData = 2,

    Marketing = 3,

    FiscalData = 4,
}
