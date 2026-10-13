namespace Casazen.Core.Entities.Enums;

public enum LeaseEventType
{
    Created,
    SigningInitiated,
    PartySignedDocument,
    AllPartiesSigned,
    RegistrationSubmitted,
    RegistrationConfirmed,
    RegistrationFailed,
    ErasureRequested,
    ImuNotificationExported,
    ImuNotificationMarkedSent,
    RegistrationAuthorized,
    RliExported,
    DeadlineReminderSent,

    /// <summary>
    /// The landlord declared the stipula date of a lease signed before CasaZen recorded it (LT-02); the offline
    /// signature itself is <see cref="AllPartiesSigned"/> with payload <c>offline</c>.
    /// </summary>
    StipulaDeclared,

    /// <summary>
    /// The landlord declared they sent the communication to the public-security authority for an extra-EU tenant
    /// (art. 7 D.Lgs. 286/1998, LT-07). Payload: the declared date. The only event that ticks the Questura item.
    /// </summary>
    QuesturaCommunicationMarkedDone,

    /// <summary>The landlord declared (or cleared) the delivery date of the property (LT-07). Payload: the date or <c>cleared</c>.</summary>
    PropertyDeliveryDateDeclared
}
