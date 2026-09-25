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

    /// <summary>
    /// Creates a refund of a PaymentIntent (BK-02). Direct charges live on the connected account, so the request carries
    /// its <c>Stripe-Account</c> header (<see cref="StripeRefundCreateRequest.ConnectedAccountId"/>); null only for a
    /// PaymentIntent of the platform account. Always sent with the idempotency key of the refund row.
    /// </summary>
    Task<Refund> CreateRefundAsync(StripeRefundCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every refund of a PaymentIntent, on the connected account when <paramref name="connectedAccountId"/> is set.</summary>
    Task<IReadOnlyList<Refund>> ListRefundsAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default);

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

    /// <summary>Detaches a saved card from its customer, so it can no longer be charged off-session.</summary>
    Task DetachPaymentMethodAsync(
        string paymentMethodId,
        string connectedAccountId,
        CancellationToken cancellationToken = default);

    Task<SetupIntent> CreateConnectedAccountSetupIntentAsync(
        string connectedAccountId,
        Dictionary<string, string> metadata,
        string? customerEmail = null,
        string? customerName = null);

    /// <summary>
    /// Deferred charge (BK-08): creates and confirms <b>off-session</b> a PaymentIntent on the connected account with the
    /// customer's saved payment method (<c>off_session=true</c>, <c>confirm=true</c>). A payment that needs the guest
    /// (authentication required, card declined) makes Stripe answer 402: a <see cref="StripeException"/> whose
    /// <see cref="StripeError.PaymentIntent"/> is the PaymentIntent left in <c>requires_payment_method</c>.
    /// </summary>
    Task<PaymentIntent> ChargePaymentMethodAsync(
        string connectedAccountId,
        string customerId,
        string paymentMethodId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms again, off-session, a deferred charge PaymentIntent whose previous attempt failed (BK-08), with the saved
    /// payment method. Same 402 behaviour as <see cref="ChargePaymentMethodAsync"/>.
    /// </summary>
    Task<PaymentIntent> ConfirmPaymentIntentOffSessionAsync(
        string paymentIntentId,
        string connectedAccountId,
        string paymentMethodId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PaymentIntents of a customer of the connected account (most recent first, at most 100): finds a deferred charge
    /// created by an attempt whose answer was lost (timeout) before a new one is created (BK-08).
    /// </summary>
    Task<IReadOnlyList<PaymentIntent>> ListCustomerPaymentIntentsAsync(
        string customerId,
        string connectedAccountId,
        CancellationToken cancellationToken = default);
}

/// <summary>Refund of <paramref name="AmountCents"/> of <paramref name="PaymentIntentId"/>.</summary>
/// <param name="PaymentIntentId">PaymentIntent to refund.</param>
/// <param name="AmountCents">Amount in cents (EUR).</param>
/// <param name="ConnectedAccountId">Account the PaymentIntent was created on (direct charge); null for the platform.</param>
/// <param name="IdempotencyKey">Stable key of the refund row: a retry returns the same Stripe refund.</param>
/// <param name="Metadata">Ids that let the webhooks find the refund row (<c>paymentRefundId</c>, <c>paymentId</c>, …).</param>
public sealed record StripeRefundCreateRequest(
    string PaymentIntentId,
    string? ConnectedAccountId,
    long AmountCents,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Stripe calls of the booking payments. Requests go through <see cref="IStripeClient"/>: the global client configured
/// from <c>Stripe:SecretKey</c> at startup, or the one passed in (tests check the path, the options and the
/// <c>Stripe-Account</c> / idempotency headers on a mocked client).
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

            var service = new PaymentIntentService(Client);
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
            var service = new PaymentIntentService(Client);
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
            var service = new PaymentIntentService(Client);
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

    public async Task<Refund> CreateRefundAsync(StripeRefundCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        // Direct charge on the connected account: the refund is created there (Stripe-Account header) and paid from
        // that account's balance. No reverse_transfer (there is no transfer to reverse) and no refund_application_fee
        // (the checkout takes no application fee): see docs/runbooks/stripe.md "Refunds".
        var options = new RefundCreateOptions
        {
            PaymentIntent = request.PaymentIntentId,
            Amount = request.AmountCents,
            Metadata = new Dictionary<string, string>(request.Metadata),
        };

        var refund = await new RefundService(Client).CreateAsync(
            options,
            RequestOptionsFor(request.ConnectedAccountId, request.IdempotencyKey),
            cancellationToken);
        logger.LogInformation(
            "Refund {RefundId} created for {PaymentIntentId} on {AccountId}: {Status}",
            refund.Id,
            request.PaymentIntentId,
            request.ConnectedAccountId ?? "platform",
            refund.Status);
        return refund;
    }

    public async Task<IReadOnlyList<Refund>> ListRefundsAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        var refunds = new List<Refund>();
        var options = new RefundListOptions { PaymentIntent = paymentIntentId, Limit = 100 };
        await foreach (var refund in new RefundService(Client)
                           .ListAutoPagingAsync(options, RequestOptionsFor(connectedAccountId), cancellationToken))
        {
            refunds.Add(refund);
        }

        return refunds;
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

    public async Task DetachPaymentMethodAsync(
        string paymentMethodId,
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        await new PaymentMethodService(Client).DetachAsync(
            paymentMethodId,
            options: null,
            RequestOptionsFor(connectedAccountId),
            cancellationToken);
        logger.LogInformation("Payment method {PaymentMethodId} detached on {AccountId}", paymentMethodId, connectedAccountId);
    }

    private static RequestOptions RequestOptionsFor(string? connectedAccountId, string? idempotencyKey = null) => new()
    {
        StripeAccount = string.IsNullOrWhiteSpace(connectedAccountId) ? null : connectedAccountId,
        IdempotencyKey = idempotencyKey,
    };

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

            var service = new SetupIntentService(Client);
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

        var service = new CustomerService(Client);
        var customer = await service.CreateAsync(options, requestOptions);
        logger.LogInformation(
            "Connected-account customer created: {CustomerId} on {AccountId}",
            customer.Id,
            connectedAccountId);
        return customer.Id;
    }

    /// <remarks>
    /// Parameters (Stripe.net 50.1, API 2025-12-15.clover; checked in the Stripe docs, see docs/runbooks/stripe.md
    /// "Deferred charge"):
    /// <list type="bullet">
    /// <item><c>off_session=true</c> with <c>confirm=true</c>: the guest is not in the checkout. When the issuer asks for
    /// authentication Stripe does not leave the PaymentIntent in <c>requires_action</c>: it fails the attempt (402,
    /// <c>authentication_required</c>, status <c>requires_payment_method</c>) and the guest completes it on-session from
    /// the link of the email, with the same PaymentIntent.</item>
    /// <item>No <c>error_on_requires_action</c>: since API 2023-08-16 PaymentIntents use automatic payment methods by
    /// default, and Stripe accepts that flag only with explicit <c>payment_method_types</c>; listing them (e.g. only
    /// <c>card</c>) would refuse the other methods the SetupIntent may have saved (SEPA Debit). With
    /// <c>off_session=true</c> it adds nothing: an authentication request already fails the attempt. A
    /// <c>requires_action</c> answer is still handled as "the guest must act".</item>
    /// <item>No <c>return_url</c>: Stripe requires it on confirmation only when <c>off_session</c> is not true (redirect
    /// methods); nobody is redirected off-session.</item>
    /// </list>
    /// </remarks>
    public async Task<PaymentIntent> ChargePaymentMethodAsync(
        string connectedAccountId,
        string customerId,
        string paymentMethodId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var options = new PaymentIntentCreateOptions
        {
            Amount = amountCents,
            Currency = currency,
            Customer = customerId,
            PaymentMethod = paymentMethodId,
            ConfirmationMethod = "automatic",
            Confirm = true,
            OffSession = true,
            Metadata = metadata,
            ApplicationFeeAmount = 0,
        };

        var paymentIntent = await new PaymentIntentService(Client).CreateAsync(
            options,
            RequestOptionsFor(connectedAccountId, idempotencyKey),
            cancellationToken);
        logger.LogInformation(
            "Off-session payment intent {PaymentIntentId} created on {AccountId}: {Status}",
            paymentIntent.Id,
            connectedAccountId,
            paymentIntent.Status);
        return paymentIntent;
    }

    public async Task<PaymentIntent> ConfirmPaymentIntentOffSessionAsync(
        string paymentIntentId,
        string connectedAccountId,
        string paymentMethodId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // Same parameters as the creation (see ChargePaymentMethodAsync): off-session, no return_url, no
        // error_on_requires_action.
        var paymentIntent = await new PaymentIntentService(Client).ConfirmAsync(
            paymentIntentId,
            new PaymentIntentConfirmOptions { PaymentMethod = paymentMethodId, OffSession = true },
            RequestOptionsFor(connectedAccountId, idempotencyKey),
            cancellationToken);
        logger.LogInformation(
            "Off-session payment intent {PaymentIntentId} confirmed again on {AccountId}: {Status}",
            paymentIntent.Id,
            connectedAccountId,
            paymentIntent.Status);
        return paymentIntent;
    }

    public async Task<IReadOnlyList<PaymentIntent>> ListCustomerPaymentIntentsAsync(
        string customerId,
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedAccountId);

        var list = await new PaymentIntentService(Client).ListAsync(
            new PaymentIntentListOptions { Customer = customerId, Limit = 100 },
            RequestOptionsFor(connectedAccountId),
            cancellationToken);
        return list.Data;
    }
}
