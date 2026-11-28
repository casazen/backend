using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
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

    [MaxLength(50)]
    public string? IpAddress { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    [MaxLength(200)]
    public string? RecordedByUserId { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public enum GuestConsentPurpose
{
    /// <summary>Privacy notice (art. 13 GDPR) of the check-in portal: presented, never a consent.</summary>
    PrivacyNotice = 1,

    Marketing = 2,

    /// <summary>
    /// The soft opt-in notice presented to a direct customer when the contact is collected (art. 130 c. 4 Codice privacy, PO-07, NF-90): the
    /// customer is told that the address can be used for similar services and can object in every message. The notice is presented, never a consent.
    /// </summary>
    SoftOptInNotice = 3,
}

public enum GuestConsentAction
{
    NoticePresented = 1,

    Granted = 2,

    Withdrawn = 3,

    Expired = 4,

    NoticeAcknowledged = 5,
}

public enum GuestConsentSource
{
    GuestPortal = 1,

    HostOnGuestRequest = 2,

    RetentionPolicy = 3,

    BookingCheckout = 4,

    WelcomePage = 5,

    GuestPage = 6,

    HistoricalImport = 7,
}

/// <summary>
/// The channel a marketing consent was given on, as the row that grants it states it in <see cref="GuestConsentRecord.Note"/>
/// (<c>channel=email</c>, <c>channel=whatsapp</c>). The register has no column for it, and a guest who ticks two channels at once leaves two rows
/// that would otherwise be the same: the register is the proof of the act, so it names the channel. Nothing is written for a notice.
/// </summary>
public static class GuestConsentChannelNote
{
    public const string Email = "channel=email";

    public const string WhatsApp = "channel=whatsapp";

    public static string? For(GuestContactChannel channel) => channel switch
    {
        GuestContactChannel.Email => Email,
        GuestContactChannel.WhatsApp => WhatsApp,
        _ => null,
    };
}
