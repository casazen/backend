using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
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
/// </remarks>
public sealed class ManualPaymentService(
    AppDbContext db,
    IFiscalRegimeService fiscalRegimeService,
    ILogger<ManualPaymentService> logger,
    TimeProvider? timeProvider = null) : IManualPaymentService
{
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

        var note = (command.Description ?? string.Empty).Trim();
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = command.Amount,
            Method = command.Method,
            Status = PaymentStatus.Completed,
            ProcessedAt = SettledAt(receivedOn, now),
            TransactionId = transactionId,
            Description = note.Length > 1000 ? note[..1000] : note,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await fiscalRegimeService.ApplyWithholdingOnCreateAsync(payment, booking, command.ApplyOtaWithholding, command.ManualWithholdingTax);

        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Payment {PaymentId} recorded by the host on booking {BookingId} ({Method})", payment.Id, booking.Id, payment.Method);
        return new ManualPaymentResult(payment, true);
    }

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
