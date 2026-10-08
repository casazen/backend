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

/// <summary>
/// Guest self-service cancellation via a signed one-time link (BK-02, BK-07, PO 2026-10-08).
/// See <see cref="IGuestBookingCancellationService"/>.
/// </summary>
/// <remarks>
/// The refund is computed from the property's <see cref="CancellationPolicy"/> only — no CasaZen floor
/// (<see cref="Booking.FreeRefundDeadline"/> is ignored), and the base is <see cref="Booking.BasePrice"/>
/// (tourist tax excluded), as decided on 2026-10-08.
/// </remarks>
public sealed class GuestBookingCancellationService(
    AppDbContext db,
    IBookingCancellationService cancellationService,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IConfiguration configuration,
    ILogger<GuestBookingCancellationService> logger,
    TimeProvider? timeProvider = null) : IGuestBookingCancellationService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task SendCancelLinkAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);

        if (!IsCancellableByGuest(booking.Status))
            throw new DomainRuleException("booking_not_cancellable_by_guest", "BookingNotCancellableByGuest");

        var token = GuestCancellationTokens.NewToken();
        var now = _clock.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddHours(GuestCancellationTokens.GetTokenExpiryHours(configuration));

        booking.GuestCancelTokenHash = GuestCancellationTokens.HashToken(token);
        booking.GuestCancelTokenExpiresAt = expiresAt;
        booking.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var guestEmail = booking.Guest?.Email?.Trim();
        if (string.IsNullOrEmpty(guestEmail))
        {
            logger.LogWarning(
                "Guest cancel link of booking {BookingId} not sent: guest has no email address", bookingId);
            return;
        }

        var org = booking.Org;
        if (org is null || string.IsNullOrWhiteSpace(org.Slug))
        {
            logger.LogWarning(
                "Guest cancel link of booking {BookingId} not sent: org slug unavailable", bookingId);
            return;
        }

        string cancelUrl;
        try
        {
            cancelUrl = links.GuestBookingCancel(org.Slug, bookingId, token);
        }
        catch (EmailConfigurationException ex)
        {
            logger.LogWarning(ex,
                "Guest cancel link of booking {BookingId} not sent: public site URL not configured", bookingId);
            return;
        }

        var content = EmailTemplates.GuestCancelLink(
            EmailTemplates.DefaultCulture,
            booking.Guest!.FirstName,
            booking.Property.Name,
            booking.CheckInDate,
            booking.CheckOutDate,
            cancelUrl,
            expiresAt);

        emailQueue.Enqueue(guestEmail, content, EmailTemplates.Names.GuestCancelLink);

        logger.LogInformation(
            "Guest cancel link of booking {BookingId} queued, expires at {ExpiresAt}", bookingId, expiresAt);
    }

    public async Task<GuestCancellationResult> CancelByGuestLinkAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var booking = await LoadBookingAsync(bookingId, cancellationToken);

        // Verify token (constant-time, anti-timing-attack)
        if (!GuestCancellationTokens.TokenMatches(booking.GuestCancelTokenHash, token))
            throw new DomainRuleException("guest_cancel_token_invalid", "GuestCancelTokenInvalid");

        var now = _clock.GetUtcNow().UtcDateTime;
        if (booking.GuestCancelTokenExpiresAt is not { } expiresAt || expiresAt < now)
            throw new DomainRuleException("guest_cancel_token_expired", "GuestCancelTokenExpired");

        if (!IsCancellableByGuest(booking.Status))
            throw new DomainRuleException("booking_not_cancellable_by_guest", "BookingNotCancellableByGuest");

        // Get quote first (reads payments, needed for refund math)
        var quote = await cancellationService.GetQuoteAsync(bookingId, cancellationToken);

        // Compute guest refund: property policy only (no FreeRefundDeadline floor), base = BasePrice
        var policyPercent = EvaluatePolicyPercent(booking.Property?.CancellationPolicy, booking.CheckInDate, now);
        var refundAmount = ComputeGuestRefund(quote.RefundableAmount, booking.BasePrice, policyPercent);

        // Invalidate token before cancelling to prevent replay even on failure
        booking.GuestCancelTokenHash = null;
        booking.GuestCancelTokenExpiresAt = null;
        booking.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Guest self-cancellation of booking {BookingId}: policy {PolicyPercent}%, refund {RefundAmount} EUR",
            bookingId, policyPercent, refundAmount);

        var result = await cancellationService.CancelAsync(
            new BookingCancellationRequest(
                bookingId,
                refundAmount > 0 ? refundAmount : null,
                "Annullamento autonomo dell'ospite tramite link firmato",
                RequestedByUserId: null),
            cancellationToken);

        return new GuestCancellationResult(result.Booking, result.Refunds, refundAmount);
    }

    /// <summary>
    /// Refund percent from the property policy alone (BK-02, PO 2026-10-08): no FreeRefundDeadline floor,
    /// no invented clauses. 0 when the booking has no policy.
    /// </summary>
    private static decimal EvaluatePolicyPercent(CancellationPolicy? policy, DateTime checkInDate, DateTime nowUtc)
    {
        if (policy is null)
            return 0m;

        var hoursBeforeCheckIn = (RomeCalendar.StartOfDayUtc(checkInDate) - nowUtc).TotalHours;

        if (hoursBeforeCheckIn >= policy.FullRefundHours)
            return 100m;

        if (hoursBeforeCheckIn >= policy.PartialRefundHours)
            return Math.Clamp(policy.PartialRefundPercent, 0m, 100m);

        return 0m;
    }

    /// <summary>
    /// Amount to refund: policy percent of <paramref name="basePrice"/> (tourist tax excluded), capped at
    /// <paramref name="refundableAmount"/>, never negative.
    /// </summary>
    private static decimal ComputeGuestRefund(decimal refundableAmount, decimal basePrice, decimal policyPercent)
    {
        if (refundableAmount <= 0 || policyPercent <= 0)
            return 0m;

        var due = Math.Round(basePrice * policyPercent / 100m, 2, MidpointRounding.AwayFromZero);
        return Math.Max(0m, Math.Min(refundableAmount, due));
    }

    private static bool IsCancellableByGuest(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn;

    private async Task<Booking> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .Include(b => b.Guest)
            .Include(b => b.Property)
                .ThenInclude(p => p.CancellationPolicy)
            .Include(b => b.Org)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
}
