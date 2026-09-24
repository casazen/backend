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

    Task<PaymentIntent> GetPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a PaymentIntent that was not paid (the guest is never charged afterwards).</summary>
    Task<PaymentIntent> CancelPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<SetupIntent> GetSetupIntentAsync(
        string setupIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a SetupIntent that has not saved a card yet.</summary>
    Task<SetupIntent> CancelSetupIntentAsync(
        string setupIntentId,
        string connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

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

/// <summary>
/// Stripe calls of the booking payments. The intent reads and cancellations go through <see cref="IStripeClient"/>: the
/// global client configured from <c>Stripe:SecretKey</c> at startup, or the one passed in (tests check the path, the
/// options and the <c>Stripe-Account</c> / idempotency headers on a mocked client).
/// </summary>
public class StripeService(ILogger<StripeService> logger, IStripeClient? stripeClient = null) : IStripeService
{
    private IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

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

    public Task<PaymentIntent> GetPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default) =>
        new PaymentIntentService(Client).GetAsync(
            paymentIntentId,
            options: null,
            RequestOptionsFor(connectedAccountId),
            cancellationToken);

    public async Task<PaymentIntent> CancelPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var paymentIntent = await new PaymentIntentService(Client).CancelAsync(
            paymentIntentId,
            new PaymentIntentCancelOptions { CancellationReason = "abandoned" },
            RequestOptionsFor(connectedAccountId, idempotencyKey),
            cancellationToken);
        logger.LogInformation(
            "Payment intent {PaymentIntentId} canceled on {AccountId}",
            paymentIntentId,
            connectedAccountId ?? "platform");
        return paymentIntent;
    }

    public Task<SetupIntent> GetSetupIntentAsync(
        string setupIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default) =>
        new SetupIntentService(Client).GetAsync(
            setupIntentId,
            options: null,
            RequestOptionsFor(connectedAccountId),
            cancellationToken);

    public async Task<SetupIntent> CancelSetupIntentAsync(
        string setupIntentId,
        string connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var setupIntent = await new SetupIntentService(Client).CancelAsync(
            setupIntentId,
            new SetupIntentCancelOptions { CancellationReason = "abandoned" },
            RequestOptionsFor(connectedAccountId, idempotencyKey),
            cancellationToken);
        logger.LogInformation("Setup intent {SetupIntentId} canceled on {AccountId}", setupIntentId, connectedAccountId);
        return setupIntent;
    }

    private static RequestOptions RequestOptionsFor(string? connectedAccountId, string? idempotencyKey = null) => new()
    {
        StripeAccount = string.IsNullOrWhiteSpace(connectedAccountId) ? null : connectedAccountId,
        IdempotencyKey = idempotencyKey,
    };

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
