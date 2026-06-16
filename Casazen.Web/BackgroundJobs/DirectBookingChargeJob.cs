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
    public async Task ExecuteAsync()
    {
        var today = DateTime.UtcNow.Date;

        var pendingBookings = await context.Bookings
            .Where(b => b.PaymentOption == PaymentOption.OnCancellationDeadline &&
                        b.FreeRefundDeadline <= today &&
                        b.Status == BookingStatus.Confirmed)
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
                metadata);

            logger.LogInformation("Charged booking {BookingId}: {PaymentIntentId}", booking.Id, paymentIntent.Id);

            var payment = new Payment
            {
                BookingId = booking.Id,
                OrgId = booking.OrgId,
                Amount = booking.TotalPrice,
                Status = PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                TransactionId = paymentIntent.Id,
                StripePaymentIntentId = paymentIntent.Id,
                Description = "Direct booking - charged at deadline",
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
}
