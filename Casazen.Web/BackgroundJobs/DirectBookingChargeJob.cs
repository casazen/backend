using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public class DirectBookingChargeJob(
    AppDbContext context,
    IPaymentRepository paymentRepository,
    IStripeService stripeService,
    IOrgService orgService,
    ILogger<DirectBookingChargeJob> logger)
{
    private const string DeadlineChargeDescription = "Direct checkout - deferred payment (charged at deadline)";
    private const string LegacyDeadlineChargeDescription = "Direct booking - charged at deadline";

    public async Task ExecuteAsync()
    {
        var today = DateTime.UtcNow.Date;

        var pendingBookings = await context.Bookings
            .Where(b => b.PaymentOption == PaymentOption.OnCancellationDeadline &&
                        b.FreeRefundDeadline <= today &&
                        b.Status == BookingStatus.Confirmed &&
                        !context.Payments.Any(p =>
                            p.BookingId == b.Id &&
                            p.Status == PaymentStatus.Completed &&
                            (p.Description == DeadlineChargeDescription || p.Description == LegacyDeadlineChargeDescription)))
            .Include(b => b.Org)
            .Include(b => b.Property)
            .ToListAsync();

        logger.LogInformation("Direct booking charge job: {Count} booking(s) ready for deadline charging", pendingBookings.Count);

        foreach (var booking in pendingBookings)
        {
            await ChargeBookingAsync(booking);
        }
    }

    private async Task ChargeBookingAsync(Booking booking)
    {
        try
        {
            var existingPayments = (await paymentRepository.GetByBookingAsync(booking.Id)).ToList();
            if (HasCompletedDeadlineCharge(existingPayments))
            {
                logger.LogInformation("Booking {BookingId} already has a completed deadline charge", booking.Id);
                return;
            }

            if (string.IsNullOrWhiteSpace(booking.StripePaymentMethodId))
            {
                logger.LogWarning("Booking {BookingId} has no saved payment method", booking.Id);
                return;
            }

            var org = await orgService.GetByIdAsync(booking.OrgId);
            if (org?.StripeConnectedAccountId is null)
            {
                logger.LogWarning("Org {OrgId} not ready for charging", booking.OrgId);
                return;
            }

            var amountCents = (long)Math.Round(booking.TotalPrice * 100m, MidpointRounding.AwayFromZero);
            var metadata = new Dictionary<string, string>
            {
                ["bookingId"] = booking.Id.ToString(),
                ["propertyId"] = booking.PropertyId.ToString(),
                ["orgId"] = booking.OrgId.ToString(),
                ["kind"] = "direct-booking-deadline-charge",
            };

            var paymentIntent = await stripeService.ChargePaymentMethodAsync(
                org.StripeConnectedAccountId,
                booking.StripeCustomerId ?? string.Empty,
                booking.StripePaymentMethodId,
                amountCents,
                "eur",
                metadata,
                $"direct-booking-deadline:{booking.Id}");

            logger.LogInformation("Charged booking {BookingId}: {PaymentIntentId}", booking.Id, paymentIntent.Id);

            var existingPaymentIntent = await paymentRepository.GetByTransactionIdAsync(paymentIntent.Id);
            if (existingPaymentIntent is not null)
            {
                logger.LogInformation(
                    "Payment intent {PaymentIntentId} was already recorded for booking {BookingId}",
                    paymentIntent.Id,
                    booking.Id);
                return;
            }

            var deferredPayment = existingPayments
                .FirstOrDefault(p =>
                    p.Status == PaymentStatus.Pending &&
                    (p.TransactionId == booking.StripeSetupIntentId || p.Description == DeadlineChargeDescription));

            if (deferredPayment is not null)
            {
                deferredPayment.Status = PaymentStatus.Completed;
                deferredPayment.TransactionId = paymentIntent.Id;
                deferredPayment.StripePaymentIntentId = paymentIntent.Id;
                deferredPayment.ProcessedAt = DateTime.UtcNow;
                deferredPayment.UpdatedAt = DateTime.UtcNow;
                await paymentRepository.UpdateAsync(deferredPayment);
                return;
            }

            var payment = new Payment
            {
                BookingId = booking.Id,
                OrgId = booking.OrgId,
                Amount = booking.TotalPrice,
                Status = PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                TransactionId = paymentIntent.Id,
                StripePaymentIntentId = paymentIntent.Id,
                Description = DeadlineChargeDescription,
                ProcessedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await paymentRepository.AddAsync(payment);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error charging booking {BookingId} at deadline", booking.Id);
        }
    }

    private static bool HasCompletedDeadlineCharge(IEnumerable<Payment> payments) =>
        payments.Any(p =>
            p.Status == PaymentStatus.Completed &&
            (p.Description == DeadlineChargeDescription || p.Description == LegacyDeadlineChargeDescription));
}
