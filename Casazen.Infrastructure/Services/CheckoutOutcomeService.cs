using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICheckoutOutcomeService"/>
/// <remarks>
/// Anonymous endpoints: the checkout token is the only access check, so a wrong booking id, a wrong token, a booking that
/// is not from the public checkout and a booking created before the token existed all get the same 404. Reads only: the
/// state changes through the webhooks, the expiry job and the host (BK-04, BK-21, BK-06).
/// <para>The page also serves the payment link of a booking the host entered (<see cref="HostPaymentLinks"/>, E08/F01/US01/T05): a
/// confirmed booking, never a hold, so it has no expiry; its state is the one of its balance and the session pays exactly the balance with
/// the PaymentIntent of the last link. Nothing here confirms or announces it: the webhook only records the payment.</para>
/// </remarks>
public sealed class CheckoutOutcomeService(
    AppDbContext db,
    IStripeService stripeService,
    IConfiguration configuration,
    ILogger<CheckoutOutcomeService> logger,
    TimeProvider? timeProvider = null) : ICheckoutOutcomeService
{
    /// <summary>Intent states in which the guest can still pay (again): nothing was collected yet.</summary>
    private static readonly HashSet<string> PayableIntentStatuses = new(StringComparer.Ordinal)
    {
        "requires_payment_method",
        "requires_confirmation",
        "requires_action",
    };

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CheckoutOutcome> GetOutcomeAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadAsync(bookingId, token, cancellationToken);
        if (HostPaymentLinks.IsHostPaymentLink(booking))
            return HostLinkOutcome(booking);

        var ttlMinutes = CheckoutHolds.GetTtlMinutes(configuration);
        var state = CheckoutOutcomes.StateOf(booking, CutoffNow(ttlMinutes));
        var deferredPayment = booking.PaymentOption == PaymentOption.OnCancellationDeadline
            ? DeferredCharges.FindPayment(booking, booking.Payments)
            : null;

        return new CheckoutOutcome(
            booking.Id,
            state,
            booking.PaymentOption,
            booking.PropertyId,
            booking.Property.Slug,
            booking.Property.Name,
            RomeCalendar.DateInRome(booking.CheckInDate),
            RomeCalendar.DateInRome(booking.CheckOutDate),
            booking.NumberOfAdults,
            booking.NumberOfChildren,
            booking.TotalPrice,
            StayCurrency.ForProperty(booking.Property),
            CheckoutOutcomes.ExpiresAt(booking, state, ttlMinutes, DeferredCharges.GetCancelAfterDays(configuration)),
            // Once the deferred charge is collected the page shows "paid online", not a charge still to come (BK-08).
            booking.PaymentOption == PaymentOption.OnCancellationDeadline &&
            booking.FreeRefundDeadline is { } deadline &&
            (deferredPayment is null || !DeferredCharges.IsCollected(deferredPayment.Status))
                ? RomeCalendar.DateInRome(deadline)
                : null,
            BookingCodes.Format(booking.BookingCode));
    }

    public async Task<CheckoutPaymentSession> ResumePaymentAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadAsync(bookingId, token, cancellationToken);
        if (HostPaymentLinks.IsHostPaymentLink(booking))
            return await ResumeHostLinkAsync(booking, cancellationToken);

        var ttlMinutes = CheckoutHolds.GetTtlMinutes(configuration);
        var state = CheckoutOutcomes.StateOf(booking, CutoffNow(ttlMinutes));

        if (state == CheckoutOutcomeState.Expired)
            throw HoldExpired();
        if (state is not (CheckoutOutcomeState.AwaitingPayment or CheckoutOutcomeState.PaymentFailed))
            throw NotResumable();

        var publishableKey = configuration["Stripe:PublishableKey"] ?? string.Empty;
        var expiresAt = CheckoutOutcomes.ExpiresAt(booking, state, ttlMinutes, DeferredCharges.GetCancelAfterDays(configuration));

        if (DeferredCharges.AwaitsGuestPayment(booking))
        {
            // Confirmed "Paga alla scadenza" whose off-session charge failed (BK-08): the guest confirms the same
            // PaymentIntent on-session (3-D Secure, or another payment method), so it can never be paid twice.
            var deferredPayment = DeferredCharges.FindPayment(booking, booking.Payments)!;
            var deferredAccount = deferredPayment.StripeAccountId ?? booking.Org.StripeConnectedAccountId;
            if (PaymentRefundService.PaymentIntentIdOf(deferredPayment) is not { } deferredIntentId ||
                string.IsNullOrWhiteSpace(deferredAccount))
                throw NotResumable();

            var deferredIntent = await stripeService.GetPaymentIntentAsync(deferredIntentId, deferredAccount, cancellationToken);
            if (deferredIntent.Status is null || !PayableIntentStatuses.Contains(deferredIntent.Status))
            {
                logger.LogInformation(
                    "Checkout {BookingId}: deferred charge not resumed, the intent is {IntentStatus}",
                    booking.Id,
                    deferredIntent.Status ?? "unknown");
                throw NotResumable();
            }

            return new CheckoutPaymentSession(
                booking.Id, booking.PaymentOption, deferredIntent.ClientSecret, null, publishableKey, deferredAccount, expiresAt);
        }

        if (booking.PaymentOption == PaymentOption.OnCancellationDeadline)
        {
            var setupPayment = booking.Payments.FirstOrDefault(p => p.TransactionId == booking.StripeSetupIntentId);
            var account = setupPayment?.StripeAccountId ?? booking.Org.StripeConnectedAccountId;
            if (string.IsNullOrWhiteSpace(booking.StripeSetupIntentId) || string.IsNullOrWhiteSpace(account))
                throw NotResumable();

            var setupIntent = await stripeService.GetSetupIntentAsync(booking.StripeSetupIntentId, account, cancellationToken);
            EnsurePayable(booking.Id, setupIntent.Status);
            return new CheckoutPaymentSession(
                booking.Id, booking.PaymentOption, null, setupIntent.ClientSecret, publishableKey, account, expiresAt);
        }

        var payment = booking.Payments
            .Where(p => p.StripePaymentIntentId != null)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault();
        var paymentAccount = payment?.StripeAccountId ?? booking.Org.StripeConnectedAccountId;
        if (payment is null || string.IsNullOrWhiteSpace(paymentAccount))
            throw NotResumable();

        var paymentIntent = await stripeService.GetPaymentIntentAsync(
            payment.StripePaymentIntentId!, paymentAccount, cancellationToken);
        EnsurePayable(booking.Id, paymentIntent.Status);
        return new CheckoutPaymentSession(
            booking.Id, booking.PaymentOption, paymentIntent.ClientSecret, null, publishableKey, paymentAccount, expiresAt);
    }

    private async Task<Booking> LoadAsync(Guid bookingId, string token, CancellationToken cancellationToken)
    {
        // Anonymous request: the tenant filter is off, the token is the access check.
        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Payments)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        // The checkout of the booking site, or the payment link of a booking the host entered (E08/F01/US01/T05): any other booking, even
        // with a token, is not a checkout.
        if (booking is null ||
            (booking.Source != BookingSource.Direct && !HostPaymentLinks.IsHostPaymentLink(booking)) ||
            !CheckoutOutcomes.TokenMatches(booking.CheckoutTokenHash, token))
        {
            throw new NotFoundException("Checkout link does not match a booking")
            {
                Code = CheckoutOutcomeErrorCodes.LinkInvalid,
                MessageKey = "CheckoutLinkInvalid",
            };
        }

        return booking;
    }

    private HoldExpiryCutoff CutoffNow(int ttlMinutes) =>
        CheckoutHolds.CutoffAt(_clock.GetUtcNow().UtcDateTime, ttlMinutes);

    /// <summary>The outcome of the payment link of a host booking: the state of its balance, no expiry (it is not a hold).</summary>
    private static CheckoutOutcome HostLinkOutcome(Booking booking) =>
        new(
            booking.Id,
            HostPaymentLinks.StateOf(booking),
            booking.PaymentOption,
            booking.PropertyId,
            booking.Property.Slug,
            booking.Property.Name,
            RomeCalendar.DateInRome(booking.CheckInDate),
            RomeCalendar.DateInRome(booking.CheckOutDate),
            booking.NumberOfAdults,
            booking.NumberOfChildren,
            booking.TotalPrice,
            StayCurrency.ForProperty(booking.Property),
            null,
            null,
            BookingCodes.Format(booking.BookingCode));

    /// <summary>
    /// The guest pays the balance of a host booking with the PaymentIntent of its last link: 409 <c>checkout_payment_not_resumable</c> when
    /// the booking is cancelled, nothing is left to pay, a payment is in progress, or the PaymentIntent no longer pays exactly the balance (a
    /// receipt was recorded since the link: the host sends a new one).
    /// </summary>
    private async Task<CheckoutPaymentSession> ResumeHostLinkAsync(Booking booking, CancellationToken cancellationToken)
    {
        var state = HostPaymentLinks.StateOf(booking);
        if (state is not (CheckoutOutcomeState.AwaitingPayment or CheckoutOutcomeState.PaymentFailed))
            throw NotResumable();

        var payment = HostPaymentLinks.OpenLinkPayment(booking.Payments);
        var account = payment?.StripeAccountId ?? booking.Org.StripeConnectedAccountId;
        if (payment?.StripePaymentIntentId is not { } intentId || string.IsNullOrWhiteSpace(account))
            throw NotResumable();

        var intent = await stripeService.GetPaymentIntentAsync(intentId, account, cancellationToken);
        var balanceCents = (long)Math.Round(
            HostPaymentLinks.BalanceOf(booking, booking.Payments) * 100m, MidpointRounding.AwayFromZero);
        if (intent.Status is null || !PayableIntentStatuses.Contains(intent.Status) || intent.Amount != balanceCents)
        {
            logger.LogInformation(
                "Payment link of booking {BookingId}: payment not resumed, the intent is {IntentStatus} or no longer the balance",
                booking.Id,
                intent.Status ?? "unknown");
            throw NotResumable();
        }

        var publishableKey = configuration["Stripe:PublishableKey"] ?? string.Empty;
        return new CheckoutPaymentSession(booking.Id, booking.PaymentOption, intent.ClientSecret, null, publishableKey, account, null);
    }

    /// <summary>
    /// A cancelled intent belongs to an expired hold (the expiry job cancels it first); one that succeeded or is processing
    /// was already paid: the webhook will confirm the booking.
    /// </summary>
    private void EnsurePayable(Guid bookingId, string? intentStatus)
    {
        if (intentStatus is not null && PayableIntentStatuses.Contains(intentStatus))
            return;

        logger.LogInformation(
            "Checkout {BookingId}: payment not resumed, the intent is {IntentStatus}", bookingId, intentStatus ?? "unknown");
        throw intentStatus == "canceled" ? HoldExpired() : NotResumable();
    }

    private static DomainConflictException HoldExpired() =>
        new(CheckoutOutcomeErrorCodes.HoldExpired, "CheckoutHoldExpired");

    private static DomainConflictException NotResumable() =>
        new(CheckoutOutcomeErrorCodes.PaymentNotResumable, "CheckoutPaymentNotResumable");
}
