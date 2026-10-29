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
/// an error leaves it usable until it expires.
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
    private const string Currency = "EUR";

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

    /// <summary>
    /// The cancellation of <paramref name="booking"/> for the refund its preview says (and, when the guest was shown one,
    /// <paramref name="expectedRefundAmount"/>). A cancelled booking answers with the outcome of its cancellation and refunds nothing again,
    /// whoever cancelled it: it is the cancelled state, not the link or a key, that stops a second refund.
    /// </summary>
    private async Task<GuestCancellationResult> CancelAsync(
        Booking booking,
        GuestCancellationChannel channel,
        decimal? expectedRefundAmount,
        CancellationToken cancellationToken)
    {
        var bookingId = booking.Id;
        if (booking.Status == BookingStatus.Cancelled)
            return await AlreadyCancelledAsync(booking, cancellationToken);

        // The refund from the property policy alone (no FreeRefundDeadline floor), the base price and the tourist tax, capped at what is
        // refundable: the one calculation, in Core, that the cancellation checks again under its lock.
        var preview = await BuildPreviewAsync(booking, channel, cancellationToken);
        if (!preview.Cancellable)
            throw NotCancellable(preview.Reason);

        // The guest confirms the figure they saw. If it is not that any more (a threshold of the policy was crossed, a payment changed)
        // nothing is cancelled and the new figure goes back to them.
        if (expectedRefundAmount is { } expected && expected != preview.RefundAmount)
            throw new GuestRefundChangedException(preview);

        // Logged before the cancellation: if it is refused (a rule, a payment in progress, Stripe) this is what was asked. Ids and
        // amounts only: nothing that names the guest.
        logger.LogInformation(
            "Guest self-cancellation of booking {BookingId} from the {Channel}: policy {PolicyPercent}%, refund {RefundAmount} EUR",
            bookingId, channel, preview.RefundPercent, preview.RefundAmount);

        // The cancellation spends the link (if the guest has one) in its own transaction, and only when it succeeds: any error leaves
        // the link usable until it expires.
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
            // Two clicks at once: the other one cancelled while this one waited for the lock. It refunded once.
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

    /// <summary>The refusals of the cancellation that mean "the refund is not what you asked for any more", not "you cannot cancel".</summary>
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

    /// <summary>
    /// What cancelling <paramref name="booking"/> would do now from <paramref name="channel"/>: whether it can be cancelled, why not, and
    /// the refund. Needs the booking with its property and policy, guest and payments (<see cref="LoadBookingAsync"/>).
    /// </summary>
    private async Task<GuestCancellationPreview> BuildPreviewAsync(
        Booking booking,
        GuestCancellationChannel channel,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
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
                Currency,
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
            Currency,
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

    /// <summary>
    /// Why the guest cannot cancel <paramref name="booking"/> from <paramref name="channel"/> now, or <c>null</c> when they can. From the
    /// guest area a stay that has started is not cancelled (the guest writes to the host, DB-D3): the check-in day began or the host
    /// registered the arrival. The link the host sent also works for a stay in progress.
    /// </summary>
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

        // A payment that is settling (SEPA, a late webhook): cancelling now would leave it to confirm the booking again (BK-04).
        return booking.Payments.Any(p => p.Status == PaymentStatus.Processing)
            ? GuestCancellationReasons.PaymentInProgress
            : null;
    }

    /// <summary>
    /// What cancelling means to the guest: withdrawing a request to pay at the property that the host has not accepted, releasing a
    /// saved card that was never charged, or the plain cancellation of a booking.
    /// </summary>
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

    /// <summary>
    /// The outcome of the cancellation that a repeated click finds: the booking as it is and the refunds that cancellation
    /// reserved, as Stripe left them.
    /// </summary>
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

    private static bool IsCancellableByGuest(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn;

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
