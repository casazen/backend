namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The four channels with an integrated chat that the channel manager exposes (E05/F01/US01/T01, source S7): the only conversations the
/// hub of messages copies from it. Stored as an integer; a channel without chat has no value here and its threads are not imported.
/// </summary>
public enum OtaChannel
{
    Airbnb = 1,
    Booking = 2,
    Vrbo = 3,
    Expedia = 4,
}

/// <summary>What an OTA conversation is about. Stored as an integer.</summary>
public enum OtaThreadKind
{
    /// <summary>A conversation about a reservation (the thread carries the reservation of the channel manager).</summary>
    Reservation = 1,

    /// <summary>A conversation with no booking yet, an inquiry (AC-NF64-2); it becomes a <see cref="Reservation"/> when the booking exists.</summary>
    Request = 2,
}

/// <summary>Who a message of an OTA conversation is for. Stored as an integer.</summary>
public enum OtaMessageDirection
{
    /// <summary>Written by the guest.</summary>
    Inbound = 1,

    /// <summary>Written by the host side: a person at the channel manager or in CasaZen, or the assistant.</summary>
    Outbound = 2,
}

/// <summary>
/// Where an outbound message of the channel manager is on its way to the guest, as it spells it (<c>CREATED</c>, <c>WAIT_AUTH</c>, <c>SENT</c>,
/// <c>ERROR</c>, <c>MANUAL_VERIFY</c>). Stored as an integer.
/// </summary>
public enum OtaMessageDeliveryState
{
    /// <summary><c>CREATED</c>: written, not sent yet.</summary>
    Created = 1,

    /// <summary><c>WAIT_AUTH</c>: waiting for the authorization of the channel.</summary>
    WaitingAuthorization = 2,

    /// <summary><c>SENT</c>: the channel accepted it.</summary>
    Sent = 3,

    /// <summary><c>ERROR</c>: the channel refused it.</summary>
    Error = 4,

    /// <summary><c>MANUAL_VERIFY</c>: the outcome is not known; a person checks on the channel before it is sent again (OCT-10).</summary>
    ManualVerify = 5,
}

/// <summary>Who wrote a message of an OTA conversation, as CasaZen tells it. Stored as an integer.</summary>
public enum MessageOrigin
{
    /// <summary>The guest.</summary>
    Guest = 1,

    /// <summary>A person of the host side, in CasaZen or outside it.</summary>
    Person = 2,

    /// <summary>The assistant of CasaZen (E05/F05): the rule that produced the message is in <c>OtaMessage.RuleCode</c>.</summary>
    Assistant = 3,
}
