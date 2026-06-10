using Stripe;
using Casazen.Core.Repositories;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class StripeWebhookHandler(
    IPaymentRepository paymentRepository,
    IBookingRepository bookingRepository,
    IConnectOnboardingService connectOnboardingService,
    ILogger<StripeWebhookHandler> logger)
{
    private const string DirectBookingKind = "direct-booking";

    public Task HandleEventAsync(Event stripeEvent) =>
        HandleEventAsync(stripeEvent, WebhookSource.Platform);

    public async Task HandleEventAsync(Event stripeEvent, WebhookSource source)
    {
        try
        {
            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    await HandlePaymentSucceededAsync(stripeEvent.Data.Object as PaymentIntent, source);
                    break;
                case "payment_intent.payment_failed":
                case "payment_intent.canceled":
                    await HandlePaymentFailedAsync(stripeEvent.Data.Object as PaymentIntent, source);
                    break;
                case "charge.refunded":
                    if (source == WebhookSource.Platform)
                        await HandleRefundAsync(stripeEvent.Data.Object as Charge);
                    break;
                case "account.updated":
                    if (source == WebhookSource.Connected)
                        await HandleAccountUpdatedAsync(stripeEvent.Data.Object as Account);
                    break;
                default:
                    logger.LogInformation("Unhandled Stripe event: {EventType} (source={Source})", stripeEvent.Type, source);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Stripe webhook");
        }
    }

    private async Task HandleAccountUpdatedAsync(Account? account)
    {
        if (account is null)
            return;

        logger.LogInformation("Connect account updated: {AccountId}", account.Id);
        var snapshot = StripeConnectGateway.MapAccount(account);
        await connectOnboardingService.ApplyAccountUpdatedAsync(snapshot);
    }

    private async Task HandlePaymentSucceededAsync(PaymentIntent? paymentIntent, WebhookSource source)
    {
        if (paymentIntent is null)
            return;

        if (IsDirectBookingEvent(paymentIntent, source))
        {
            await HandleDirectBookingPaymentSucceededAsync(paymentIntent);
            return;
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment succeeded: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Completed);
    }

    private async Task HandleDirectBookingPaymentSucceededAsync(PaymentIntent paymentIntent)
    {
        logger.LogInformation("Direct booking payment succeeded: {PaymentIntentId}", paymentIntent.Id);

        var payment = await paymentRepository.GetByTransactionIdAsync(paymentIntent.Id);
        if (payment is null)
        {
            logger.LogWarning("No payment row for direct booking PI {PaymentIntentId}", paymentIntent.Id);
            return;
        }

        if (payment.Status == PaymentStatus.Completed)
            return;

        payment.Status = PaymentStatus.Completed;
        payment.ProcessedAt = DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;
        await paymentRepository.UpdateAsync(payment);

        var booking = await bookingRepository.GetByIdAsync(payment.BookingId);
        if (booking is null)
            return;

        if (booking.Status == BookingStatus.Confirmed)
            return;

        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = DateTime.UtcNow;
        await bookingRepository.UpdateAsync(booking);
    }

    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent, WebhookSource source)
    {
        if (paymentIntent is null)
            return;

        if (IsDirectBookingEvent(paymentIntent, source))
        {
            logger.LogInformation("Direct booking payment failed/canceled: {PaymentIntentId}", paymentIntent.Id);
            await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
            return;
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment failed: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
    }

    private async Task HandleRefundAsync(Charge? charge)
    {
        if (charge is null)
            return;

        logger.LogInformation("Charge refunded: {ChargeId}", charge.Id);
        var transactionId = charge.PaymentIntentId;
        if (string.IsNullOrEmpty(transactionId))
            return;

        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment is not null)
        {
            payment.Status = PaymentStatus.Refunded;
            await paymentRepository.UpdateAsync(payment);
        }
    }

    private static bool IsDirectBookingEvent(PaymentIntent paymentIntent, WebhookSource source)
    {
        if (source != WebhookSource.Connected)
            return false;

        paymentIntent.Metadata.TryGetValue("kind", out var kind);
        return string.Equals(kind, DirectBookingKind, StringComparison.Ordinal);
    }

    private async Task UpdatePaymentStatusAsync(string transactionId, PaymentStatus status)
    {
        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment is null)
            return;

        payment.Status = status;
        if (status == PaymentStatus.Completed)
            payment.ProcessedAt = DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;
        await paymentRepository.UpdateAsync(payment);
    }
}
