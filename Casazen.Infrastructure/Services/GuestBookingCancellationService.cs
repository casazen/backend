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
/// Guest self-service cancellation, from the guest area or with the signed one-time link (BK-02, BK-07, PO 2026-10-08, DB-08a).
/// See <see cref="IGuestBookingCancellationService"/>.
/// </summary>
/// <remarks>
/// The refund is computed from the property's <see cref="CancellationPolicy"/> only — no CasaZen floor
/// (<see cref="Booking.FreeRefundDeadline"/> is ignored) — as the policy's share of <see cref="Booking.BasePrice"/> plus the tourist
/// tax (DB-D15 default). The calculation is
/// <see cref="CancellationRefundPolicy.EvaluateGuestRefund(Booking, DateTimeOffset, decimal)"/>, the same for the preview the guest sees
/// and for the cancellation; <see cref="BookingCancellationService"/> checks it again under its lock (the guest's minimum is the
/// policy's refund, DB-08h) and, when the guest cancels with the link, spends the link in the same transaction as the cancellation;
/// an error leaves it usable until it expires. The <c>BookingCancelled</c> event of a guest's cancellation (origin Guest,
/// E08/F06/US01/T01) is recorded by <see cref="BookingCancellationService"/>, in the save that cancels the booking: this class cancels
/// nothing itself, and a second record here would be a second event for the same cancellation.
/// </remarks>
public sealed class GuestBookingCancellationService(
    AppDbContext db,
    IBookingCancellationService cancellationService,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IConfiguration configuration,
    ILogger<GuestBookingCancellationService> logger,
    TimeProvider? timeProvider = null,
    IGuestEmailBrandingProvider? emailBranding = null,
    IGuestEmailLanguageProvider? emailLanguage = null) : IGuestBookingCancellationService
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

        var branding = await emailBranding.ForOrgOrNullAsync(booking.OrgId, cancellationToken);
        var culture = await emailLanguage.CultureOrAsync(booking.OrgId, booking.Id, EmailTemplates.DefaultCulture, cancellationToken: cancellationToken);
        var content = EmailTemplates.GuestCancelLink(
            culture,
            booking.Guest!.FirstName,
            booking.Property.Name,
            booking.CheckInDate,
            booking.CheckOutDate,
            cancelUrl,
            expiresAt,
            branding);

        emailQueue.Enqueue(guestEmail, content, EmailTemplates.Names.GuestCancelLink);

        logger.LogInformation(
            "Guest cancel link of booking {BookingId} queued, expires at {ExpiresAt}", bookingId, expiresAt);
    }

    public async Task<GuestCancellationPreview> PreviewForGuestAreaAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        return await BuildPreviewAsync(booking, GuestCancellationChannel.GuestArea, cancellationToken);
    }

    public async Task<GuestCancellationPreview> PreviewByGuestLinkAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        VerifyLink(booking, token);
        return await BuildPreviewAsync(booking, GuestCancellationChannel.SignedLink, cancellationToken);
    }

    public async Task<GuestCancellationResult> CancelFromGuestAreaAsync(
        Guid bookingId,
        decimal expectedRefundAmount,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        return await CancelAsync(booking, GuestCancellationChannel.GuestArea, expectedRefundAmount, cancellationToken);
    }

    public Task<GuestCancellationResult> CancelByGuestLinkAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default) =>
        CancelByGuestLinkCoreAsync(bookingId, token, expectedRefundAmount: null, cancellationToken);

    public Task<GuestCancellationResult> CancelByGuestLinkAsync(
        Guid bookingId,
        string token,
        decimal expectedRefundAmount,
        CancellationToken cancellationToken = default) =>
        CancelByGuestLinkCoreAsync(bookingId, token, expectedRefundAmount, cancellationToken);

    private async Task<GuestCancellationResult> CancelByGuestLinkCoreAsync(
        Guid bookingId,
        string token,
        decimal? expectedRefundAmount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        VerifyLink(booking, token);
        return await CancelAsync(booking, GuestCancellationChannel.SignedLink, expectedRefundAmount, cancellationToken);
    }

    private async Task<GuestCancellationResult> CancelAsync(
        Booking booking,
        GuestCancellationChannel channel,
        decimal? expectedRefundAmount,
        CancellationToken cancellationToken)
    {
        var bookingId = booking.Id;
        if (booking.Status == BookingStatus.Cancelled)
            return await AlreadyCancelledAsync(booking, cancellationToken);

        var preview = await BuildPreviewAsync(booking, channel, cancellationToken);
        if (!preview.Cancellable)
            throw NotCancellable(preview.Reason);

        if (expectedRefundAmount is { } expected && expected != preview.RefundAmount)
            throw new GuestRefundChangedException(preview);

        logger.LogInformation(
            "Guest self-cancellation of booking {BookingId} from the {Channel}: policy {PolicyPercent}%, refund {RefundAmount} {Currency}",
            bookingId, channel, preview.RefundPercent, preview.RefundAmount, preview.Currency);

        BookingCancellationResult result;
        try
        {
            result = await cancellationService.CancelAsync(
                new BookingCancellationRequest(
                    bookingId,
                    preview.RefundAmount,
                    channel == GuestCancellationChannel.GuestArea
                        ? "Annullamento autonomo dell'ospite dall'area ospite"
                        : "Annullamento autonomo dell'ospite tramite link firmato",
                    RequestedByUserId: null,
                    BookingCancellationInitiator.Guest),
                cancellationToken);
        }
        catch (DomainConflictException ex) when (ex.Code == BookingErrorCodes.AlreadyCancelled)
        {
            return await AlreadyCancelledAsync(await LoadBookingAsync(bookingId, cancellationToken), cancellationToken);
        }
        catch (DomainRuleException ex) when (MovedUnderTheLock(ex.Code))
        {
            // The money moved between the figure and the lock (a threshold of the policy, a payment that landed, a refund of the host):
            // the cancellation refused the amount and nothing was cancelled. The guest decides again on the figure as it is now,
            // as for any other change of the refund, not on a message written for the host (DB-08h-F5).
            var current = await BuildPreviewAsync(
                await LoadBookingAsync(bookingId, cancellationToken), channel, cancellationToken);
            throw new GuestRefundChangedException(current);
        }

        return new GuestCancellationResult(result.Booking, result.Refunds, preview.RefundAmount);
    }

    private static bool MovedUnderTheLock(string code) =>
        code is "booking_cancel_refund_below_minimum"
            or "booking_cancel_refund_required"
            or "refund_amount_exceeds_refundable"
            or "booking_cancel_nothing_to_refund";

    /// <summary>
    /// The token of the link against the booking, in the order the cancellation has always checked it: the hash first (constant time,
    /// anti-timing-attack), then, for a booking that is not cancelled yet, the expiry. A cancelled booking (by the host, by this link, by
    /// a click that was first) is not refused here: it answers with the outcome of that cancellation.
    /// </summary>
    private void VerifyLink(Booking booking, string token)
    {
        if (!GuestCancellationTokens.TokenMatches(booking.GuestCancelTokenHash, token))
            throw new DomainRuleException("guest_cancel_token_invalid", "GuestCancelTokenInvalid");

        if (booking.Status == BookingStatus.Cancelled)
            return;

        var now = _clock.GetUtcNow();
        if (booking.GuestCancelTokenExpiresAt is not { } expiresAt || expiresAt < now.UtcDateTime)
            throw new DomainRuleException("guest_cancel_token_expired", "GuestCancelTokenExpired");
    }

    private async Task<GuestCancellationPreview> BuildPreviewAsync(
        Booking booking,
        GuestCancellationChannel channel,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var currency = StayCurrency.ForProperty(booking.Property);
        var catalog = booking.Property.CancellationPolicy;
        var policy = CancellationRefundPolicy.EffectivePolicy(booking);
        var policyInfo = catalog is null
            ? null
            : new GuestCancellationPolicyInfo(
                catalog.Slug,
                catalog.Name,
                CancellationRefundPolicy.IsCatalogPolicy(catalog, policy) ? catalog.Description : null);
        var stay = new GuestCancellationStay(
            booking.Property.Name,
            RomeCalendar.DateInRome(booking.CheckInDate),
            RomeCalendar.DateInRome(booking.CheckOutDate));

        var quote = await cancellationService.GetQuoteAsync(booking.Id, cancellationToken);
        var alreadyRefunded = quote.RefundedAmount + quote.PendingRefundAmount;
        var kind = KindOf(booking, quote);

        var reason = ReasonNotCancellable(booking, now, channel);
        if (reason is not null)
        {
            GuestCancellationOutcome? outcome = null;
            if (reason == GuestCancellationReasons.AlreadyCancelled)
            {
                var refunds = await CancellationRefundsAsync(booking.Id, cancellationToken);
                outcome = new GuestCancellationOutcome(refunds.Sum(r => r.Amount), GuestCancellationRefundStatuses.Of(refunds));
            }

            return new GuestCancellationPreview(
                Cancellable: false,
                reason,
                kind,
                currency,
                quote.PaidAmount,
                alreadyRefunded,
                RefundPercent: 0m,
                RefundAmount: 0m,
                RetainedAmount: 0m,
                RetainedBecause: null,
                policyInfo,
                FullRefundUntil: null,
                PartialRefundPercent: null,
                PartialRefundUntil: null,
                stay,
                outcome,
                now.UtcDateTime);
        }

        // The one calculation of the guest's refund; what is left of what is refundable stays with the host, because of the policy.
        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, policy, now, quote.RefundableAmount);
        var retained = Math.Max(0m, quote.RefundableAmount - decision.Amount);
        var windows = CancellationRefundPolicy.RefundWindows(policy, booking.CheckInDate);

        return new GuestCancellationPreview(
            Cancellable: true,
            Reason: null,
            kind,
            currency,
            quote.PaidAmount,
            alreadyRefunded,
            decision.Percent,
            decision.Amount,
            retained,
            retained > 0m ? "policy" : null,
            policyInfo,
            windows.FullRefundUntil,
            windows.PartialRefundPercent,
            windows.PartialRefundUntil,
            stay,
            Outcome: null,
            now.UtcDateTime);
    }

    private static string? ReasonNotCancellable(Booking booking, DateTimeOffset now, GuestCancellationChannel channel)
    {
        if (booking.Status == BookingStatus.Cancelled)
            return GuestCancellationReasons.AlreadyCancelled;

        if (channel == GuestCancellationChannel.GuestArea
            && (booking.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut
                || now.UtcDateTime >= RomeCalendar.StartOfDayUtc(booking.CheckInDate)))
        {
            return GuestCancellationReasons.StayStarted;
        }

        if (booking.Status == BookingStatus.CheckedOut)
            return GuestCancellationReasons.NotCancellableStatus;

        return booking.Payments.Any(p => p.Status == PaymentStatus.Processing)
            ? GuestCancellationReasons.PaymentInProgress
            : null;
    }

    private static string KindOf(Booking booking, BookingCancellationQuote quote)
    {
        if (booking.Status == BookingStatus.Pending && booking.PaymentOption == PaymentOption.OnSite)
            return GuestCancellationKinds.WithdrawRequest;

        return booking.PaymentOption == PaymentOption.OnCancellationDeadline && quote.PaidAmount <= 0m
            ? GuestCancellationKinds.DeferredNoCharge
            : GuestCancellationKinds.Cancel;
    }

    private static Exception NotCancellable(string? reason) => reason switch
    {
        GuestCancellationReasons.StayStarted =>
            new DomainRuleException(GuestCancellationErrorCodes.StayStarted, "GuestCancelStayStarted"),
        GuestCancellationReasons.PaymentInProgress =>
            new DomainConflictException("booking_payment_in_progress", "BookingPaymentInProgress"),
        _ => new DomainRuleException("booking_not_cancellable_by_guest", "BookingNotCancellableByGuest"),
    };

    private async Task<GuestCancellationResult> AlreadyCancelledAsync(Booking booking, CancellationToken cancellationToken)
    {
        var refunds = await CancellationRefundsAsync(booking.Id, cancellationToken);

        logger.LogInformation("Guest cancellation of booking {BookingId} asked again: the booking is already cancelled", booking.Id);
        return new GuestCancellationResult(booking, refunds, refunds.Sum(r => r.Amount));
    }

    private async Task<List<PaymentRefund>> CancellationRefundsAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.PaymentRefunds
            .AsNoTracking()
            .Where(r => r.Origin == PaymentRefundOrigin.BookingCancellation && r.Payment.BookingId == bookingId)
            .ToListAsync(cancellationToken);

    private static bool IsCancellableByGuest(BookingStatus status) => BookingEditRules.IsCancellable(status);

    private async Task<Booking> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .Include(b => b.Guest)
            .Include(b => b.Property)
                .ThenInclude(p => p.CancellationPolicy)
            .Include(b => b.Org)
            .Include(b => b.Payments)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
}
