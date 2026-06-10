using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

public interface IStripeService
{
    Task<PaymentIntent> CreatePaymentIntentAsync(long amount, string currency, Dictionary<string, string> metadata);
    Task<PaymentIntent> CreateConnectedAccountPaymentIntentAsync(
        string connectedAccountId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata);
    Task<PaymentIntent> ConfirmPaymentAsync(string paymentIntentId);
    Task<Refund> RefundPaymentAsync(string paymentIntentId, long? amount = null);
}

public class StripeService(ILogger<StripeService> logger) : IStripeService
{
    public async Task<PaymentIntent> CreatePaymentIntentAsync(long amount, string currency, Dictionary<string, string> metadata)
    {
        try
        {
            var options = new PaymentIntentCreateOptions
            {
                Amount = amount,
                Currency = currency,
                Metadata = metadata
            };

            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options);
            logger.LogInformation("Payment intent created: {PaymentIntentId}", paymentIntent.Id);
            return paymentIntent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating payment intent");
            throw;
        }
    }

    public async Task<PaymentIntent> CreateConnectedAccountPaymentIntentAsync(
        string connectedAccountId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata)
    {
        try
        {
            var options = new PaymentIntentCreateOptions
            {
                Amount = amountCents,
                Currency = currency,
                Metadata = metadata,
                ApplicationFeeAmount = 0,
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                {
                    Enabled = true,
                },
            };

            var requestOptions = new RequestOptions { StripeAccount = connectedAccountId };
            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options, requestOptions);
            logger.LogInformation(
                "Connected-account payment intent created: {PaymentIntentId} on {AccountId}",
                paymentIntent.Id,
                connectedAccountId);
            return paymentIntent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating connected-account payment intent for {AccountId}", connectedAccountId);
            throw;
        }
    }

    public async Task<PaymentIntent> ConfirmPaymentAsync(string paymentIntentId)
    {
        try
        {
            var service = new PaymentIntentService();
            var paymentIntent = await service.ConfirmAsync(paymentIntentId);
            logger.LogInformation("Payment confirmed: {PaymentIntentId}", paymentIntentId);
            return paymentIntent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error confirming payment");
            throw;
        }
    }

    public async Task<Refund> RefundPaymentAsync(string paymentIntentId, long? amount = null)
    {
        try
        {
            var options = new RefundCreateOptions
            {
                PaymentIntent = paymentIntentId,
                Amount = amount
            };

            var service = new RefundService();
            var refund = await service.CreateAsync(options);
            logger.LogInformation("Refund created: {RefundId}", refund.Id);
            return refund;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refunding payment");
            throw;
        }
    }
}
