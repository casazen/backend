using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The card of the offer a customer accepts on a request on measure (SP-10b-b, <see cref="ISupplierFundsService.OpenOfferPaymentAsync"/>): the payment
/// of the request is born when the customer accepts, with the PaymentIntent that blocks its card for the total of the offer; the request is taken when
/// the card is blocked, by the same capture-then-take that a supplier's acceptance of a booking uses (<c>.Funds.cs</c>).
/// </summary>
/// <remarks>
/// <para><b>One live payment per request, and it is the offer's.</b> The unique index allows a single payment that is not canceled for a request, so
/// the payment of an offer the supplier replaced is dropped (its PaymentIntent canceled) before the next is made; and a payment is the offer's only
/// if it is for the same total and was created after the supplier sent the offer (<see cref="ServiceRequestOfferRules.IsPaymentOfOffer"/>), so a card
/// blocked for an offer that is no longer the standing one is never charged.</para>
/// <para><b>Locks.</b> The payment lock of the request only (the Stripe calls happen under it, as everywhere in the held cycle); the caller does not hold
/// the supplier's calendar lock while a PaymentIntent is created. The request is taken later, by the calendar lock first and the payment lock second, the
/// order of every take.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    public async Task<OfferPaymentSession?> OpenOfferPaymentAsync(
        ServiceRequest request,
        ServiceRequestOffer offer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentOutOfRangeException.ThrowIfLessThan(offer.TotalCents, 1);
        if (request is not { RentalContext: ServiceRequestRentalContext.Showcase, Kind: ServiceRequestKind.Custom })
            throw new ArgumentException("Only the offer of a request on measure from a showcase is paid this way.", nameof(request));

        await using var transaction = await LockAsync(request.Id, cancellationToken);
        var now = Now();

        // Read after the lock: whatever the previous holder committed is seen.
        var payment = await db.ServiceRequestPayments
            .Where(p => p.ServiceRequestId == request.Id && p.Status != ServicePaymentStatus.Canceled)
            .FirstOrDefaultAsync(cancellationToken);
        if (payment is not null)
            await db.Entry(payment).ReloadAsync(cancellationToken);

        if (payment is not null)
        {
            // Money that is not a block waiting for a card (an offer already charged and kept, a refund, a review) is not for this call to touch.
            if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld, PayerKind: ServicePayerKind.Private }
                || payment.Status is not (ServicePaymentStatus.Requested or ServicePaymentStatus.Failed or ServicePaymentStatus.Authorized or ServicePaymentStatus.Held))
            {
                throw InFlight();
            }

            var ofThisOffer = ServiceRequestOfferRules.IsPaymentOfOffer(payment.CreatedAt, payment.AmountCents, offer.SentAt, offer.TotalCents);
            if (ofThisOffer && payment.Status is ServicePaymentStatus.Authorized or ServicePaymentStatus.Held)
                return null;

            // Asked again for the offer it was made for: the same PaymentIntent, while it is still the right one and can still be paid.
            if (ofThisOffer && payment.StripePaymentIntentId is { } intentId)
            {
                var current = await ReadPlatformIntentAsync(intentId, cancellationToken);

                // The card is blocked on Stripe and the event that says so never came (or has not yet): the block is recorded now, with the checks
                // of the webhook, and the caller takes the request. A customer who paid is never asked to pay again because an event was late.
                if (current is not null
                    && ServiceCharges.IsCapturable(current.Status)
                    && HeldChargeVerification.AuthorizationMismatches(ExpectationOf(payment), ObservationOf(current)).Count == 0)
                {
                    payment.Status = ServicePaymentStatus.Authorized;
                    payment.AuthorizedAt = now;
                    payment.AuthorizationExpiresAt = HeldPaymentRules.AuthorizationExpiry(now, options.Value.AuthorizationValidity);
                    payment.FailureCode = null;
                    payment.UpdatedAt = now;
                    await SaveAsync(cancellationToken);
                    await CommitAsync(transaction, cancellationToken);
                    return null;
                }

                if (current is not null
                    && ServiceCharges.IsPayable(current.Status)
                    && HeldIntentMatches(current, payment)
                    && !string.IsNullOrWhiteSpace(current.ClientSecret))
                {
                    return ToOfferSession(current);
                }
            }

            // Of an earlier offer, or a PaymentIntent that cannot be paid anymore: dropped, and the next is made below. A block that was charged
            // meanwhile is not dropped silently (the money is held; an admin looks at it), nor is one Stripe did not cancel.
            if (payment.Status == ServicePaymentStatus.Held)
                throw InFlight();

            var outcome = payment.StripePaymentIntentId is null
                ? DropWithoutIntent(payment, now)
                : await TryCancelAuthorizationAsync(payment, now, cancellationToken);
            await SaveAsync(cancellationToken);
            if (outcome != AuthorizationOutcome.Canceled)
            {
                await CommitAsync(transaction, cancellationToken);
                logger.LogError(
                    "Payment {PaymentId} of request {RequestId} was made for an earlier offer and could not be dropped ({Outcome}); an admin has to look at it",
                    payment.Id, request.Id, outcome);
                throw InFlight();
            }
        }

        var percent = await ReadCommissionPercentAsync(request.SupplierOrgId, cancellationToken);
        var fee = SupplierCommission.FeeCents(offer.TotalCents, percent);
        var created = new ServiceRequestPayment
        {
            ServiceRequestId = request.Id,
            SupplierOrgId = request.SupplierOrgId,
            PayerKind = ServicePayerKind.Private,
            PayerOrgId = null,
            AmountCents = offer.TotalCents,
            Currency = ServiceCharges.Currency,
            CommissionPercent = percent,

            // The commission CasaZen keeps: nothing is sent to Stripe as a fee, it is the part that is not transferred.
            ApplicationFeeCents = fee,
            NetCents = SupplierCommission.NetCents(offer.TotalCents, fee),
            Status = ServicePaymentStatus.Requested,
            FundsModel = ServiceFundsModel.PlatformHeld,
            ConnectedAccountId = null,
            PaymentIntentCount = 1,
            TransferGroup = ServiceCharges.TransferGroup(request.Id),
            LineItemsJson = ServiceRequestJson.Serialize(ServiceRequestQuoteRules.AsPriceLines(offer.Lines)),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // The PaymentIntent is created before the row is saved: a Stripe failure leaves nothing behind (the customer is told to try again), and a
        // failure of the save leaves an orphan PaymentIntent nobody can pay (it has no client secret anywhere) that Stripe lets expire.
        var charge = await Platform.CreateAsync(
            new PlatformChargeRequest(
                created.AmountCents,
                created.Currency,
                ManualCapture: true,
                created.TransferGroup!,
                new Dictionary<string, string>
                {
                    [ServiceCharges.PaymentMetadataKey] = created.Id.ToString(),
                    [ServiceCharges.RequestMetadataKey] = request.Id.ToString(),
                    [ServiceCharges.SupplierMetadataKey] = request.SupplierOrgId.ToString(),
                },
                ServiceCharges.CreationIdempotencyKey(created.Id, 1),
                "Servizio su misura"),
            cancellationToken);
        created.StripePaymentIntentId = charge.Id;
        db.ServiceRequestPayments.Add(created);
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation(
            "Request {RequestId}: PaymentIntent {PaymentIntentId} created to block {AmountCents} cents on the customer's card for the accepted offer",
            request.Id, charge.Id, created.AmountCents);
        return ToOfferSession(charge);
    }

    public async Task CancelOfferPaymentAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await FindLatestHeldAsync(requestId, cancellationToken);
            if (latest is not { } known || known.Status == ServicePaymentStatus.Canceled)
                return;

            var refundAll = false;
            await using (var transaction = await LockAsync(requestId, cancellationToken))
            {
                var payment = await LoadPaymentAsync(known.Id, cancellationToken);

                // Only the card of an offer: a block or a PaymentIntent still waiting for a card. Money that was charged belongs to a request that
                // was taken (or to a review), and is settled by the road of its own.
                if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld, PayerKind: ServicePayerKind.Private }
                    || payment.Status is not (ServicePaymentStatus.Requested or ServicePaymentStatus.Failed or ServicePaymentStatus.Authorized))
                {
                    return;
                }

                var now = Now();
                var outcome = payment.StripePaymentIntentId is null
                    ? DropWithoutIntent(payment, now)
                    : await TryCancelAuthorizationAsync(payment, now, cancellationToken);
                await SaveAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);

                if (outcome == AuthorizationOutcome.Canceled)
                {
                    logger.LogInformation("Payment {PaymentId}: the offer ended, the card was released", payment.Id);
                    return;
                }

                // Charged meanwhile: the money is held now, and goes back in full below.
                refundAll = outcome == AuthorizationOutcome.Captured;
            }

            if (refundAll)
            {
                await RefundHeldAsync(
                    known.Id,
                    amountCents: null,
                    reason: "offer_ended",
                    ServicePaymentRefundOrigin.System,
                    actorUserId: null,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The offer is over already and must stay so: the funds job releases the card on its next run if this cannot.
            DetachPendingPaymentChanges();
            logger.LogError(ex, "The payment of the offer of request {RequestId} could not be dropped now; the funds job will try again", requestId);
        }
    }

    /// <summary>A payment with no PaymentIntent (nothing was ever asked of Stripe) is dropped as it is.</summary>
    private static AuthorizationOutcome DropWithoutIntent(ServiceRequestPayment payment, DateTime now)
    {
        payment.Status = ServicePaymentStatus.Canceled;
        payment.CanceledAt = now;
        payment.UpdatedAt = now;
        return AuthorizationOutcome.Canceled;
    }

    private OfferPaymentSession ToOfferSession(PlatformCharge charge) =>
        new(
            charge.ClientSecret ?? throw new PaymentProcessingException("Stripe returned a PaymentIntent without a client secret."),
            configuration["Stripe:PublishableKey"] ?? string.Empty,
            (int)Math.Min(charge.AmountCents, int.MaxValue),
            charge.Currency.ToUpperInvariant());
}
