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
/// the registration of the static IP addresses. <see cref="Sending"/> is the message of the chat that the channel manager has not sent yet. Stored as an integer: a
/// member is never renumbered.
/// </summary>
public enum CheckInLinkDeliveryState
{
    /// <summary>
    /// The link was handed over: the message is <b>sent</b> (the channel manager says so), or the e-mail is with the queue of the e-mails. Final for that link. A message that is only
    /// created, or waits for the authorization of the channel, is <see cref="Sending"/>, never this.
    /// </summary>
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

    /// <summary>
    /// The message of the chat is with the channel manager (<c>CREATED</c>, or <c>WAIT_AUTH</c> while the channel authorizes it) and has not been sent yet: the link is not delivered, the
    /// row stays open with its token, and the hourly pass reads the message again: <see cref="Sent"/> when the channel manager sends it, <see cref="Failed"/> with
    /// <c>octorate_message_error</c> when it ends in error. The host has nothing to do while it is here, and a «To do» it already has stays open.
    /// </summary>
    Sending = 7,
}
