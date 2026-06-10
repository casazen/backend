using Stripe;
using Casazen.Core.Repositories;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class StripeWebhookHandler(
    IPaymentRepository paymentRepository,
    IConnectOnboardingService connectOnboardingService,
    ILogger<StripeWebhookHandler> logger)
{
    public async Task HandleEventAsync(Event stripeEvent)
    {
        try
        {
            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    await HandlePaymentSucceededAsync(stripeEvent.Data.Object as PaymentIntent);
                    break;
                case "payment_intent.payment_failed":
                    await HandlePaymentFailedAsync(stripeEvent.Data.Object as PaymentIntent);
                    break;
                case "charge.refunded":
                    await HandleRefundAsync(stripeEvent.Data.Object as Charge);
                    break;
                case "account.updated":
                    await HandleAccountUpdatedAsync(stripeEvent.Data.Object as Account);
                    break;
                default:
                    logger.LogInformation("Unhandled Stripe event: {EventType}", stripeEvent.Type);
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

    private async Task HandlePaymentSucceededAsync(PaymentIntent? paymentIntent)
    {
        if (paymentIntent == null)
            return;

        logger.LogInformation("Payment succeeded: {PaymentIntentId}", paymentIntent.Id);

        var transactionId = paymentIntent.Id;
        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment != null)
        {
            payment.Status = Core.Entities.PaymentStatus.Completed;
            await paymentRepository.UpdateAsync(payment);
        }
    }

    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent)
    {
        if (paymentIntent == null)
            return;

        logger.LogInformation("Payment failed: {PaymentIntentId}", paymentIntent.Id);

        var transactionId = paymentIntent.Id;
        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment != null)
        {
            payment.Status = Core.Entities.PaymentStatus.Failed;
            await paymentRepository.UpdateAsync(payment);
        }
    }

    private async Task HandleRefundAsync(Charge? charge)
    {
        if (charge == null)
            return;

        logger.LogInformation("Charge refunded: {ChargeId}", charge.Id);

        var transactionId = charge.PaymentIntentId;
        var payment = await paymentRepository.GetByTransactionIdAsync(transactionId);
        if (payment != null)
        {
            payment.Status = Core.Entities.PaymentStatus.Refunded;
            await paymentRepository.UpdateAsync(payment);
        }
    }
}