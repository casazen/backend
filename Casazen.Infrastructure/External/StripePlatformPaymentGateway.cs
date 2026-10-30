using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

/// <summary>
/// The Stripe calls of the payment <b>held by CasaZen</b> (SP-15c-a, decision VT-D1): everything is on the <b>platform</b> account (no
/// <c>Stripe-Account</c> header) and <b>nothing carries an application fee</b>; the commission is the part CasaZen does not transfer.
/// Requests go through <see cref="IStripeClient"/> like <see cref="StripeSupplierPaymentGateway"/>, the other gateway of the supplier payments
/// (the one that keeps the direct charge on a supplier's account): the global client configured from <c>Stripe:SecretKey</c>, or the one passed
/// in (tests check the path, the options and the headers on a mocked client).
/// </summary>
/// <remarks>
/// <para><b>The card block.</b> A PaymentIntent with <c>capture_method = manual</c> and <c>payment_method_types = [card]</c>: a block only exists
/// for cards (VT-D22). It is charged with <see cref="CaptureAsync"/> and dropped with <see cref="CancelAsync"/>; both return the PaymentIntent
/// as it really is when Stripe says it is in another state (<c>payment_intent_unexpected_state</c>), so the caller never acts on a guess.</para>
/// <para><b>The release.</b> A <c>Transfer</c> with <c>source_transaction</c> (the charge of the payment) and the request's
/// <c>transfer_group</c>. The funds become the supplier's when the charge's funds are available (about six days in test mode): the release
/// does not make the supplier's balance available at once. Stripe does not retry a failed transfer; the release job does.</para>
/// <para>Ids go to the logs (PaymentIntent, charge, transfer, refund), never a client secret and never a personal datum.</para>
/// </remarks>
public class StripePlatformPaymentGateway(
    ILogger<StripePlatformPaymentGateway> logger,
    IStripeClient? stripeClient = null) : IPlatformPaymentGateway
{
    private const string PaymentIntentUnexpectedStateCode = "payment_intent_unexpected_state";

    private IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    public async Task<PlatformCharge> CreateAsync(PlatformChargeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TransferGroup);

        // The webhook routes on metadata.kind: it is set here so that no caller can forget it or send another one.
        var metadata = new Dictionary<string, string>(request.Metadata)
        {
            ["kind"] = ServiceCharges.Kind,
            [ServiceCharges.FundsMetadataKey] = ServiceCharges.HeldFunds,
        };

        var options = new PaymentIntentCreateOptions
        {
            Amount = request.AmountCents,
            Currency = request.Currency,
            Metadata = metadata,
            Description = request.Description,
            TransferGroup = request.TransferGroup,

            // Cards only: a block exists only for them, and the Payment Element still handles 3-D Secure (VT-D22).
            PaymentMethodTypes = ["card"],
        };

        // Block now, charge when the supplier accepts. Otherwise the default: charged as soon as the payer confirms.
        if (request.ManualCapture)
            options.CaptureMethod = "manual";

        var paymentIntent = await new PaymentIntentService(Client).CreateAsync(
            options,
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            cancellationToken);

        logger.LogInformation(
            "Held service payment intent {PaymentIntentId} created on the platform (manual capture: {ManualCapture})",
            paymentIntent.Id, request.ManualCapture);
        return Map(paymentIntent);
    }

    public async Task<PlatformCharge> GetAsync(string paymentIntentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        // The charge is expanded for capture_before, the moment the card network stops guaranteeing the block (informative).
        var paymentIntent = await new PaymentIntentService(Client).GetAsync(
            paymentIntentId,
            new PaymentIntentGetOptions { Expand = ["latest_charge"] },
            requestOptions: null,
            cancellationToken);
        return Map(paymentIntent);
    }

    public async Task<PlatformCharge> CaptureAsync(string paymentIntentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        try
        {
            // The whole block: no AmountToCapture (the amount of a held payment is the amount blocked, VT-D18).
            var captured = await new PaymentIntentService(Client).CaptureAsync(
                paymentIntentId,
                new PaymentIntentCaptureOptions(),
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            logger.LogInformation("Held service payment intent {PaymentIntentId} captured: {Status}", paymentIntentId, captured.Status);
            return Map(captured);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
        {
            // Already charged (a retry, another worker), canceled or expired: the caller must see what it really is.
            logger.LogInformation("Held service payment intent {PaymentIntentId} could not be captured, it is in another state", paymentIntentId);
            return await GetAsync(paymentIntentId, cancellationToken);
        }
    }

    public async Task<PlatformCharge> CancelAsync(string paymentIntentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        try
        {
            var canceled = await new PaymentIntentService(Client).CancelAsync(
                paymentIntentId,
                new PaymentIntentCancelOptions { CancellationReason = "abandoned" },
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            logger.LogInformation("Held service payment intent {PaymentIntentId} canceled", paymentIntentId);
            return Map(canceled);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
        {
            // Charged meanwhile (or already canceled): the caller must see what it really is, and give the money back if it was charged.
            logger.LogInformation("Held service payment intent {PaymentIntentId} could not be canceled, it changed state meanwhile", paymentIntentId);
            return await GetAsync(paymentIntentId, cancellationToken);
        }
    }

    public async Task<ServiceChargeRefund> CreateRefundAsync(PlatformRefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PaymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        // The refund leaves CasaZen's balance. There is no commission to give back with it: CasaZen sent none to Stripe.
        var refund = await new RefundService(Client).CreateAsync(
            new RefundCreateOptions
            {
                PaymentIntent = request.PaymentIntentId,
                Amount = request.AmountCents,
                Metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = ServiceCharges.RefundKind },
            },
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            cancellationToken);

        logger.LogInformation(
            "Held service refund {RefundId} created for {PaymentIntentId}: {Status}", refund.Id, request.PaymentIntentId, refund.Status);
        return StripeSupplierPaymentGateway.MapRefund(refund);
    }

    public async Task<IReadOnlyList<ServiceChargeRefund>> ListRefundsAsync(string paymentIntentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        var refunds = new List<ServiceChargeRefund>();
        await foreach (var refund in new RefundService(Client).ListAutoPagingAsync(
                           new RefundListOptions { PaymentIntent = paymentIntentId, Limit = 100 },
                           requestOptions: null,
                           cancellationToken))
        {
            refunds.Add(StripeSupplierPaymentGateway.MapRefund(refund));
        }

        return refunds;
    }

    public async Task<ServiceTransfer> CreateTransferAsync(ServiceTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceChargeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TransferGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        // source_transaction ties the transfer to the money of this payment: it works even while the charge is not settled, and Stripe
        // makes the funds the supplier's when the charge's become available. Without it the transfer would draw on the platform's
        // available balance and could fail for lack of it (VT-D22).
        var transfer = await new TransferService(Client).CreateAsync(
            new TransferCreateOptions
            {
                Amount = request.AmountCents,
                Currency = request.Currency,
                Destination = request.DestinationAccountId,
                SourceTransaction = request.SourceChargeId,
                TransferGroup = request.TransferGroup,
                Metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = ServiceCharges.TransferKind },
            },
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            cancellationToken);

        logger.LogInformation(
            "Service transfer {TransferId} of {AmountCents} cents created to {AccountId} from {ChargeId}",
            transfer.Id, request.AmountCents, request.DestinationAccountId, request.SourceChargeId);
        return MapTransfer(transfer);
    }

    public async Task<IReadOnlyList<ServiceTransfer>> ListTransfersAsync(string transferGroup, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferGroup);

        var transfers = new List<ServiceTransfer>();
        await foreach (var transfer in new TransferService(Client).ListAutoPagingAsync(
                           new TransferListOptions { TransferGroup = transferGroup, Limit = 100 },
                           requestOptions: null,
                           cancellationToken))
        {
            transfers.Add(MapTransfer(transfer));
        }

        return transfers;
    }

    /// <summary>A Stripe transfer as CasaZen reads it; also used by the webhook handler for the <c>transfer.*</c> events.</summary>
    public static ServiceTransfer MapTransfer(Transfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return new ServiceTransfer(
            transfer.Id,
            transfer.Amount,
            transfer.Currency ?? string.Empty,
            transfer.DestinationId,
            transfer.SourceTransactionId,
            transfer.TransferGroup,
            transfer.Reversed,
            transfer.AmountReversed,
            transfer.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(transfer.Metadata));
    }

    /// <summary>A Stripe PaymentIntent of the platform as CasaZen reads it; also used by the webhook handler for the <c>payment_intent.*</c> events.</summary>
    public static PlatformCharge Map(PaymentIntent paymentIntent)
    {
        ArgumentNullException.ThrowIfNull(paymentIntent);
        return new PlatformCharge(
            paymentIntent.Id,
            paymentIntent.Status ?? string.Empty,
            paymentIntent.Amount,
            paymentIntent.AmountCapturable,
            paymentIntent.AmountReceived,
            paymentIntent.Currency ?? string.Empty,
            paymentIntent.ApplicationFeeAmount,
            paymentIntent.ClientSecret,
            paymentIntent.LatestChargeId,
            paymentIntent.LatestCharge?.PaymentMethodDetails?.Card?.CaptureBefore,
            paymentIntent.TransferGroup,
            paymentIntent.LastPaymentError?.Code,
            paymentIntent.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(paymentIntent.Metadata));
    }
}
