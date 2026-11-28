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

    Task<GuestAccountBookingDetail> GetAsync(string accountEmail, Guid bookingId, CancellationToken cancellationToken = default);

    Task<GuestAccountSessionOpened> OpenSessionAsync(string accountEmail, Guid bookingId, CancellationToken cancellationToken = default);
}

public enum GuestAccountBookingScope
{
    Upcoming,

    Past,
}

public static class GuestAccountBookings
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public static NotFoundException NotFound() =>
        new("No booking of the account matches the id")
        {
            Code = GuestBookingLookupErrorCodes.NotFound,
            MessageKey = "GuestAccountBookingNotFound",
        };
}

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

public sealed record GuestAccountBookingDetail(Guid Id, string OrgSlug, GuestBookingView View);

public sealed record GuestAccountSessionOpened(string AccessToken, DateTime ExpiresAt, string BookingCode, string OrgSlug);

public static class GuestAccountErrorCodes
{
    public const string EmailNotVerified = "guest_email_not_verified";

    public const string IsProfessional = "guest_account_is_professional";
}
