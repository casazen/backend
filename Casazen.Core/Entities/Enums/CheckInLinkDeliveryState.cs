namespace Casazen.Core.Entities.Enums;

public enum CheckInLinkChannel
{
    /// <summary>
    /// An e-mail to the guest of the booking: an address the booking had, one the host wrote since, or one read from the channel manager for the check-in
    /// link only (E23/F01/US01/T02). Also the channel of a booking that waits for the static IP addresses, because that is the way it will go.
    /// </summary>
    Email = 1,

    PortalChat = 2,

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
    Sent = 1,

    WaitingChatPermission = 2,

    WaitingThread = 3,

    WaitingContact = 4,

    WaitingStaticIps = 5,

    Failed = 6,

    Sending = 7,
}
