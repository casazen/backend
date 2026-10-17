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
    PropertyDeliveryDateDeclared,

    /// <summary>
    /// The landlord confirmed the review of the draft (LR-12): the data of the first five steps were checked and have no
    /// blocking problem. No payload. The values the confirmation covers are fingerprinted on the lease.
    /// </summary>
    ReviewConfirmed,

    /// <summary>
    /// The confirmation of the review no longer holds (LR-12): the landlord withdrew it, or changed a value the review covered
    /// after confirming. Payload: <c>withdrawn</c> or <c>changed</c>.
    /// </summary>
    ReviewReopened,

    /// <summary>
    /// The landlord downloaded the contract for the signature on paper (LR-12). The lease stays a draft until the signed copy
    /// is uploaded; this only records that the contract was handed out. No payload.
    /// </summary>
    SentForSignature,

    /// <summary>
    /// The landlord took back "downloaded for signature" (LR-12), or the data changed after it, so the copy handed out is no
    /// longer the contract. Payload: <c>withdrawn</c> or <c>review_reopened</c>.
    /// </summary>
    SentRevoked,
}
