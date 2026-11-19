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

    /// <summary>
    /// An owner asked for a stay in its property and a person of the agency decides (E07/F07/US01/T01, NF-111): the rule asks for an approval, or the
    /// automatic approval found the dates taken. About the owner stay (entity type <c>OwnerStay</c>), on its property; closes when the stay is approved,
    /// rejected or withdrawn. The numbers from 70 keep clear of the values the tasks that run in parallel append after 51 and 60.
    /// </summary>
    OwnerStayToReview = 70,

    /// <summary>
    /// The channel manager refused the block of an owner stay, or its release (E07/F07/US01/T01): the stay is not confirmed until the block is sent
    /// again. About the owner stay, on its property, urgent; closes when the block (or its release) is acknowledged, the stay is cancelled or it is over.
    /// </summary>
    OwnerStaySyncFailed = 71,

    /// <summary>
    /// A document the agency shares with an owner lapses within 30 days, or has lapsed (E07/F07/US01/T02, AC-NF111-3: the expiry alert): an insurance or a
    /// mandate to renew. About the version in force (entity type <c>OwnerDocumentVersion</c>), on the property of the document or of the org; raised once
    /// per version, closed when a new version is in force beyond the window or the document is withdrawn.
    /// </summary>
    OwnerDocumentExpiring = 72,

    /// <summary>
    /// The owner could not be told by e-mail that a statement was published (E13/F01/US02/T02): the e-mail could not be prepared, or the queue refused it, so the agency
    /// tells the owner by hand. About the statement version (entity type <c>OwnerStatementVersion</c>), organization-level; the statement is published whatever happens.
    /// Raised once for each version, and never for a portal that is off or an owner nobody reaches in it. The number leaves room for the values the owner portal appends after 72.
    /// </summary>
    OwnerStatementNoticeFailed = 80,

    /// <summary>
    /// The guest of a stay is owed money back on the tourist tax (E25/F01/US01/T01, NF-119): the confirmed tax is lower than what the price of the booking
    /// included or than what was paid online, the stay was cancelled after the arrival with the tax paid online, or the automatic refund of a cancelled
    /// stay failed. Never refunded by CasaZen by itself in these cases: the host gives the difference back. About the booking, leading to it
    /// (<see cref="ActivityAction.ReviewBooking"/>); raised once per booking. The numbers from 90 are the tourist tax collection (epic E25), apart from
    /// the values the tasks that run in parallel append.
    /// </summary>
    TouristTaxRefundDue = 90,

    /// <summary>
    /// A task a routine of the host raised (E01/F04/US01/T02, NF-21): the action <c>CreateActivity</c> of a routine, about the booking of the event (entity
    /// type <c>Booking</c>), leading to the booking (<see cref="ActivityAction.ReviewBooking"/>), with the words the host wrote in the routine as its
    /// reason. One open task for each booking, whatever the number of routines and events that ask for it; it closes when the host closes it. The number
    /// 100 keeps clear of the values the tasks that run in parallel append after 80 and of the tourist tax collection of E25, which takes the numbers from 90.
    /// </summary>
    RoutineTask = 100,

    /// <summary>
    /// The check-in link of a stay from a portal has not reached the guest and the host may have to carry it (E23/F01/US01/T01, NF-122): the chat of the portal is not
    /// available yet, the booking has no conversation, no way to the guest is on, the contact of the guest cannot be read yet, or the automatic delivery failed. About the
    /// booking (entity type <c>Booking</c>), leading to the link to copy (<see cref="ActivityAction.CopyCheckInLink"/>); raised once per booking, only while one of the flags
    /// <c>PortalChatCheckInLink</c> and <c>GuestContactInfo</c> is on; closed by the system when the link is sent, when the guest completes the check-in, when the booking is
    /// cancelled or when the host issues a link itself. The numbers from 110 are the check-in link and the chat of the portal (epic E23), apart from the values the tasks that
    /// run in parallel append.
    /// </summary>
    CheckInLinkToSend = 110,

    /// <summary>
    /// A company is the tenant of a stay in the Italian property of a private owner (E18/F01/US03/T01, ⚖ I7, NF-117): the tax agency circular and the Court of Cassation
    /// read the short-let regime differently, so a person decides whether the stay stays a short let (withholding) or not, and the decision is recorded
    /// (<c>ShortLetTaxDecision</c>). About the booking (entity type <c>Booking</c>), on its property, leading to it (<see cref="ActivityAction.ReviewBooking"/>), with the
    /// reason <c>company_tenant_decision</c> in words; closed when a decision is recorded, when the booking is cancelled or marked a no-show, or when the stay is no company tenant's any more (closed by the system). Raised only while <c>Features:TaxReportingExports</c>
    /// is on. The number 120 keeps clear of the values the tasks that run in parallel append after 100.
    /// </summary>
    CompanyTenantDecision = 120,

    /// <summary>
    /// The dates of a stay changed after the supplier took its automatic cleaning (E24/F01/US01/T02, NF-120): the cleaning stays on the old day and the host
    /// decides whether to cancel it, ask for another or keep it. About the booking, leading to it (<see cref="ActivityAction.ReviewBooking"/>); raised once for
    /// each stay while it is open, and closed by the system when the stay is cancelled or the property is declared ready. The numbers from 130 are the
    /// cleaning at check-out (epic E24): they keep clear of the values the tasks that run in parallel append between 100 and 129.
    /// </summary>
    CleaningDateChanged = 130,

    /// <summary>
    /// A cleaning recharged to the owner at its cost whose VAT nature the cost source does not say (E14/F01/US02/T01, ⚖ I13): the agency states it, because CasaZen never guesses it. The
    /// charge is posted all the same; the item asks the agency to state the VAT (and the amount, when the cleaning has none: the statement is held back until it has). About the booking
    /// of the cleaning when it has one (entity type <c>Booking</c>, leading to it: <see cref="ActivityAction.ReviewBooking"/>), else about the request or the staff activity itself;
    /// raised at the posting of the month, once for each of them (never again after a person closed it), and closed by the system when the cleaning's VAT is known. The number 140
    /// keeps clear of 130 to 139, the cleaning at check-out (epic E24), and of the values the tasks that run in parallel append.
    /// </summary>
    CleaningVatToState = 140,

    /// <summary>
    /// A message of a routine could not go out and the host has to look at it (E05/F07/US01/T03, NF-71): it is blocked for a piece of data it has no value for (a variable, the access
    /// code of a guest who is not verified) or its last send failed for good. About the booking (entity type <c>Booking</c>), leading to the booking where the message is edited or sent
    /// by hand (<see cref="ActivityAction.FixScheduledMessage"/>), with the code of the reason (<c>variable_missing:access_code</c>, <c>id_not_verified</c>), never the words of the
    /// message. One open item for each booking, shared by its blocked messages; closed by the system when the last one is sent, skipped, expired, or the booking is cancelled. The number 150
    /// keeps clear of the values the tasks that run in parallel append between 140 and 149.
    /// </summary>
    ScheduledMessageIssue = 150,

    /// <summary>
    /// The SDI rejected a self-billed invoice (TD17, FIX/BE-TD17-ALERTS): for the tax office it was not issued, and a TD17 has no correction in CasaZen (<c>self_billing_not_correctable</c>), so
    /// this item is the agency's alert, not the «Correggi la fattura» of an invoice to a guest. About the rejected document (entity type <c>FiscalDocument</c>), organization-level (a TD17 spans the
    /// properties of its holder), leading to the self-billed invoices (<see cref="ActivityAction.ReviewSelfBilling"/>, which carries the document id); urgent. Raised once for each document (the
    /// event of the rejection is delivered more than once), listed only to who holds <c>agency.invoice.manage</c>, and closed by the system when the document is corrected, credited or accepted by
    /// the SDI. The numbers from 170 are the self-billed invoices (epic E17): they keep clear of 150 and 160, which the tasks that run in parallel take after 140.
    /// </summary>
    SelfBillingRejected = 170,

    /// <summary>
    /// The drafts of the self-billed invoices (TD17) of a month wait for the agency to check and issue them (FIX/BE-TD17-ALERTS): raised by the monthly run
    /// (<c>SelfBilling:MonthlyDrafts:Enabled</c>) once for each organization and month, about the month (entity type <c>SelfBillingPeriod:yyyy-MM</c>, the id is the organization's),
    /// organization-level, leading to the self-billed invoices (<see cref="ActivityAction.ReviewSelfBilling"/>); its deadline is the day the TD17 is issued by (<c>SelfBilling:IssueByDay</c>).
    /// Listed only to who holds <c>agency.invoice.manage</c>, and closed by the system when no draft of the month is left open (every one issued or discarded).
    /// </summary>
    SelfBillingDraftsToReview = 171,
}
