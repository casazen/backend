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
    /// <summary>The booking: <c>/app/short-rent/bookings/{id}</c>.</summary>
    ReviewBooking = 1,

    /// <summary>The check-in of the booking: <c>/app/short-rent/bookings/{id}/check-in</c>.</summary>
    StartCheckIn = 2,

    /// <summary>The check-out of the booking: <c>/app/short-rent/bookings/{id}/check-out</c>.</summary>
    CompleteCheckOut = 3,

    /// <summary>The identity verification of the guests of the booking.</summary>
    VerifyIdentity = 4,

    /// <summary>The Alloggiati Web page of the compliance area.</summary>
    UploadAlloggiatiProof = 5,

    /// <summary>The ISTAT page of the compliance area.</summary>
    UploadIstatProof = 6,

    /// <summary>The listing to fix: <c>/app/short-rent/listings/{id}</c>.</summary>
    FixListingError = 7,

    /// <summary>The message thread to answer: <c>/app/short-rent/messages/{threadId}</c>.</summary>
    ReplyToMessage = 8,

    /// <summary>The AI draft to approve in its message thread.</summary>
    ApproveAiDraft = 9,

    /// <summary>The wizard to resume.</summary>
    ResumeWizard = 10,

    /// <summary>The channel settings: <c>/app/short-rent/settings/channels</c>.</summary>
    ReconnectChannel = 11,

    /// <summary>The mapping of the listings found on the channel manager (the connect wizard, E02/F01/US02/T05), at the listing to decide.</summary>
    ConfirmChannelMapping = 12,

    /// <summary>The «Check imported listing» wizard of the property created from the channel manager (LST-03).</summary>
    CheckImportedListing = 13,

    // E03/F02/US01/T03 (NF-52, CHK-12): the cockpit actions that feed the To do list under the same name, so the action and the id of an item
    // are the ones of the compliance cockpit (ComplianceCockpitAction). The numbers leave room for the values other tasks append after 13.

    /// <summary>
    /// A stay the channel manager does not send by itself (over 30 nights, a short stay not entrusted, a connection not active): the booking's
    /// Alloggiati tab, where the host sends it by hand. About the booking.
    /// </summary>
    SendAlloggiatiManually = 20,

    /// <summary>The police send of the channel manager of the property is not active or cannot be verified: the sender page of the property. About the property.</summary>
    ActivateOctorateSend = 21,

    /// <summary>The tourist tax rate of the property's comune is not known: where the host enters it. About the property.</summary>
    EnterMunicipalityTaxRate = 22,

    /// <summary>The tourist tax of a closed month is not declared and paid with its receipts archived: the proof archive of the period. About the property and the month.</summary>
    DeclareTouristTax = 23,

    /// <summary>The police credentials kept in CasaZen were refused: the Alloggiati dashboard, where the host sets them again. About the property.</summary>
    RenewPoliceCredentials = 24,
}
