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
    CheckInPending = 1,

    CheckOutPending = 2,

    /// <summary>The guests of a stay are still to be sent to Alloggiati Web within its deadline.</summary>
    AlloggiatiDeadline = 3,

    /// <summary>The ISTAT data of a period are still to be sent within their deadline.</summary>
    IstatDeadline = 4,

    ReceiptToRetrieve = 5,

    MessageToApprove = 6,

    PublicationError = 7,

    FiscalDocument = 8,

    WizardResume = 9,

    IntegrationError = 10,

    CredentialsToRenew = 11,

    ReconnectChannelManager = 12,

    CleaningDue = 13,

    BookingSpecialRequest = 14,

    ConfirmChannelMapping = 20,

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

    IdentityVerification = 40,

    TouristTaxDeadline = 50,

    MunicipalityTaxRateMissing = 51,

    MessageToReply = 60,

    OwnerStayToReview = 70,

    OwnerStaySyncFailed = 71,

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

    CleaningDateChanged = 130,

    /// <summary>
    /// A cleaning of a company recharged to the owner at its cost whose VAT nobody stated (E14/F01/US02/T01, ⚖ I13; FIX/BE-CLEANING-VAT): the agency answers «La ditta di pulizie ha
    /// applicato l'IVA su questa fattura?» (Sì / No / Non lo so), because CasaZen never guesses it. The charge is posted all the same, gross. About the cleaning request (entity type
    /// <c>ServiceRequest</c>, leading to the question: <see cref="ActivityAction.StateCleaningVat"/>); the items raised before FIX/BE-CLEANING-VAT are about the stay (entity type
    /// <c>Booking</c>, <see cref="ActivityAction.ReviewBooking"/>) and are closed the same way. Raised at the posting of the month, once for each request (never again after a person
    /// closed it), closed by the answer (or by the system when the company's remembered answer covers the request), and proposed once more at the close of the month when the answer
    /// was «Non lo so». A staff cleaning has no VAT of a company and is never asked. The number 140 keeps clear of 130 to 139, the cleaning at check-out (epic E24), and of the values
    /// the tasks that run in parallel append.
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

    SelfBillingDraftsToReview = 171,
    GuestDataCopyRequested = 190,
    GuestDataErasureRequested = 191,
    GuestInvoiceCorrectionRequested = 192,
    ImportedPropertyReview = 180,
}
