using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// Where the check-in link of a booking stands on its way to the guest, as the host sees it (<c>GET /api/bookings/{id}/checkin/link-delivery</c>, E23/F01/US01/T01, T02,
/// NF-122). Every member but <see cref="Attempts"/> and <see cref="Ended"/> is empty when the booking has no delivery record (no link was ever issued for a stay with no
/// e-mail address): <c>Channel</c> and <c>State</c> are then <c>null</c>.
/// </summary>
/// <remarks>
/// <c>Channel</c> is <c>Email</c>, <c>PortalChat</c> (one message in the reservation thread of the portal) or <c>Manual</c> (the host carries the link). <c>State</c> is
/// <c>Sent</c>, <c>WaitingChatPermission</c> (the chat of the portal is not in the license of the listing yet), <c>WaitingThread</c> (the booking has no conversation yet),
/// <c>WaitingContact</c> (no way to the guest is on, or none has an address), <c>WaitingStaticIps</c> (the contact of the guest cannot be read until the static IP addresses
/// are registered) or <c>Failed</c>, with <c>LastErrorCode</c> the stable code of why (<c>chat_not_licensed</c>, <c>no_conversation</c>, <c>manual_verify</c>,
/// <c>octorate_unavailable</c>, ...). <c>Ended</c> is true once the guest completed the check-in, the booking was cancelled, the link expired or the host issued another
/// link, with <c>EndedReason</c> a code. <c>CheckInLink</c> is the link to copy («Copia link»): <b>only</b> for a caller who can already copy one (<c>booking.write</c> on the
/// property), only while the delivery waits or failed, and only while the link can still be opened; <c>null</c> for everyone else.
/// </remarks>
public sealed class CheckInLinkDeliveryResponse
{
    public CheckInLinkChannel? Channel { get; init; }

    public CheckInLinkDeliveryState? State { get; init; }

    public int Attempts { get; init; }

    public string? LastErrorCode { get; init; }

    public DateTime? SentAt { get; init; }

    public bool Ended { get; init; }

    public string? EndedReason { get; init; }

    public DateTime? ExpiresAt { get; init; }

    public string? CheckInLink { get; init; }

    public static CheckInLinkDeliveryResponse From(CheckInLinkDeliveryView? view) =>
        view is null
            ? new CheckInLinkDeliveryResponse()
            : new CheckInLinkDeliveryResponse
            {
                Channel = view.Channel,
                State = view.State,
                Attempts = view.Attempts,
                LastErrorCode = view.LastErrorCode,
                SentAt = view.SentAt,
                Ended = view.EndedAt is not null,
                EndedReason = view.EndedReason,
                ExpiresAt = view.ExpiresAt,
                CheckInLink = view.Link,
            };
}
