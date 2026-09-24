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
/// </remarks>
public sealed class CheckoutOutcomeService(
    AppDbContext db,
    IStripeService stripeService,
    IConfiguration configuration,
    ILogger<CheckoutOutcomeService> logger,
    TimeProvider? timeProvider = null) : ICheckoutOutcomeService
{
    private const string Currency = "EUR";

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
        var ttlMinutes = CheckoutHolds.GetTtlMinutes(configuration);
        var state = CheckoutOutcomes.StateOf(booking, CutoffNow(ttlMinutes));

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
            Currency,
            CheckoutOutcomes.ExpiresAt(booking, state, ttlMinutes),
            booking.PaymentOption == PaymentOption.OnCancellationDeadline && booking.FreeRefundDeadline is { } deadline
                ? RomeCalendar.DateInRome(deadline)
                : null);
    }

    public async Task<CheckoutPaymentSession> ResumePaymentAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var booking = await LoadAsync(bookingId, token, cancellationToken);
        var ttlMinutes = CheckoutHolds.GetTtlMinutes(configuration);
        var state = CheckoutOutcomes.StateOf(booking, CutoffNow(ttlMinutes));

        if (state == CheckoutOutcomeState.Expired)
            throw HoldExpired();
        if (state is not (CheckoutOutcomeState.AwaitingPayment or CheckoutOutcomeState.PaymentFailed))
            throw NotResumable();

        var publishableKey = configuration["Stripe:PublishableKey"] ?? string.Empty;
        var expiresAt = CheckoutOutcomes.ExpiresAt(booking, state, ttlMinutes)!.Value;

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

        if (booking is null ||
            booking.Source != BookingSource.Direct ||
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
