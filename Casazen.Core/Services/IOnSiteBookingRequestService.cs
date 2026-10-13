using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The life of a "pay at the property" request after the checkout (decision D5, BK-06, A3-06; see
/// <see cref="OnSiteRequests"/>): the guest confirms the email, the host accepts or declines. Every transition runs
/// under a lock on the booking (the one of the host cancellation, BK-02, plus the row lock skipped by the expiry job), so
/// an accept and a decline sent together, or an answer racing the expiry, give one outcome; the other gets 409.
/// Errors are <see cref="Exceptions.DomainConflictException"/> / <see cref="Exceptions.DomainRuleException"/> with the
/// codes of <see cref="OnSiteRequestErrorCodes"/>. Emails are queued after the change is saved.
/// </summary>
public interface IOnSiteBookingRequestService
{
    /// <summary>
    /// The guest confirms the email with the token of the link: the request goes to the host, who has
    /// <c>DirectBooking:OnSiteApprovalHours</c> to answer. Idempotent: a second click answers the current state.
    /// </summary>
    Task<OnSiteRequestSnapshot> ConfirmGuestEmailAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);

    /// <summary>Requests of <paramref name="scope"/> waiting for the host (email confirmed, deadline not passed), soonest deadline first.</summary>
    Task<IReadOnlyList<Booking>> GetAwaitingHostApprovalAsync(HostScope scope, CancellationToken cancellationToken = default);

    /// <summary>The host accepts: the booking becomes <see cref="BookingStatus.Confirmed"/> and valid (D5).</summary>
    Task<Booking> AcceptAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host declines: the booking is cancelled with <see cref="BookingCancellationReason.OnSiteRequestDeclined"/> and
    /// its dates released. <paramref name="messageToGuest"/> is only written in the email to the guest.
    /// </summary>
    Task<Booking> DeclineAsync(Guid bookingId, string? messageToGuest, CancellationToken cancellationToken = default);
}

/// <summary>A request as the guest's confirmation page shows it.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="Status">Its status (Pending while waiting, Confirmed when accepted, Cancelled when closed).</param>
/// <param name="State">Where a pending request stands; <c>null</c> otherwise.</param>
/// <param name="RequestExpiresAt">Deadline of the pending request (the host's, once the email is confirmed).</param>
public sealed record OnSiteRequestSnapshot(
    Guid BookingId,
    BookingStatus Status,
    OnSiteRequestState? State,
    DateTime? RequestExpiresAt);

/// <summary>Stable error codes of "pay at the property" requests (frontend <c>apiErrors.codes.*</c>).</summary>
public static class OnSiteRequestErrorCodes
{
    /// <summary>422: the stay is longer than <c>DirectBooking:OnSiteMaxNights</c>.</summary>
    public const string TooManyNights = "onsite_request_too_many_nights";

    /// <summary>404: the confirmation link does not match any request (wrong booking or token).</summary>
    public const string LinkInvalid = "onsite_request_link_invalid";

    /// <summary>409: the request is past its deadline (email not confirmed in time, or no answer from the host).</summary>
    public const string Expired = "onsite_request_expired";

    /// <summary>409: the request was already accepted, declined, cancelled or expired.</summary>
    public const string NotPending = "onsite_request_not_pending";

    /// <summary>409: the guest has not confirmed the email yet: the host cannot answer.</summary>
    public const string EmailNotConfirmed = "onsite_request_email_not_confirmed";

    /// <summary>422: the booking is not a "pay at the property" request.</summary>
    public const string NotOnSiteRequest = "booking_not_onsite_request";

    /// <summary>409: the dates now overlap a calendar block imported from an OTA: the request can only be declined.</summary>
    public const string DatesBlocked = "onsite_request_dates_blocked";
}
