using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestBookingLookupService"/>
/// <remarks>
/// Anonymous endpoints: org of the site, booking code and email are matched in one query, so a missing code, a code of
/// another org and a wrong email take the same path and get the same 404. Only bookings of the public checkout
/// (<see cref="BookingSource.Direct"/>): their guests are the ones who receive the code. Nothing is logged of what the
/// guest typed. The guest session may prove the booking with the last four digits of the phone instead of the e-mail
/// (<see cref="LoadByPhoneAsync"/>, NF-143), with the same 404.
/// </remarks>
public sealed class GuestBookingLookupService(
    AppDbContext db,
    IGuestCheckInService checkInService,
    IGuestCheckInLinkEmailQueue linkEmails,
    PublicSiteLinks links,
    IConfiguration configuration,
    ILogger<GuestBookingLookupService> logger,
    TimeProvider? timeProvider = null,
    IBookingFlowSettingsService? flowSettings = null) : IGuestBookingLookupService
{
    // What the phone proof looks up when the guest typed something that is not a code: a stored code has only the characters of
    // BookingCodes.Alphabet, so no booking has this one.
    private const string UnmatchableCode = "-";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestBookingView> FindAsync(
        GuestBookingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadAsync(credentials, cancellationToken);
        return await ViewOfAsync(booking, cancellationToken);
    }

    /// <summary>
    /// <paramref name="booking"/> as its guest sees it. The caller has already proved that the guest is the one of the booking
    /// (code + email, or a guest session): nothing is checked here.
    /// </summary>
    internal async Task<GuestBookingView> ViewOfAsync(Booking booking, CancellationToken cancellationToken)
    {
        var ttlMinutes = CheckoutHolds.GetTtlMinutes(configuration);
        var cutoff = CheckoutHolds.CutoffAt(_clock.GetUtcNow().UtcDateTime, ttlMinutes);
        var outcomeState = CheckoutOutcomes.StateOf(booking, cutoff);
        var checkIn = await CheckInAccessAsync(booking, cancellationToken);
        var (paidAmount, refundedAmount) = GuestBookings.AmountsOf(booking);

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
            paidAmount,
            refundedAmount,
            StayCurrency.ForProperty(booking.Property),
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

        // A new session, its link only in the email, the older links expired once the email is queued. The email goes
        // through the check-in link email job, which records its real outcome for the host (CO-09).
        var token = await checkInService.CreateSessionAsync(booking.Id, booking.OrgId);
        var tokenHash = GuestCheckInService.HashToken(token);
        var sessionId = await db.GuestCheckInSessions
            .Where(s => s.TokenHash == tokenHash)
            .Select(s => s.Id)
            .SingleAsync(cancellationToken);
        var email = await linkEmails.QueueAsync(sessionId, token, cancellationToken);
        if (email.Status == GuestCheckInLinkEmailStatus.Failed)
        {
            await checkInService.ExpireTokenAsync(token);
            logger.LogError(
                "Check-in link requested by the guest of booking {BookingId} was not queued: {Reason}", booking.Id, email.Error);
            throw new EmailConfigurationException("The check-in link email could not be queued.");
        }

        await checkInService.ExpireOtherActiveSessionsAsync(booking.Id, token);
        logger.LogInformation("Check-in link sent again at the request of the guest of booking {BookingId}", booking.Id);
    }

    /// <summary>The booking of the site that matches <paramref name="credentials"/>; the same <see cref="NotFoundException"/> whatever does not match.</summary>
    internal async Task<Booking> LoadAsync(GuestBookingCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var slug = credentials.OrgSlug?.Trim() ?? string.Empty;
        var email = credentials.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!BookingCodes.TryNormalize(credentials.BookingCode, out var code) || slug.Length == 0 || email.Length == 0)
            throw NotFound();

        // The slug of the link may be a previous slug of the org (PL-04): links in older emails keep working.
        var orgId = await OrgService.ResolveOrgIdBySlugAsync(db, slug, cancellationToken) ?? throw NotFound();

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
                b.OrgId == orgId &&
                b.Org.IsActive &&
                b.Guest.Email.Trim().ToLower() == email)
            .SingleOrDefaultAsync(cancellationToken);

        return booking ?? throw NotFound();
    }

    /// <summary>
    /// The booking of the site that matches <paramref name="credentials"/>, proved with the last four digits of the guest's phone instead of the
    /// e-mail (NF-143, the guest session only); the same <see cref="NotFoundException"/> as the e-mail proof whatever does not match.
    /// </summary>
    /// <remarks>
    /// One path for every outcome, so neither the answer nor its time tells whether a code exists: an unknown code, a code of another site, a string
    /// that is not a code, a booking without a phone and wrong digits all run the same light query (the booking id and the guest's phone, nothing
    /// else; a site or a code that cannot match is looked up all the same) and the same fixed-time comparison
    /// (<see cref="GuestPhoneDigits.Matches"/>, against a placeholder when there is nothing to compare). The booking is loaded in full only after the
    /// digits match. Direct bookings of an active org only, as the e-mail proof. Nothing the guest typed is logged.
    /// </remarks>
    internal async Task<Booking> LoadByPhoneAsync(GuestBookingPhoneCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var slug = credentials.OrgSlug?.Trim() ?? string.Empty;

        // The slug of the link may be a previous slug of the org (PL-04). A slug of no org and a string that is not a code still run the query below,
        // with values no booking has.
        var orgId = slug.Length == 0 ? null : await OrgService.ResolveOrgIdBySlugAsync(db, slug, cancellationToken);
        var site = orgId ?? Guid.Empty;
        var code = BookingCodes.TryNormalize(credentials.BookingCode, out var normalized) ? normalized : UnmatchableCode;

        // Anonymous request: the tenant filter is off, the org comes from the site and code + digits are the access check. (OrgId, BookingCode) is
        // unique, so the query finds at most one booking.
        var candidate = await db.Bookings
            .AsNoTracking()
            .Where(b =>
                b.BookingCode == code &&
                b.Source == BookingSource.Direct &&
                b.OrgId == site &&
                b.Org.IsActive)
            .Select(b => new { b.Id, b.Guest.PhoneNumber })
            .SingleOrDefaultAsync(cancellationToken);

        var matches = GuestPhoneDigits.Matches(candidate?.PhoneNumber, credentials.PhoneLast4);
        if (!matches || candidate is null)
            throw NotFound();

        var booking = await db.Bookings
            .AsNoTracking()
            .AsSplitQuery()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .Where(b => b.Id == candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        // Gone between the two reads: the answer of any other refusal.
        return booking ?? throw NotFound();
    }

    /// <summary>
    /// The direct booking <paramref name="bookingCode"/> of the guest of a session (DB-06a): of the org of the session, with the code in
    /// the stored form, and whose guest has the email the session was opened with. A code that is not a code, a code that does not
    /// exist, a booking of another site and a booking of another guest all end in the same <see cref="NotFoundException"/>. The org is
    /// in the query, not only in the tenant filter of the request: the filter is a second wall.
    /// </summary>
    internal async Task<Booking> LoadAsync(GuestSessionTicket ticket, string? bookingCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!BookingCodes.TryNormalize(bookingCode, out var code))
            throw NotFound();

        // (OrgId, BookingCode) is unique, so the query finds at most one booking; the email decides whether it is this guest's.
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
                b.OrgId == ticket.OrgId &&
                b.Org.IsActive)
            .SingleOrDefaultAsync(cancellationToken);

        return booking is not null && ticket.IsForEmail(booking.Guest.Email) ? booking : throw NotFound();
    }

    /// <summary>
    /// The online check-in of <paramref name="booking"/>, with the rules of the daily job that emails the links
    /// (<c>GuestCheckInSendJob</c>): confirmed or checked-in stays not over, from <c>CheckIn:SendWindowDays</c> days
    /// before the arrival, none once the data were sent or the Alloggiati Web communication is done.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="GuestCheckInAccessRules"/> since E23/F02/US01/T01, the one place that also decides what the e-mail that confirms the booking says about the link
    /// (<see cref="BookingNotifier"/>): by the id of a booking the caller already proved to be the guest's, so the reads do not depend on the tenant of the request: an anonymous
    /// request has none, a guest session has the org of the booking, and the signed-in guest of the mobile app (<see cref="GuestAccountBookingService"/>) has its own host org or
    /// none, and must read the same rows.
    /// </remarks>
    private async Task<GuestCheckInAccess> CheckInAccessAsync(Booking booking, CancellationToken cancellationToken)
    {
        return await GuestCheckInAccessRules.ReadAsync(
            db,
            booking.Id,
            booking.Status,
            booking.CheckInDate,
            booking.CheckOutDate,
            _clock.TodayInRomeAsDateOnly(),
            _clock.GetUtcNow().UtcDateTime,
            // casazen/backend#978: the window of the org's booking flow, the one its links go out with.
            flowSettings is null
                ? configuration.GetValue("CheckIn:SendWindowDays", 3)
                : (await flowSettings.GetAsync(booking.OrgId, cancellationToken)).CheckInLinkDaysBefore,
            cancellationToken);
    }

    private static NotFoundException NotFound() =>
        new("No booking of the site matches the booking code and email")
        {
            Code = GuestBookingLookupErrorCodes.NotFound,
            MessageKey = "GuestBookingNotFound",
        };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
