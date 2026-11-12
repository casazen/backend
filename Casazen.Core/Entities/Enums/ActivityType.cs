namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What an item of the "To do" list is (ADR-026, NF-07). Stored by name in the string column
/// <see cref="InternalActivity.ActivityType"/>, next to the plain-text values of the staff work ("Cleaning", "Maintenance",
/// "CheckIn"), which are not members of this enum: a row of the staff work has no <see cref="InternalActivity.Action"/>, a
/// row of the To do always has one.
/// </summary>
/// <remarks>
/// The name is what the rows keep, so a member is never renamed; a later task appends the member it needs. The
/// numbers are never stored.
/// </remarks>
public enum ActivityType
{
    /// <summary>A stay whose check-in is still to be done.</summary>
    CheckInPending = 1,

    /// <summary>A stay whose check-out is still to be done.</summary>
    CheckOutPending = 2,

    /// <summary>The guests of a stay are still to be sent to Alloggiati Web within its deadline.</summary>
    AlloggiatiDeadline = 3,

    /// <summary>The ISTAT data of a period are still to be sent within their deadline.</summary>
    IstatDeadline = 4,

    /// <summary>A receipt (of a transmission) is still to be retrieved.</summary>
    ReceiptToRetrieve = 5,

    /// <summary>A message waits for the host's approval before it is sent.</summary>
    MessageToApprove = 6,

    /// <summary>A listing could not be published on a channel.</summary>
    PublicationError = 7,

    /// <summary>A fiscal document needs the host.</summary>
    FiscalDocument = 8,

    /// <summary>A wizard was left half way and can be resumed.</summary>
    WizardResume = 9,

    /// <summary>An integration failed and needs the host.</summary>
    IntegrationError = 10,

    /// <summary>The credentials of a service are about to expire or expired.</summary>
    CredentialsToRenew = 11,

    /// <summary>The connection to the channel manager was lost.</summary>
    ReconnectChannelManager = 12,

    /// <summary>
    /// A stay ended and nobody asked for a cleaning: the property has to be declared ready or a cleaning requested before the
    /// next arrival (E08/F06/US01/T02). About the booking that ended; closes when the property is declared ready.
    /// </summary>
    CleaningDue = 13,

    /// <summary>
    /// A booking came with a special request of the guest, to read before the arrival (E08/F06/US01/T02). About the booking; closes
    /// at the check-in or when the booking is cancelled.
    /// </summary>
    BookingSpecialRequest = 14,

    /// <summary>
    /// A listing found on the channel manager matches several CasaZen properties, or none clearly: a person confirms or rejects the
    /// mapping (E02/F01/US02/T03); closed when the mapping is confirmed or rejected. Organization-level: about the mapping, not a property.
    /// </summary>
    ConfirmChannelMapping = 20,

    /// <summary>
    /// A property was created from a listing of the channel manager and is inactive until a person checks what was imported (LST-03,
    /// E02/F01/US02/T03).
    /// </summary>
    CheckImportedListing = 21,

    /// <summary>
    /// A booking could not be written to the channel manager because the dates are taken there (E08/F01/US01/T03; E08/F01/US02/T02 raises it
    /// too). About the booking, urgent: the host checks the booking and the calendars of the channels before a second guest arrives.
    /// </summary>
    BookingOverlap = 22,

    /// <summary>
    /// A change the host made to a booking could not be written to the channel manager because the reservation was changed there meanwhile
    /// (E08/F02/US01/T02, NF-75): Octorate answered 409. About the booking, with both versions readable in the booking answer: the host chooses
    /// to keep CasaZen's (the retry with <c>overwrite=true</c>) or to take Octorate's (editing the booking to match). Closes when the mirror is
    /// <c>Synced</c> again.
    /// </summary>
    BookingSyncConflict = 23,

    /// <summary>
    /// The content of a listing was changed on the channel manager after CasaZen's last push (E02/F03/US01/T07, NF-27): the host chooses between
    /// keeping the CasaZen version (republished over it) and taking the descriptions Octorate holds. About the property on one channel, so the item
    /// is one for each property and channel; it closes when the choice is made. The number leaves room for the values other tasks append after 22.
    /// </summary>
    ListingChangedOutside = 30,

    /// <summary>
    /// The identity of the guest of a stay waits for an operator (E03/F04/US02/T03, CHK-11): the agent asked for a review, the attempts are used,
    /// the guest refused the biometric consent or asked for a person, or the check could not run. About the booking, urgent, leading to its
    /// verification (<see cref="ActivityAction.VerifyIdentity"/>); closed by the operator's decision.
    /// </summary>
    IdentityVerification = 40,

    /// <summary>
    /// The tourist tax of a closed month is not declared and paid with its receipts archived (E03/F02/US01/T03, NF-52). About the property and the
    /// month (the entity type names the month, <c>PropertyPeriod:yyyy-MM</c>); closes when both receipts are archived. The numbers 50 and 51 keep
    /// clear of the values the tasks that run in parallel append after 30.
    /// </summary>
    TouristTaxDeadline = 50,

    /// <summary>
    /// The tourist tax rate of a property's comune is not known (E03/F02/US01/T03, NF-52): never taken as zero, the host enters it. About the
    /// property; closes when a rate is known.
    /// </summary>
    MunicipalityTaxRateMissing = 51,

    /// <summary>
    /// A message of a guest the assistant passed to a person (E05/F05/US02/T01, NF-67): its situation is set to escalate, it touches a forbidden topic, or it is urgent. About the
    /// conversation (<c>OtaMessageThread</c> and its id, or <c>Booking</c> and the id of a direct booking), with the urgency and a deadline of <c>Assistant:Escalation</c>, leading to
    /// the answer (<see cref="ActivityAction.ReplyToMessage"/>); closed when a person answers the conversation. The number keeps clear of the values the tasks that run in parallel append.
    /// </summary>
    MessageToReply = 60,
}
