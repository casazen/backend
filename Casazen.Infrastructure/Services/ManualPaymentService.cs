using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Payments the host received outside CasaZen and records on a booking (SR-06b). See <see cref="IManualPaymentService"/>.
/// </summary>
/// <remarks>
/// <para><b>Exactly once.</b> The key the client sends is kept in <see cref="Payment.TransactionId"/> (<c>manual:{key}</c>), so no
/// column is added. The booking's advisory lock serializes the check of the key, the balance and the insert: two calls with the
/// same key write one payment, and two calls with different keys never take the booking past its total.</para>
/// <para><b>What it counts.</b> The balance is the total of the booking minus what came in
/// (<see cref="BookingListRules.PaidAmount"/>: collected payments, refunds taken off), the same figure the booking sheet shows.
/// Payments still open (a checkout waiting for the card) are not money yet and do not reduce it.</para>
/// <para><b>Its event.</b> A receipt is a payment received: <c>PaymentSucceeded</c> is recorded in the save that writes it (E08/F01/US01/T04),
/// once per payment, so the channel manager records the income of a mirrored booking.</para>
/// <para><b>The payment link.</b> A receipt changes the balance, so the open PaymentIntents of the payment link of a host booking
/// (<see cref="HostPaymentLinks"/>) are cancelled on Stripe, under the same booking lock, and their rows marked Canceled: a link page the guest
/// already has open can no longer collect the money a second time. A PaymentIntent that succeeded or is processing means the guest is paying
/// at that moment: the receipt waits (409 <c>payment_in_progress</c>).</para>
/// </remarks>
public sealed class ManualPaymentService(
    AppDbContext db,
    IFiscalRegimeService fiscalRegimeService,
    ILogger<ManualPaymentService> logger,
    TimeProvider? timeProvider = null,
    IDomainEventRecorder? domainEvents = null,
    IStripeService? stripeService = null) : IManualPaymentService
{
    /// <summary>The code of the refusal of a receipt while the guest is paying with the payment link.</summary>
    internal const string PaymentInProgressCode = "payment_in_progress";

    /// <summary>The Stripe code of a PaymentIntent that cannot be cancelled in its state (the guest paid meanwhile).</summary>
    private const string PaymentIntentUnexpectedStateCode = "payment_intent_unexpected_state";

    /// <summary>Prefix of <see cref="Payment.TransactionId"/> for a payment recorded by the host: the rest is the client's key.</summary>
    internal const string KeyPrefix = "manual:";

    /// <summary>Longest key accepted (the column holds 500 characters).</summary>
    internal const int MaxKeyLength = 100;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ManualPaymentResult> RecordAsync(ManualPaymentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var key = (command.IdempotencyKey ?? string.Empty).Trim();
        if (key.Length is 0 or > MaxKeyLength)
            throw new DomainRuleException("payment_idempotency_key_invalid", "PaymentIdempotencyKeyInvalid");

        // The host records money that arrived by hand: cash or a bank transfer. Cards and wallets go through Stripe.
        if (command.Method is not (PaymentMethod.CashOnArrival or PaymentMethod.BankTransfer))
            throw new DomainRuleException("payment_method_not_allowed", "PaymentMethodNotAllowed");

        if (command.Amount <= 0m || decimal.Round(command.Amount, 2) != command.Amount)
            throw new DomainRuleException("payment_amount_invalid", "PaymentAmountInvalid");

        var nowOffset = _clock.GetUtcNow();
        var now = nowOffset.UtcDateTime;
        var today = DateOnly.FromDateTime(RomeCalendar.TodayAt(nowOffset));
        var receivedOn = command.ReceivedOn ?? today;
        if (receivedOn > today)
            throw new DomainRuleException("payment_received_on_future", "PaymentReceivedOnFuture");

        var transactionId = KeyPrefix + key;
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.BookingPayment, command.BookingId.ToString("N")));

        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == command.BookingId, cancellationToken)
            ?? throw new NotFoundException("Booking not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var payments = await db.Payments
            .Where(p => p.BookingId == booking.Id)
            .ToListAsync(cancellationToken);

        // The same key again: the payment it wrote, nothing new. A key that belongs to a different record is a mistake of the client.
        var previous = payments.FirstOrDefault(p => p.TransactionId == transactionId);
        if (previous is not null)
        {
            if (previous.Amount != command.Amount || previous.Method != command.Method)
                throw new DomainConflictException("payment_idempotency_key_reused", "PaymentIdempotencyKeyReused");

            return new ManualPaymentResult(previous, false);
        }

        if (booking.Status == BookingStatus.Cancelled)
            throw new DomainRuleException("payment_booking_cancelled", "PaymentBookingCancelled");

        // A portal (Airbnb, Booking.com...) collects the money itself: the host does not record a receipt on those stays.
        if (FiscalCopy.IsOtaBookingSource(booking.Source))
            throw new DomainRuleException("payment_booking_from_portal", "PaymentBookingFromPortal");

        var paid = BookingListRules.PaidAmount(payments.Select(p => new BookingPaymentFact(p.Status, p.Amount, p.RefundedAmount)));
        var balance = Math.Max(0m, booking.TotalPrice - paid);
        if (command.Amount > balance)
        {
            throw new DomainRuleException(
                "payment_exceeds_balance",
                "PaymentExceedsBalance",
                balance.ToString("N2", CultureInfo.CurrentCulture));
        }

        // The receipt changes the balance: the open PaymentIntent of the payment link would collect it a second time from a page the guest
        // already has open. Cancelled on Stripe, under this lock, before the receipt is written.
        await CancelOpenLinkIntentsAsync(booking, payments, now, cancellationToken);

        // A row that waits for the host to collect it by hand (the "pay at the property" row of an on-site booking, or a record the old
        // form left Pending for good) has no Stripe side. The receipt closes it: the oldest one becomes the receipt, any other is
        // cancelled, so nothing is left "to collect" next to the money that came in. The rows of a payment link are the ones above.
        var openByHand = payments.Where(IsWaitingForTheHost).OrderBy(p => p.CreatedAt).ToList();
        var payment = openByHand.FirstOrDefault();
        var isNew = payment is null;
        payment ??= new Payment { BookingId = booking.Id, OrgId = booking.OrgId, CreatedAt = now };
        foreach (var duplicate in openByHand.Skip(1))
        {
            duplicate.Status = PaymentStatus.Canceled;
            duplicate.UpdatedAt = now;
        }

        var note = (command.Description ?? string.Empty).Trim();
        payment.Amount = command.Amount;
        payment.Method = command.Method;
        payment.Status = PaymentStatus.Completed;
        payment.ProcessedAt = SettledAt(receivedOn, now);
        payment.TransactionId = transactionId;
        if (note.Length > 0 || isNew)
            payment.Description = note.Length > 1000 ? note[..1000] : note;
        payment.UpdatedAt = now;
        await fiscalRegimeService.ApplyWithholdingOnCreateAsync(payment, booking, command.ApplyOtaWithholding, command.ManualWithholdingTax);

        if (isNew)
            db.Payments.Add(payment);

        // E08/F01/US01/T04: the money came in; the event is saved with the payment (the Octorate handler records it on the reservation).
        await PaymentEvents.RecordSucceededAsync(
            domainEvents, db, payment, await PaymentEvents.CurrencyOfAsync(db, booking, cancellationToken), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Payment {PaymentId} recorded by the host on booking {BookingId} ({Method})", payment.Id, booking.Id, payment.Method);
        return new ManualPaymentResult(payment, true);
    }

    /// <summary>
    /// Cancels on Stripe the open PaymentIntents of the payment link of a host booking (a PaymentIntent that is pending or failed: the guest can
    /// still pay it) and marks their rows Canceled, in the unit of work of the receipt; a PaymentIntent that Stripe already cancelled only has its
    /// row marked. Nothing for a booking that has no payment link.
    /// </summary>
    /// <exception cref="DomainConflictException">
    /// 409 <see cref="PaymentInProgressCode"/>: a PaymentIntent of the link succeeded or is processing (the guest is paying, or has just paid, and
    /// the webhook has not recorded it yet): the receipt would charge the same balance twice.
    /// </exception>
    private async Task CancelOpenLinkIntentsAsync(
        Booking booking,
        IReadOnlyCollection<Payment> payments,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!HostPaymentLinks.IsHostPaymentLink(booking))
            return;

        var open = payments
            .Where(p => !string.IsNullOrEmpty(p.StripePaymentIntentId) && p.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            .ToList();
        if (open.Count == 0)
            return;

        if (stripeService is null)
            throw new InvalidOperationException("The Stripe service is not available: the open payment link of the booking cannot be cancelled.");

        foreach (var payment in open)
        {
            var intentId = payment.StripePaymentIntentId!;
            var account = payment.StripeAccountId;
            if (string.IsNullOrWhiteSpace(account))
            {
                account = await db.Orgs
                    .AsNoTracking()
                    .Where(o => o.Id == booking.OrgId)
                    .Select(o => o.StripeConnectedAccountId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(account))
            {
                logger.LogWarning(
                    "Payment {PaymentId} of booking {BookingId} has a PaymentIntent but no Stripe account: it cannot be cancelled", payment.Id, booking.Id);
                continue;
            }

            var intent = await stripeService.GetPaymentIntentAsync(intentId, account, cancellationToken);
            if (intent.Status is "succeeded" or "processing")
                throw new DomainConflictException(PaymentInProgressCode, "PaymentInProgress");

            if (intent.Status != "canceled")
            {
                try
                {
                    await stripeService.CancelPaymentIntentAsync(intentId, account, $"manual-receipt:{booking.Id:N}:{intentId}", cancellationToken);
                }
                catch (Stripe.StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
                {
                    // The guest paid between the read and the cancellation: the receipt waits for the payment to be recorded.
                    throw new DomainConflictException(PaymentInProgressCode, "PaymentInProgress");
                }
            }

            payment.Status = PaymentStatus.Canceled;
            payment.UpdatedAt = now;
            logger.LogInformation(
                "Payment {PaymentId} of booking {BookingId}: its open PaymentIntent is cancelled, a receipt was recorded by the host", payment.Id, booking.Id);
        }
    }

    /// <summary>Pending, and neither a Stripe intent nor a transaction id: only the host can collect it.</summary>
    private static bool IsWaitingForTheHost(Payment payment) =>
        payment.Status == PaymentStatus.Pending
        && string.IsNullOrEmpty(payment.StripePaymentIntentId)
        && string.IsNullOrEmpty(payment.TransactionId);

    /// <summary>
    /// The instant a receipt of <paramref name="receivedOn"/> is settled at: midday in Rome of that day, so its Rome date is the
    /// one the host said whatever the offset, and never later than now (a receipt of today recorded before midday is settled now).
    /// </summary>
    private static DateTime SettledAt(DateOnly receivedOn, DateTime now)
    {
        var midday = RomeCalendar.ToUtc(receivedOn, new TimeOnly(12, 0));
        return midday < now ? midday : now;
    }
}
