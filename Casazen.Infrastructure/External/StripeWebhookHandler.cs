using Stripe;
using Casazen.Core.Repositories;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.External;

public class StripeWebhookHandler(
    IPaymentRepository paymentRepository,
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

    private async Task HandlePaymentSucceededAsync(PaymentIntent? paymentIntent)
    {
        if (paymentIntent == null)
            return;

        logger.LogInformation("Payment succeeded: {PaymentIntentId}", paymentIntent.Id);
        // Update payment status in database
    }

    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent)
    {
        if (paymentIntent == null)
            return;

        logger.LogInformation("Payment failed: {PaymentIntentId}", paymentIntent.Id);
        // Update payment status in database
    }

    private async Task HandleRefundAsync(Charge? charge)
    {
        if (charge == null)
            return;

        logger.LogInformation("Charge refunded: {ChargeId}", charge.Id);
        // Update payment status in database
    }
}