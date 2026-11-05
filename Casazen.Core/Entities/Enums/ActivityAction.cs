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
}
