using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// "Le mie prenotazioni" of the guest who has a CasaZen account (the mobile app, <c>api/guest/*</c>): the direct bookings, of any
/// booking site, whose guest has the email address of the account, listed, read and opened as a guest-area session. The account's
/// address must be <b>verified</b> (the caller checks it: <see cref="GuestAccountErrorCodes.EmailNotVerified"/>), because it is the
/// only thing that ties a booking to an account: the booking was made on a public site, with no account, and its guest typed that
/// address.
/// </summary>
/// <remarks>
/// <para><b>Whose bookings.</b> <c>Source = Direct</c> (the public checkout), an active org, and a guest whose email equals the account's
/// (trimmed, case-insensitive), in any org. The tenant filter is ignored on purpose, together with the soft-delete one of the property
/// (a stay at a house the host has since deleted is still the guest's); the predicates above are the wall. Nothing else of the org is
/// read, and an inactive org's bookings never appear.</para>
/// <para><b>What leaves.</b> The projection of "Le mie prenotazioni" (<see cref="GuestBookingView"/>) and a short list item
/// (<see cref="GuestAccountBookingSummary"/>): the booking's state, stay, amounts, the property's public data and the host's name. Never
/// the host's notes (the reason of a cancellation, the note of an agreed price), the channel data of an OTA stay, a Stripe identifier,
/// the guest's documents or anything of another guest or of the people of the stay.</para>
/// <para><b>Not found.</b> A booking that is not the account's answers the same <see cref="Exceptions.NotFoundException"/>
/// (<see cref="GuestBookingLookupErrorCodes.NotFound"/>) as one that does not exist.</para>
/// </remarks>
public interface IGuestAccountBookingService
{
    /// <summary>
    /// One page of the account's bookings: <see cref="GuestAccountBookingScope.Upcoming"/> are the stays whose check-out is today (Europe/Rome)
    /// or later, by check-in ascending; <see cref="GuestAccountBookingScope.Past"/> the ones that ended, by check-in descending. The states
    /// that are dead ends for the guest (<see cref="GuestBookingStatus.Expired"/>, <see cref="GuestBookingStatus.DatesUnavailable"/>) are not listed.
    /// </summary>
    /// <param name="accountEmail">The verified email of the account.</param>
    /// <param name="page">1-based; below 1 counts as 1.</param>
    /// <param name="pageSize">Clamped to 1 and <see cref="GuestAccountBookings.MaxPageSize"/>.</param>
    Task<GuestAccountBookingPage> ListAsync(
        string accountEmail,
        GuestAccountBookingScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>One booking of the account as "Le mie prenotazioni" shows it, with its id and the slug of its site.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
    Task<GuestAccountBookingDetail> GetAsync(string accountEmail, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the guest-area session of the site of the booking for the account's email: the same stateless token as the one a booking
    /// code and an email open (<see cref="IGuestSessionService"/>, <c>gs1.</c>, the same length), so the app reuses the endpoints of the
    /// guest area (<c>api/public/guest/bookings/{code}/…</c>: the cancellation, the messages) without asking the guest for the code.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
    Task<GuestAccountSessionOpened> OpenSessionAsync(string accountEmail, Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>Which stays <see cref="IGuestAccountBookingService.ListAsync"/> lists. Serialized by name in the query: <c>upcoming</c>, <c>past</c>.</summary>
public enum GuestAccountBookingScope
{
    /// <summary>The check-out is today (Europe/Rome) or later.</summary>
    Upcoming,

    /// <summary>The check-out is before today (Europe/Rome).</summary>
    Past,
}

/// <summary>Limits of the list of the account's bookings, and the one answer for a booking that is not the account's.</summary>
public static class GuestAccountBookings
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    /// <summary>
    /// 404 <see cref="GuestBookingLookupErrorCodes.NotFound"/> with a message that speaks of the account's bookings (the one of the
    /// anonymous lookup speaks of a code and an email): the same for an id that does not exist, one that is not an id and one of another person.
    /// </summary>
    public static NotFoundException NotFound() =>
        new("No booking of the account matches the id")
        {
            Code = GuestBookingLookupErrorCodes.NotFound,
            MessageKey = "GuestAccountBookingNotFound",
        };
}

/// <summary>A page of <see cref="IGuestAccountBookingService.ListAsync"/>; <paramref name="Total"/> counts the whole scope, not the page.</summary>
public sealed record GuestAccountBookingPage(IReadOnlyList<GuestAccountBookingSummary> Items, int Total, int Page, int PageSize);

/// <summary>
/// One booking in the list of the account, in the currency of its property (<paramref name="Currency"/>). The code is formatted
/// (<c>XXXXX-XXXXX</c>), the dates are Europe/Rome calendar days and <paramref name="Nights"/> is their difference. No personal data of
/// anyone: the guest is the person looking.
/// </summary>
/// <param name="Id">The booking id: the account's key to read it, open its session or find it again.</param>
/// <param name="OrgSlug">The slug of the booking site.</param>
/// <param name="City">The property's city; empty when it has none.</param>
/// <param name="PhotoUrl">The first photo of the property's public gallery (https), or null.</param>
/// <param name="PaidAmount">Collected by Stripe (refunds included), as in <see cref="GuestBookingView.PaidAmount"/>.</param>
/// <param name="HostName">The name of the host's site (<c>Org.DisplayName</c>), or null.</param>
public sealed record GuestAccountBookingSummary(
    Guid Id,
    string OrgSlug,
    string BookingCode,
    GuestBookingStatus Status,
    Guid PropertyId,
    string? PropertySlug,
    string PropertyName,
    string City,
    string? PhotoUrl,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int Nights,
    int Adults,
    int Children,
    string Currency,
    decimal TotalPrice,
    decimal PaidAmount,
    decimal RefundedAmount,
    PaymentOption PaymentOption,
    string? HostName);

/// <summary>A booking of the account as "Le mie prenotazioni" shows it, plus the id and the site slug the app needs.</summary>
public sealed record GuestAccountBookingDetail(Guid Id, string OrgSlug, GuestBookingView View);

/// <summary>
/// A guest-area session opened for the account: the token to send as <c>Authorization: Bearer</c> to <c>api/public/guest/*</c>, until when
/// it works, the booking code (<c>XXXXX-XXXXX</c>) those endpoints take in their path, and the slug of the site.
/// </summary>
public sealed record GuestAccountSessionOpened(string AccessToken, DateTime ExpiresAt, string BookingCode, string OrgSlug);

/// <summary>Stable error codes of the guest account (frontend <c>apiErrors.codes.*</c>); the not-found one is <see cref="GuestBookingLookupErrorCodes.NotFound"/>.</summary>
public static class GuestAccountErrorCodes
{
    /// <summary>403: the account's email address is not verified, so no booking can be tied to it.</summary>
    public const string EmailNotVerified = "guest_email_not_verified";

    /// <summary>409: the account also works as a host, a supplier or a platform administrator: it is not removed from the app.</summary>
    public const string IsProfessional = "guest_account_is_professional";
}
