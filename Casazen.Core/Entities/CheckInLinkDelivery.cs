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
    public const string BookingIndexName = "UIX_CheckInLinkDeliveries_BookingId";

    public const int ErrorCodeMaxLength = 64;

    public const int EndedReasonMaxLength = 40;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public Guid? SessionId { get; set; }

    public CheckInLinkChannel Channel { get; set; }

    public CheckInLinkDeliveryState State { get; set; }

    /// <summary>
    /// The attempts of the automatic delivery for the link that failed for a reason worth a retry (a limit, an outage), the first one included: the retries stop at
    /// <c>CheckInLinkDeliveryRules.MaxAttempts</c>. Waiting for the permission of the chat, the conversation, a way to the guest or the IP addresses is not an attempt that failed, and
    /// the read of the contact is not one either.
    /// </summary>
    public int Attempts { get; set; }

    [MaxLength(ErrorCodeMaxLength)]
    public string? LastErrorCode { get; set; }

    public DateTime? SentAt { get; set; }

    public Guid? OtaMessageId { get; set; }

    public string? LinkToken { get; set; }

    /// <summary>When the last automatic attempt was made (UTC): the retries keep their distance from it.</summary>
    public DateTime? LastAttemptAt { get; set; }

    public DateTime? ContactReadAt { get; set; }

    public bool ContactRefused { get; set; }

    public DateTime? EndedAt { get; set; }

    [MaxLength(EndedReasonMaxLength)]
    public string? EndedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
