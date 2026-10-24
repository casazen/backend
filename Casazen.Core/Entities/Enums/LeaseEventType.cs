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

    /// <summary>
    /// The landlord brought in a contract already signed outside CasaZen (LR-13): the signed copy was uploaded with the day of the
    /// signature and the draft of origin <c>Imported</c> became a Signed lease. Recorded together with <see cref="AllPartiesSigned"/>
    /// (payload <c>imported</c>). No payload.
    /// </summary>
    Imported,

    /// <summary>
    /// The landlord recorded a notice of non-renewal (LR-14): the lease will end with the current period instead of renewing by itself.
    /// Payload: <c>{landlord|tenant}:{yyyy-MM-dd}</c>, who gave it and the day it was given. No personal data.
    /// </summary>
    NoticeRecorded,

    /// <summary>
    /// The landlord took back a notice recorded by mistake (LR-14), so the lease renews by itself again at the end of the period.
    /// Payload: the day the notice had been given (<c>yyyy-MM-dd</c>).
    /// </summary>
    NoticeRemoved,

    /// <summary>
    /// The lease was ended before the end of its period (LR-14): the end of the period moved to the chosen day and the installments
    /// of the rent plan that start after it were cancelled. Payload: the last day of the lease (<c>yyyy-MM-dd</c>).
    /// </summary>
    Terminated,

    /// <summary>
    /// The lease renewed by itself (LR-14): nobody gave notice, so the end of the current period moved forward by one period of the
    /// type (48 months for the libero, 24 for the concordato). One event per period. Payload: the new last day (<c>yyyy-MM-dd</c>).
    /// </summary>
    TermRenewed,

    /// <summary>
    /// A platform admin accessed ConcordatoFacile data for this lease using their admin role (AC-NF04-3), bypassing payment or gift.
    /// Payload: JSON with <c>userId</c> of the admin. No sensitive data.
    /// </summary>
    ConcordatoFacileAdminAccess,

    /// <summary>
    /// The landlord generated a signed link to access the ConcordatoFacile partner portal (ADR-007-R8).
    /// No URL or signature is stored, only the timestamp for audit and brief refresh window (ADR-008-R9).
    /// </summary>
    ConcordatoFacileLinkGenerated,
}
