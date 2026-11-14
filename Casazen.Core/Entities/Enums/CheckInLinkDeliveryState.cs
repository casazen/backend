namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The way the check-in link of a booking goes (or is going) to the guest (<see cref="CheckInLinkDelivery.Channel"/>, E23/F01/US01/T01, NF-122). Stored
/// as an integer: a member is never renumbered.
/// </summary>
public enum CheckInLinkChannel
{
    /// <summary>
    /// An e-mail to the guest of the booking: an address the booking had, one the host wrote since, or one read from the channel manager for the check-in
    /// link only (E23/F01/US01/T02). Also the channel of a booking that waits for the static IP addresses, because that is the way it will go.
    /// </summary>
    Email = 1,

    /// <summary>One message in the reservation thread of the booking on the chat of the portal, written by CasaZen as an automatic message.</summary>
    PortalChat = 2,

    /// <summary>The host takes the link to the guest by hand (it copies it from the «To do»): no automatic way to the guest exists, or none is turned on.</summary>
    Manual = 3,
}

/// <summary>
/// Where the delivery of the check-in link of a booking stands (<see cref="CheckInLinkDelivery.State"/>, E23/F01/US01/T01, T02). The four waiting states say
/// what the link waits for, which is never something the guest does: the permission of the chat, the conversation of the booking, a way to reach the guest,
/// the registration of the static IP addresses. Stored as an integer: a member is never renumbered.
/// </summary>
public enum CheckInLinkDeliveryState
{
    /// <summary>The link was handed over: the message is with the channel manager, or the e-mail is with the queue of the e-mails. Final for that link.</summary>
    Sent = 1,

    /// <summary>The chat of the portal is not in the license of the listing (<c>chat_not_licensed</c>): tried again when the listing reads its chat.</summary>
    WaitingChatPermission = 2,

    /// <summary>The booking has no conversation on the portal yet: tried again after the next read of the threads.</summary>
    WaitingThread = 3,

    /// <summary>No automatic way to the guest is on, or the channel manager holds no address for the guest: the host carries the link.</summary>
    WaitingContact = 4,

    /// <summary>The channel manager refused to give the contact of the guest (401 or 403): the static IP addresses are not registered. Read again once a day at most.</summary>
    WaitingStaticIps = 5,

    /// <summary>The automatic delivery failed with <see cref="CheckInLinkDelivery.LastErrorCode"/>: the host carries the link and is told.</summary>
    Failed = 6,
}
