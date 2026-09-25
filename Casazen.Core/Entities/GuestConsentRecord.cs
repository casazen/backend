using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One event of the privacy history of a guest (CO-15, A5-14, A5-15): the privacy notice (art. 13 GDPR) presented on the
/// check-in portal, and the marketing consent (art. 6.1.a, art. 7) granted by the guest, withdrawn on the guest's
/// documented request or expired by the retention policy. Append-only: a new state is a new row, so the controller can
/// prove which text version the guest saw and when (art. 7.1 GDPR, accountability art. 5.2).
/// </summary>
/// <remarks>
/// The Alloggiati Web registration is a legal obligation (art. 109 TULPS, art. 6.1.c GDPR): it is never a consent, only
/// the notice presented is recorded. The host's own consents (ToS, DPA) are <see cref="ConsentRecord"/>. On anonymization
/// <see cref="IpAddress"/> and <see cref="Note"/> are cleared; purpose, action, version and time stay as proof without
/// personal data.
/// </remarks>
[Table("GuestConsentRecords")]
public class GuestConsentRecord : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the guest (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    public GuestConsentPurpose Purpose { get; set; }

    public GuestConsentAction Action { get; set; }

    /// <summary>
    /// Version of the text presented or consented to (<c>Gdpr:PrivacyNoticeVersion</c>, <c>Gdpr:MarketingConsentVersion</c>).
    /// For a withdrawal or an expiry: the version of the consent it ends, empty when that consent predates CO-15.
    /// </summary>
    [MaxLength(100)]
    public string Version { get; set; } = string.Empty;

    public GuestConsentSource Source { get; set; }

    /// <summary>Client IP of the guest's own action (portal); null for the host and the retention job. Cleared on anonymization.</summary>
    [MaxLength(50)]
    public string? IpAddress { get; set; }

    /// <summary>How the guest asked the host to withdraw (date, channel): required for <see cref="GuestConsentSource.HostOnGuestRequest"/>. Cleared on anonymization.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>User id (Auth0 <c>sub</c>) of the host who recorded the withdrawal; null for the guest and the retention job.</summary>
    [MaxLength(200)]
    public string? RecordedByUserId { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>What the row is about. Stored as an integer: never renumber.</summary>
public enum GuestConsentPurpose
{
    /// <summary>Privacy notice (art. 13 GDPR) of the check-in portal: presented, never a consent.</summary>
    PrivacyNotice = 1,

    /// <summary>Marketing communications: optional consent of the guest (art. 6.1.a GDPR).</summary>
    Marketing = 2,
}

/// <summary>What happened. Stored as an integer: never renumber.</summary>
public enum GuestConsentAction
{
    /// <summary>The notice was presented to the guest, who then submitted the data (no checkbox: legal obligation).</summary>
    NoticePresented = 1,

    /// <summary>The guest gave the consent (opt-in).</summary>
    Granted = 2,

    /// <summary>The consent was withdrawn (art. 7.3 GDPR).</summary>
    Withdrawn = 3,

    /// <summary>The consent ended with the configured retention period (<c>Gdpr:Retention:Marketing</c>).</summary>
    Expired = 4,
}

/// <summary>Who recorded the row. Stored as an integer: never renumber.</summary>
public enum GuestConsentSource
{
    /// <summary>The guest, on the public check-in portal.</summary>
    GuestPortal = 1,

    /// <summary>The host, on the guest's documented request (withdrawal only: the host can never grant a consent).</summary>
    HostOnGuestRequest = 2,

    /// <summary>The GDPR retention job.</summary>
    RetentionPolicy = 3,
}
