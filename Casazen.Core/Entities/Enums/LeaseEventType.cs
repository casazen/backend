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
    StipulaDeclared
}
