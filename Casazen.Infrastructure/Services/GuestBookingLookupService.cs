using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestBookingLookupService"/>
/// <remarks>
/// Anonymous endpoints: org of the site, booking code and email are matched in one query, so a missing code, a code of
/// another org and a wrong email take the same path and get the same 404. Only bookings of the public checkout
/// (<see cref="BookingSource.Direct"/>): their guests are the ones who receive the code. Nothing is logged of what the
/// guest typed.
/// </remarks>
public sealed class GuestBookingLookupService(
    AppDbContext db,
    IGuestCheckInService checkInService,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IConfiguration configuration,
    ILogger<GuestBookingLookupService> logger,
    TimeProvider? timeProvider = null) : IGuestBookingLookupService
{
    private const string Currency = "EUR";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestBookingView> FindAsync(
        GuestBookingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadAsync(credentials, cancellationToken);
        var ttlMinutes = CheckoutHolds.GetTtlMinutes(configuration);
        var cutoff = CheckoutHolds.CutoffAt(_clock.GetUtcNow().UtcDateTime, ttlMinutes);
        var outcomeState = CheckoutOutcomes.StateOf(booking, cutoff);
        var checkIn = await CheckInAccessAsync(booking, cancellationToken);
        var collected = booking.Payments
            .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
            .ToList();

        return new GuestBookingView(
            BookingCodes.Format(booking.BookingCode),
            GuestBookings.StatusOf(booking, cutoff),
            booking.PaymentOption,
            booking.PropertyId,
            booking.Property.Slug,
            booking.Property.Name,
            string.IsNullOrWhiteSpace(booking.Property.City) ? null : booking.Property.City,
            RomeCalendar.DateInRome(booking.CheckInDate),
            RomeCalendar.DateInRome(booking.CheckOutDate),
            booking.NumberOfAdults,
            booking.NumberOfChildren,
            booking.BasePrice - booking.CleaningFee,
            booking.CleaningFee,
            booking.TouristTax,
            booking.TotalPrice,
            collected.Sum(p => p.Amount),
            collected.Sum(p => p.RefundedAmount),
            Currency,
            CheckoutOutcomes.ExpiresAt(booking, outcomeState, ttlMinutes),
            booking.PaymentOption == PaymentOption.OnCancellationDeadline && booking.FreeRefundDeadline is { } deadline
                ? RomeCalendar.DateInRome(deadline)
                : null,
            new GuestBookingHostContact(NullIfBlank(booking.Org.DisplayName), NullIfBlank(booking.Org.ContactEmail)),
            checkIn);
    }

    public async Task SendCheckInLinkAsync(
        GuestBookingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadAsync(credentials, cancellationToken);
        var access = await CheckInAccessAsync(booking, cancellationToken);
        if (access.Status != GuestCheckInAccessStatus.Open)
        {
            throw new DomainConflictException(
                GuestBookingLookupErrorCodes.CheckInLinkUnavailable,
                "GuestCheckInLinkUnavailable");
        }

        // A missing App:PublicSiteBaseUrl is a configuration error (500) before any session is created.
        links.EnsureConfigured();

        // The same steps as the host's "resend link": a new session, its link only in the email, the older links expired.
        var token = await checkInService.CreateSessionAsync(booking.Id, booking.OrgId);
        var email = EmailTemplates.GuestCheckInLink(
            EmailTemplates.DefaultCulture,
            booking.Guest.FirstName,
            booking.Property.Name,
            booking.CheckInDate,
            links.GuestCheckIn(token));

        if (!emailQueue.Enqueue(booking.Guest.Email, email, EmailTemplates.Names.GuestCheckInLink))
        {
            await checkInService.ExpireTokenAsync(token);
            logger.LogError("Check-in link requested by the guest of booking {BookingId} was not queued", booking.Id);
            throw new EmailConfigurationException("The check-in link email could not be queued.");
        }

        await checkInService.ExpireOtherActiveSessionsAsync(booking.Id, token);
        logger.LogInformation("Check-in link sent again at the request of the guest of booking {BookingId}", booking.Id);
    }

    private async Task<Booking> LoadAsync(GuestBookingCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var slug = credentials.OrgSlug?.Trim() ?? string.Empty;
        var email = credentials.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!BookingCodes.TryNormalize(credentials.BookingCode, out var code) || slug.Length == 0 || email.Length == 0)
            throw NotFound();

        // Anonymous request: the tenant filter is off, the org comes from the site and code + email are the access check.
        var booking = await db.Bookings
            .AsNoTracking()
            .AsSplitQuery()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .Where(b =>
                b.BookingCode == code &&
                b.Source == BookingSource.Direct &&
                b.Org.Slug == slug &&
                b.Org.IsActive &&
                b.Guest.Email.Trim().ToLower() == email)
            .SingleOrDefaultAsync(cancellationToken);

        return booking ?? throw NotFound();
    }

    /// <summary>
    /// The online check-in of <paramref name="booking"/>, with the rules of the daily job that emails the links
    /// (<c>GuestCheckInSendJob</c>): confirmed or checked-in stays not over, from <c>CheckIn:SendWindowDays</c> days
    /// before the arrival, none once the data were sent or the Alloggiati Web communication is done.
    /// </summary>
    private async Task<GuestCheckInAccess> CheckInAccessAsync(Booking booking, CancellationToken cancellationToken)
    {
        var today = _clock.TodayInRomeAsDateOnly();
        var checkInDate = RomeCalendar.DateInRome(booking.CheckInDate);
        var checkOutDate = RomeCalendar.DateInRome(booking.CheckOutDate);
        if (booking.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn) || checkOutDate < today)
            return new GuestCheckInAccess(GuestCheckInAccessStatus.NotApplicable, null, null);

        var sessions = await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.BookingId == booking.Id)
            .Select(s => new { s.Status, s.SentAt, s.ExpiresAt })
            .ToListAsync(cancellationToken);
        var communicationSent = await db.AlloggiatiWebReports
            .AnyAsync(
                r => r.BookingId == booking.Id &&
                     (r.Status == AlloggiatiWebStatus.Inviato || r.Status == AlloggiatiWebStatus.InviatoManualmente),
                cancellationToken);
        if (communicationSent ||
            sessions.Any(s => s.Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato))
        {
            return new GuestCheckInAccess(GuestCheckInAccessStatus.Completed, null, null);
        }

        var opensOn = checkInDate.AddDays(-configuration.GetValue("CheckIn:SendWindowDays", 3));
        if (booking.Status == BookingStatus.Confirmed && today < opensOn)
            return new GuestCheckInAccess(GuestCheckInAccessStatus.NotYetOpen, opensOn, null);

        var now = _clock.GetUtcNow().UtcDateTime;
        var linkSentAt = sessions
            .Where(s =>
                (s.Status is GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione) &&
                s.ExpiresAt > now)
            .Max(s => s.SentAt);
        return new GuestCheckInAccess(GuestCheckInAccessStatus.Open, null, linkSentAt);
    }

    private static NotFoundException NotFound() =>
        new("No booking of the site matches the booking code and email")
        {
            Code = GuestBookingLookupErrorCodes.NotFound,
            MessageKey = "GuestBookingNotFound",
        };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
