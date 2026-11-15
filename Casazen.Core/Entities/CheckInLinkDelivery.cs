using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Where the check-in link of one booking stands on its way to the guest (E23/F01/US01/T01, T02, NF-122): one row per booking, made by the daily job that
/// issues the link of a booking that has no e-mail address to send it to (a stay from a portal) and kept up to date by the delivery, whatever the way it
/// goes: an e-mail, one message in the reservation thread of the portal chat, or the host by hand. The row is what the host sees on the booking and in its
/// «To do» («Copia link»), so it says why the link has not reached the guest instead of failing in silence.
/// </summary>
/// <remarks>
/// <para><b>One per booking.</b> A new link of the booking (the daily job after the previous one expired) starts the row again for that link; the unique index
/// keeps it to one. A row ends (<see cref="EndedAt"/>) when the guest completes the check-in, when the booking is no longer one that takes a check-in, when the
/// link expires, or when the host issues another link itself.</para>
/// <para><b>The link.</b> The system stores only the hash of a link (<see cref="GuestCheckInSession.TokenHash"/>); the raw token is kept here, <b>encrypted at
/// rest</b> (<c>EncryptedColumns</c>), only while the host may need it (a waiting or failed delivery of a booking whose new flags are on) and cleared as soon as
/// the link is sent or the row ends: a retry sends the same link the host can copy, never a second one. It is never logged and never returned to a caller
/// that cannot already copy a link.</para>
/// <para><b>No personal data</b> otherwise: the row holds ids, a state, counters and a stable error code.</para>
/// </remarks>
[Table("CheckInLinkDeliveries")]
public class CheckInLinkDelivery : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="BookingId"/>.</summary>
    public const string BookingIndexName = "UIX_CheckInLinkDeliveries_BookingId";

    public const int ErrorCodeMaxLength = 64;

    public const int EndedReasonMaxLength = 40;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org of the booking. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The booking the link is for. FK, CASCADE. Unique.</summary>
    public Guid BookingId { get; set; }

    /// <summary>The check-in session (the link) being delivered. FK, SET NULL.</summary>
    public Guid? SessionId { get; set; }

    public CheckInLinkChannel Channel { get; set; }

    public CheckInLinkDeliveryState State { get; set; }

    /// <summary>
    /// The attempts of the automatic delivery for the link that failed for a reason worth a retry (a limit, an outage), the first one included: the retries stop at
    /// <c>CheckInLinkDeliveryRules.MaxAttempts</c>. Waiting for the permission of the chat, the conversation, a way to the guest or the IP addresses is not an attempt that failed, and
    /// the read of the contact is not one either.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>The stable code of the last thing that went wrong (<c>chat_not_licensed</c>, <c>octorate_unavailable</c>, ...); <c>null</c> when nothing did.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? LastErrorCode { get; set; }

    /// <summary>When the link was handed over (UTC); <c>null</c> until then.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>The message CasaZen wrote in the thread of the portal, when the link went by chat. FK, SET NULL.</summary>
    public Guid? OtaMessageId { get; set; }

    /// <summary>
    /// The raw token of the link, <b>encrypted at rest</b> (purpose <c>Casazen.CheckInLink.Token</c>; the column holds the payload only, so it has no length).
    /// <c>null</c> when nobody may need it any more. See the remarks of the class.
    /// </summary>
    public string? LinkToken { get; set; }

    /// <summary>When the last automatic attempt was made (UTC): the retries keep their distance from it.</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// When the contact of the guest was last asked of the channel manager for this booking (UTC), always for the check-in link and nothing else (T02): once for the
    /// booking, and again only after a refusal, at most once a day. <c>null</c> while it was never asked.
    /// </summary>
    public DateTime? ContactReadAt { get; set; }

    /// <summary>The last read of the contact was refused (401 or 403, the static IP addresses): the only answer that lets the read be repeated.</summary>
    public bool ContactRefused { get; set; }

    /// <summary>When the row ended (UTC); <c>null</c> while the link is still being delivered or waits.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Why it ended: a code of <c>CheckInLinkEndReasons</c>.</summary>
    [MaxLength(EndedReasonMaxLength)]
    public string? EndedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
