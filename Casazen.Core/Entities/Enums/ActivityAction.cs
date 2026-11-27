namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the "Do now" button of a "To do" item leads (ADR-026-R6): the frontend maps every member to a route in its
/// <c>ACTION_ROUTE_MAP</c> (<c>src/features/todo/</c>), the single registry of the routes. Stored by name in
/// <see cref="InternalActivity.Action"/> and written by name in the API.
/// </summary>
/// <remarks>
/// The task that creates the first "To do" of a new action appends the member here and its entry in
/// <c>ACTION_ROUTE_MAP</c> in the same change (for example <c>ConfirmChannelMapping</c>, <c>CorrectInvoice</c>,
/// <c>ReviewFiscalDocument</c>). A member is never renamed: the rows keep its name.
/// </remarks>
public enum ActivityAction
{
    ReviewBooking = 1,

    StartCheckIn = 2,

    CompleteCheckOut = 3,

    VerifyIdentity = 4,

    UploadAlloggiatiProof = 5,

    UploadIstatProof = 6,

    FixListingError = 7,

    ReplyToMessage = 8,

    ApproveAiDraft = 9,

    ResumeWizard = 10,

    ReconnectChannel = 11,

    ConfirmChannelMapping = 12,

    CheckImportedListing = 13,

    /// <summary>
    /// A stay the channel manager does not send by itself (over 30 nights, a short stay not entrusted, a connection not active): the booking's
    /// Alloggiati tab, where the host sends it by hand. About the booking.
    /// </summary>
    SendAlloggiatiManually = 20,

    ActivateOctorateSend = 21,

    EnterMunicipalityTaxRate = 22,

    DeclareTouristTax = 23,

    /// <summary>The police credentials kept in CasaZen were refused: the Alloggiati dashboard, where the host sets them again. About the property.</summary>
    RenewPoliceCredentials = 24,

    /// <summary>
    /// A fiscal document that needs the host (E06/F03/US02/T01, NF-84): the documents of the booking (a document Octorate issued and the SDI rejected,
    /// one still to collect, a booking with documents of two issuers). About the document, or the booking for the two issuers. The numbers 30 to 39
    /// are the fiscal documents (epic E06), so the tasks of the waves that run in parallel never pick the same number.
    /// </summary>
    ReviewFiscalDocument = 30,

    /// <summary>
    /// The SDI rejected an electronic invoice of CasaZen (E17/F01/US02/T03, NF-86): the guided correction of the document, <c>/app/short-rent/fiscal/documents?document={id}&amp;step=correct</c>
    /// (the frontend adds the entry to its <c>ACTION_ROUTE_MAP</c>). About the rejected document; the reason code is <c>sdi_rejected</c>. Closes by itself when the correction starts.
    /// </summary>
    CorrectInvoice = 31,

    ReviewSelfBilling = 32,

    ReviewOwnerStay = 40,

    RenewOwnerDocument = 41,

    ReviewOwnerStatement = 50,

    CopyCheckInLink = 110,

    FixScheduledMessage = 120,

    /// <summary>
    /// The question of the VAT of a cleaning recharged to the owner (FIX/BE-CLEANING-VAT): «La ditta di pulizie ha applicato l'IVA su questa fattura?», answered with
    /// <c>POST api/service-requests/{id}/cleaning-vat</c> (the question itself is <c>GET</c> of the same route). About the cleaning request (entity type <c>ServiceRequest</c>,
    /// the id is the request's) of <see cref="ActivityType.CleaningVatToState"/>; the items raised before it are about the stay and keep <see cref="ReviewBooking"/>. The number
    /// is the one of its type (140, the cleaning recharge of E14), clear of the values the tasks that run in parallel append after 50 and 120.
    /// </summary>
    StateCleaningVat = 140,
    ReviewImportedProperty = 150,
}
