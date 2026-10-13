using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

/// <summary>
/// The Stripe calls of the payment of a service request (SP-15a, decision D2): a <b>direct charge</b> on the supplier's own
/// connected account (<c>Stripe-Account</c> header) with the platform commission as <c>application_fee_amount</c>. Requests go
/// through <see cref="IStripeClient"/>: the global client configured from <c>Stripe:SecretKey</c> at startup, or the one passed
/// in (tests check the path, the options and the headers on a mocked client).
/// </summary>
/// <remarks>
/// <para><b>This is the one place that sets <c>ApplicationFeeAmount</c></b> (<c>ApplicationFeeArchitectureTests</c>). The other
/// PaymentIntents of the application (guest bookings, deferred charges, rent) never carry a fee, which
/// <c>StripeServiceApplicationFeeTests</c> proves and which this class does not touch.</para>
/// <para>The fee is sent only when it is strictly between zero and the amount (<see cref="SupplierCommission.IsSendable"/>): a zero
/// is left out, never sent as an explicit <c>0</c> (A3-40, unverified against Stripe and possibly rejected on a direct charge),
/// and a fee that is not below the amount would be rejected by Stripe. A failed call is a <see cref="StripeException"/>: the API
/// answers 503 <c>payment_provider_error</c>. The client secret is never logged (ids of the PaymentIntent and of the account are).</para>
/// </remarks>
public class StripeSupplierPaymentGateway(
    ILogger<StripeSupplierPaymentGateway> logger,
    IStripeClient? stripeClient = null) : ISupplierPaymentGateway
{
    private const string PaymentIntentUnexpectedStateCode = "payment_intent_unexpected_state";

    private IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    public async Task<ServiceChargeIntent> CreateAsync(ServiceChargeIntentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectedAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        // The webhook of SP-15b routes on metadata.kind: it is set here so that no caller can forget it or send another one.
        var metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = ServiceCharges.Kind };

        var options = new PaymentIntentCreateOptions
        {
            Amount = request.AmountCents,
            Currency = request.Currency,
            Metadata = metadata,
            Description = request.Description,
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true },
        };

        // The platform commission of the direct charge (decision D2/D3). Left unset, not 0, when there is none to send (A3-40).
        if (request.ApplicationFeeCents is { } fee && SupplierCommission.IsSendable(fee, request.AmountCents))
            options.ApplicationFeeAmount = fee;

        var paymentIntent = await new PaymentIntentService(Client).CreateAsync(
            options,
            RequestOptionsFor(request.ConnectedAccountId, request.IdempotencyKey),
            cancellationToken);

        logger.LogInformation(
            "Service payment intent {PaymentIntentId} created on {AccountId} (fee sent: {FeeSent})",
            paymentIntent.Id,
            request.ConnectedAccountId,
            options.ApplicationFeeAmount is not null);
        return Map(paymentIntent, request.ConnectedAccountId);
    }

    public async Task<ServiceChargeIntent> GetAsync(
        string paymentIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedAccountId);

        var paymentIntent = await new PaymentIntentService(Client).GetAsync(
            paymentIntentId,
            options: null,
            RequestOptionsFor(connectedAccountId),
            cancellationToken);
        return Map(paymentIntent, connectedAccountId);
    }

    public async Task<ServiceChargeIntent> CancelAsync(
        string paymentIntentId,
        string connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        try
        {
            var canceled = await new PaymentIntentService(Client).CancelAsync(
                paymentIntentId,
                new PaymentIntentCancelOptions { CancellationReason = "abandoned" },
                RequestOptionsFor(connectedAccountId, idempotencyKey),
                cancellationToken);
            logger.LogInformation("Service payment intent {PaymentIntentId} canceled on {AccountId}", paymentIntentId, connectedAccountId);
            return Map(canceled, connectedAccountId);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
        {
            // Paid or in progress between the read and the cancellation: the caller must see what it really is.
            logger.LogInformation(
                "Service payment intent {PaymentIntentId} could not be canceled, it changed state meanwhile", paymentIntentId);
            return await GetAsync(paymentIntentId, connectedAccountId, cancellationToken);
        }
    }

    public async Task<ServiceChargeRefund> CreateRefundAsync(ServiceChargeRefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PaymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectedAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        // Direct charge: the refund is created on the supplier's account (Stripe-Account header) and paid from its balance. There is
        // no transfer to reverse. The commission goes back with the refund (all of it for a refund in full, in proportion for a
        // partial one) when the payment carried one; a payment without a commission has none to give back.
        var options = new RefundCreateOptions
        {
            PaymentIntent = request.PaymentIntentId,
            Amount = request.AmountCents,
            Metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = ServiceCharges.RefundKind },
        };
        if (request.RefundApplicationFee)
            options.RefundApplicationFee = true;

        var refund = await new RefundService(Client).CreateAsync(
            options,
            RequestOptionsFor(request.ConnectedAccountId, request.IdempotencyKey),
            cancellationToken);

        logger.LogInformation(
            "Service refund {RefundId} created for {PaymentIntentId} on {AccountId}: {Status} (commission refunded: {FeeRefunded})",
            refund.Id,
            request.PaymentIntentId,
            request.ConnectedAccountId,
            refund.Status,
            request.RefundApplicationFee);
        return MapRefund(refund);
    }

    public async Task<IReadOnlyList<ServiceChargeRefund>> ListRefundsAsync(
        string paymentIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectedAccountId);

        var refunds = new List<ServiceChargeRefund>();
        var options = new RefundListOptions { PaymentIntent = paymentIntentId, Limit = 100 };
        await foreach (var refund in new RefundService(Client)
                           .ListAutoPagingAsync(options, RequestOptionsFor(connectedAccountId), cancellationToken))
        {
            refunds.Add(MapRefund(refund));
        }

        return refunds;
    }

    /// <summary>A Stripe refund as CasaZen reads it; also used by the webhook handler for the <c>refund.*</c> events.</summary>
    public static ServiceChargeRefund MapRefund(Refund refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return new ServiceChargeRefund(
            refund.Id,
            refund.PaymentIntentId,
            refund.Amount,
            refund.Status,
            refund.FailureReason,
            refund.Metadata is null ? null : new Dictionary<string, string>(refund.Metadata));
    }

    private static ServiceChargeIntent Map(PaymentIntent paymentIntent, string connectedAccountId) => new(
        paymentIntent.Id,
        paymentIntent.Status ?? string.Empty,
        paymentIntent.Amount,
        paymentIntent.Currency ?? string.Empty,
        paymentIntent.ApplicationFeeAmount,
        paymentIntent.ClientSecret,
        connectedAccountId,
        paymentIntent.LastPaymentError?.Code,
        paymentIntent.AmountReceived);

    private static RequestOptions RequestOptionsFor(string connectedAccountId, string? idempotencyKey = null) => new()
    {
        StripeAccount = connectedAccountId,
        IdempotencyKey = idempotencyKey,
    };
}
