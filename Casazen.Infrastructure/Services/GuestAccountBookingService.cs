using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestAccountBookingService"/>
/// <remarks>
/// <para><b>The wall is the query, not the tenant filter.</b> The request of a signed-in guest has the tenant of its own host org or none
/// at all, so the tenant filter is ignored (and the soft-delete one of the property, which would drop the stays at a house the host has
/// deleted) and every read says whose bookings it wants: <c>Source = Direct</c>, an active org and a guest whose email is the account's.
/// A booking id of another person is found by none of them, so it answers the same 404 as an id that does not exist.</para>
/// <para>The list loads the account's bookings of the scope and works their state out in memory (the state of a hold depends on the clock
/// and on its payments, <see cref="GuestBookings.StatusOf"/>), which also keeps the page and the total consistent with the states that are
/// not listed. A person has a few bookings; the comparison of the email has no index (<c>Guests.Email</c> is indexed as typed), the same
/// as the lookup with a booking code.</para>
/// </remarks>
public sealed class GuestAccountBookingService(
    AppDbContext db,
    GuestBookingLookupService lookup,
    GuestSessionTokens tokens,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IGuestAccountBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestAccountBookingPage> ListAsync(
        string accountEmail,
        GuestAccountBookingScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(accountEmail);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, GuestAccountBookings.MaxPageSize);

        var today = _clock.TodayInRomeAsDateOnly();
        var todayUtc = _clock.TodayInRome();
        var upcomingFrom = todayUtc.AddDays(-1);

        var candidates = OwnBookings(email)
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Payments)
            .AsSplitQuery();
        candidates = scope == GuestAccountBookingScope.Upcoming
            ? candidates.Where(b => b.CheckOutDate >= upcomingFrom)
            : candidates.Where(b => b.CheckOutDate < todayUtc);
        var bookings = await candidates.ToListAsync(cancellationToken);

        var cutoff = CheckoutHolds.CutoffAt(_clock.GetUtcNow().UtcDateTime, CheckoutHolds.GetTtlMinutes(configuration));
        var listed = bookings
            .Select(b => new Row(
                b,
                GuestBookings.StatusOf(b, cutoff),
                RomeCalendar.DateInRome(b.CheckInDate),
                RomeCalendar.DateInRome(b.CheckOutDate)))
            .Where(r => r.Status is not (GuestBookingStatus.Expired or GuestBookingStatus.DatesUnavailable))
            .Where(r => scope == GuestAccountBookingScope.Upcoming ? r.CheckOut >= today : r.CheckOut < today)
            .ToList();

        var ordered = scope == GuestAccountBookingScope.Upcoming
            ? listed.OrderBy(r => r.CheckIn).ThenBy(r => r.CheckOut).ThenBy(r => r.Booking.Id)
            : listed.OrderByDescending(r => r.CheckIn).ThenByDescending(r => r.CheckOut).ThenBy(r => r.Booking.Id);

        var skip = (long)(page - 1) * pageSize;
        IReadOnlyList<GuestAccountBookingSummary> items = skip >= listed.Count
            ? []
            : ordered.Skip((int)skip).Take(pageSize).Select(Summarize).ToList();
        return new GuestAccountBookingPage(items, listed.Count, page, pageSize);
    }

    public async Task<GuestAccountBookingDetail> GetAsync(
        string accountEmail,
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(accountEmail);
        var booking = await OwnBookings(email)
            .AsSplitQuery()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Payments)
            .Where(b => b.Id == bookingId)
            .SingleOrDefaultAsync(cancellationToken) ?? throw GuestAccountBookings.NotFound();

        var view = await lookup.ViewOfAsync(booking, cancellationToken);
        return new GuestAccountBookingDetail(booking.Id, booking.Org.Slug, view);
    }

    public async Task<GuestAccountSessionOpened> OpenSessionAsync(
        string accountEmail,
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(accountEmail);
        var own = await OwnBookings(email)
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OrgId, b.BookingCode, OrgSlug = b.Org.Slug })
            .SingleOrDefaultAsync(cancellationToken) ?? throw GuestAccountBookings.NotFound();

        var expiresAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(GuestSessionService.SessionMinutesOf(configuration));
        var token = tokens.Issue(own.OrgId, GuestSessionTicket.HashEmail(email), expiresAt);
        return new GuestAccountSessionOpened(token, expiresAt, BookingCodes.Format(own.BookingCode), own.OrgSlug);
    }

    private IQueryable<Booking> OwnBookings(string email) =>
        db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(b =>
                b.Source == BookingSource.Direct &&
                b.Org.IsActive &&
                b.Guest.Email.Trim().ToLower() == email);

    private static GuestAccountBookingSummary Summarize(Row row)
    {
        var booking = row.Booking;
        var (paid, refunded) = GuestBookings.AmountsOf(booking);
        return new GuestAccountBookingSummary(
            booking.Id,
            booking.Org.Slug,
            BookingCodes.Format(booking.BookingCode),
            row.Status,
            booking.PropertyId,
            booking.Property.Slug,
            booking.Property.Name,
            booking.Property.City?.Trim() ?? string.Empty,
            FirstPublicPhoto(booking.Property.PhotoUrls),
            row.CheckIn,
            row.CheckOut,
            row.CheckOut.DayNumber - row.CheckIn.DayNumber,
            booking.NumberOfAdults,
            booking.NumberOfChildren,
            StayCurrency.ForProperty(booking.Property),
            booking.TotalPrice,
            paid,
            refunded,
            booking.PaymentOption,
            string.IsNullOrWhiteSpace(booking.Org.DisplayName) ? null : booking.Org.DisplayName.Trim());
    }

    internal static string? FirstPublicPhoto(IEnumerable<string>? urls) =>
        urls?
            .Select(url => url?.Trim())
            .FirstOrDefault(url => !string.IsNullOrEmpty(url)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps);

    private static string NormalizeEmail(string accountEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEmail);
        return accountEmail.Trim().ToLowerInvariant();
    }

    private sealed record Row(Booking Booking, GuestBookingStatus Status, DateOnly CheckIn, DateOnly CheckOut);
}
