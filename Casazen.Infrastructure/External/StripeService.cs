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
    Task<SetupIntent> CreateConnectedAccountSetupIntentAsync(
        string connectedAccountId,
        Dictionary<string, string> metadata,
        string? customerEmail = null,
        string? customerName = null);
    Task<PaymentIntent> ChargePaymentMethodAsync(
        string connectedAccountId,
        string customerId,
        string paymentMethodId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata,
        string? idempotencyKey = null);
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

    public async Task<SetupIntent> CreateConnectedAccountSetupIntentAsync(
        string connectedAccountId,
        Dictionary<string, string> metadata,
        string? customerEmail = null,
        string? customerName = null)
    {
        try
        {
            var requestOptions = new RequestOptions { StripeAccount = connectedAccountId };
            var customerId = await CreateConnectedAccountCustomerAsync(
                connectedAccountId,
                requestOptions,
                customerEmail,
                customerName,
                metadata);
            var options = new SetupIntentCreateOptions
            {
                Customer = customerId,
                Metadata = metadata,
                Usage = "off_session",
                AutomaticPaymentMethods = new SetupIntentAutomaticPaymentMethodsOptions
                {
                    Enabled = true,
                },
            };

            var service = new SetupIntentService();
            var setupIntent = await service.CreateAsync(options, requestOptions);
            logger.LogInformation(
                "Connected-account setup intent created: {SetupIntentId} on {AccountId}",
                setupIntent.Id,
                connectedAccountId);
            return setupIntent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating connected-account setup intent for {AccountId}", connectedAccountId);
            throw;
        }
    }

    private async Task<string> CreateConnectedAccountCustomerAsync(
        string connectedAccountId,
        RequestOptions requestOptions,
        string? customerEmail,
        string? customerName,
        Dictionary<string, string> metadata)
    {
        var options = new CustomerCreateOptions
        {
            Email = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim(),
            Name = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim(),
            Metadata = metadata,
        };

        var service = new CustomerService();
        var customer = await service.CreateAsync(options, requestOptions);
        logger.LogInformation(
            "Connected-account customer created: {CustomerId} on {AccountId}",
            customer.Id,
            connectedAccountId);
        return customer.Id;
    }

    public async Task<PaymentIntent> ChargePaymentMethodAsync(
        string connectedAccountId,
        string customerId,
        string paymentMethodId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata,
        string? idempotencyKey = null)
    {
        try
        {
            var options = new PaymentIntentCreateOptions
            {
                Amount = amountCents,
                Currency = currency,
                Customer = customerId,
                PaymentMethod = paymentMethodId,
                ConfirmationMethod = "automatic",
                Confirm = true,
                Metadata = metadata,
                ApplicationFeeAmount = 0,
            };

            var requestOptions = new RequestOptions
            {
                StripeAccount = connectedAccountId,
                IdempotencyKey = idempotencyKey,
            };
            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options, requestOptions);
            logger.LogInformation(
                "Off-session payment intent created: {PaymentIntentId} on {AccountId}",
                paymentIntent.Id,
                connectedAccountId);
            return paymentIntent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating off-session payment for {AccountId}", connectedAccountId);
            throw;
        }
    }
}
