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

    StipulaDeclared,

    /// <summary>
    /// The landlord declared they sent the communication to the public-security authority for an extra-EU tenant
    /// (art. 7 D.Lgs. 286/1998, LT-07). Payload: the declared date. The only event that ticks the Questura item.
    /// </summary>
    QuesturaCommunicationMarkedDone,

    PropertyDeliveryDateDeclared,

    ReviewConfirmed,

    ReviewReopened,

    SentForSignature,

    SentRevoked,

    Imported,

    /// <summary>
    /// The landlord recorded a notice of non-renewal (LR-14): the lease will end with the current period instead of renewing by itself.
    /// Payload: <c>{landlord|tenant}:{yyyy-MM-dd}</c>, who gave it and the day it was given. No personal data.
    /// </summary>
    NoticeRecorded,

    NoticeRemoved,

    Terminated,

    TermRenewed,

    ConcordatoFacileAdminAccess,

    ConcordatoFacileGiftConsumed,

    ConcordatoFacileLinkGenerated,

    /// <summary>
    /// The landlord accepted the privacy notice for this lease before the guided contract form could be opened (CF-BE). Payload: JSON
    /// with <c>policyVersion</c>, the version of the notice that was current (<c>Legal:Documents:Privacy:Version</c>). No personal data.
    /// The signed link is made only for a lease that has this event for the current version. Appended last: the stored integers of
    /// the other types do not change.
    /// </summary>
    ConcordatoFacilePrivacyAccepted,

    /// <summary>
    /// The landlord recorded the withdrawal (recesso) the tenant sent (LR-OPS-1): the lease was ended early on the day the notice runs out
    /// (a <see cref="Terminated"/> event is written with it). Payload: <c>{reason}:{receivedOn}:{effectiveOn}:{noticeMonths}</c>, the
    /// reason name and the three facts, days as <c>yyyy-MM-dd</c>. No personal data. Appended last: the stored integers of the other types do not change.
    /// </summary>
    WithdrawalRecorded,

    /// <summary>
    /// The landlord marked a letter CasaZen had prepared as sent (LR-OPS-1). Payload: <c>{kind}:{means}:{sentOn}</c>, the names of
    /// <c>LeaseLetterKind</c> and <c>LeaseLetterMeans</c> and the day as <c>yyyy-MM-dd</c>. Nothing else: CasaZen does not send the letter and
    /// does not keep a copy of it. No personal data.
    /// </summary>
    LetterSent,

    /// <summary>
    /// The landlord recorded the renewal of the lease before the end of its period (LR-OPS-1): the end of the period moved forward and the
    /// rent plan was extended. Payload: <c>{Standard|Agreed}:{from}:{to}</c>, the first and last day of the period added (<c>yyyy-MM-dd</c>).
    /// Not the tacit renewal of the hourly job, which writes <see cref="TermRenewed"/>. No personal data.
    /// </summary>
    RenewalRecorded,

    /// <summary>
    /// The landlord recorded that he returned the security deposit (LR-OPS-1c). Payload: <c>{returnedOn}:{deposit}:{interestDue}:{deductions}:{returned}</c>,
    /// the day as <c>yyyy-MM-dd</c> and the four sums in euros with two decimals and a dot. No reason of a deduction and no personal data: CasaZen
    /// moves no money, it records what the landlord did outside it.
    /// </summary>
    DepositReturnRecorded,

    /// <summary>
    /// The tenant opened a maintenance request. Payload: <c>{requestId}:{urgency}</c>. No title and no personal data.
    /// Appended last: the stored integers of the other types do not change.
    /// </summary>
    MaintenanceRequested,

    MaintenanceDecided,

    MaintenancePaymentStarted,

    MaintenancePaid,

    CondominiumExpenseChosen,
}
