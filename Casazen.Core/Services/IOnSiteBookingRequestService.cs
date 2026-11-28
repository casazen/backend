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

    Task<IReadOnlyList<Booking>> GetAwaitingHostApprovalAsync(HostScope scope, CancellationToken cancellationToken = default);

    Task<Booking> AcceptAsync(Guid bookingId, CancellationToken cancellationToken = default);

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

public static class OnSiteRequestErrorCodes
{
    public const string TooManyNights = "onsite_request_too_many_nights";

    public const string LinkInvalid = "onsite_request_link_invalid";

    public const string Expired = "onsite_request_expired";

    public const string NotPending = "onsite_request_not_pending";

    public const string EmailNotConfirmed = "onsite_request_email_not_confirmed";

    public const string NotOnSiteRequest = "booking_not_onsite_request";

    public const string DatesBlocked = "onsite_request_dates_blocked";
}
