namespace Casazen.Core.Entities.Enums;

public enum OtaChannel
{
    Airbnb = 1,
    Booking = 2,
    Vrbo = 3,
    Expedia = 4,
}

public enum OtaThreadKind
{
    Reservation = 1,

    Request = 2,
}

public enum OtaMessageDirection
{
    Inbound = 1,

    Outbound = 2,
}

public enum OtaMessageDeliveryState
{
    Created = 1,

    WaitingAuthorization = 2,

    Sent = 3,

    Error = 4,

    ManualVerify = 5,
}

public enum MessageOrigin
{
    Guest = 1,

    Person = 2,

    Assistant = 3,

    Automation = 4,
}
