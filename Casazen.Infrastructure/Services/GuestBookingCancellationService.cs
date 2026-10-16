using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
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
/// (tourist tax excluded), as decided on 2026-10-08. The calculation is
/// <see cref="CancellationRefundPolicy.EvaluateGuestRefund(Booking, DateTimeOffset, decimal)"/>;
/// <see cref="BookingCancellationService"/> checks it again under its lock (the guest's minimum is the policy's refund,
/// DB-08h) and spends the link in the same transaction as the cancellation; an error leaves it usable until it expires.
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

        // A cancelled booking answers with the outcome of its cancellation and refunds nothing again, whoever cancelled it:
        // the host, this link (spent by that cancellation, but the hash stays so the click is recognised), or a click of
        // this link that was first. It is the cancelled state, not the link, that stops a second refund.
        if (booking.Status == BookingStatus.Cancelled)
            return await AlreadyCancelledAsync(booking, cancellationToken);

        var now = _clock.GetUtcNow();
        if (booking.GuestCancelTokenExpiresAt is not { } expiresAt || expiresAt < now.UtcDateTime)
            throw new DomainRuleException("guest_cancel_token_expired", "GuestCancelTokenExpired");

        if (!IsCancellableByGuest(booking.Status))
            throw new DomainRuleException("booking_not_cancellable_by_guest", "BookingNotCancellableByGuest");

        // Refund from the property policy alone (no FreeRefundDeadline floor), base = BasePrice, capped at what is
        // refundable: the one calculation, in Core, that the cancellation checks again under its lock.
        var quote = await cancellationService.GetQuoteAsync(bookingId, cancellationToken);
        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, now, quote.RefundableAmount);

        // Logged before the cancellation: if it is refused (a rule, a payment in progress, Stripe) this is what was asked.
        logger.LogInformation(
            "Guest self-cancellation of booking {BookingId}: policy {PolicyPercent}%, refund {RefundAmount} EUR",
            bookingId, decision.Percent, decision.Amount);

        // The cancellation spends the link in its own transaction, and only when it succeeds: any error leaves the link
        // usable until it expires.
        BookingCancellationResult result;
        try
        {
            result = await cancellationService.CancelAsync(
                new BookingCancellationRequest(
                    bookingId,
                    decision.Amount,
                    "Annullamento autonomo dell'ospite tramite link firmato",
                    RequestedByUserId: null,
                    BookingCancellationInitiator.Guest),
                cancellationToken);
        }
        catch (DomainConflictException ex) when (ex.Code == BookingErrorCodes.AlreadyCancelled)
        {
            // Two clicks at once: the other one cancelled while this one waited for the lock. It refunded once.
            return await AlreadyCancelledAsync(await LoadBookingAsync(bookingId, cancellationToken), cancellationToken);
        }

        return new GuestCancellationResult(result.Booking, result.Refunds, decision.Amount);
    }

    /// <summary>
    /// The outcome of the cancellation that a repeated click finds: the booking as it is and the refunds that cancellation
    /// reserved, as Stripe left them.
    /// </summary>
    private async Task<GuestCancellationResult> AlreadyCancelledAsync(Booking booking, CancellationToken cancellationToken)
    {
        var refunds = await db.PaymentRefunds
            .AsNoTracking()
            .Where(r => r.Origin == PaymentRefundOrigin.BookingCancellation && r.Payment.BookingId == booking.Id)
            .ToListAsync(cancellationToken);

        logger.LogInformation("Guest cancel link of booking {BookingId} used again: the booking is already cancelled", booking.Id);
        return new GuestCancellationResult(booking, refunds, refunds.Sum(r => r.Amount));
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
