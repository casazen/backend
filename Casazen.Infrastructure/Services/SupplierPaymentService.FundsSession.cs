using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The payer's session for the money CasaZen holds (SP-15c-b, <see cref="OpenHeldSessionAsync"/>): what a host pays for a completed job is a
/// PaymentIntent on the <b>platform</b>, charged at once (the work is done), carrying the transfer group of the request. Nothing is sent to
/// Stripe as a commission: CasaZen keeps its part when it transfers the rest. The session of a direct charge (SP-15a) is in
/// <c>.Sessions.cs</c> and is not changed; the model of a payment is fixed when it is created, so one or the other is chosen by reading it.
/// </summary>
public sealed partial class SupplierPaymentService
{
    /// <summary>
    /// The PaymentIntent of a payment held by CasaZen, created or reused under the payment lock of its request, as the session of
    /// <see cref="OpenSessionAsync"/> does for a direct charge: the payment is read again after the lock and the link (or the host) checked
    /// again; a PaymentIntent that is still payable and still the right amount is reused, one that is not is canceled and replaced; one that was
    /// paid or is in progress on Stripe is never replaced (the payment is shown as processing and the webhook settles it).
    /// </summary>
    private async Task<ServicePaymentSession> OpenHeldSessionAsync(
        Guid requestId,
        Guid paymentId,
        string? token,
        Guid? hostOrgId,
        CancellationToken cancellationToken)
    {
        ServicePaymentSession? session = null;
        DomainException? refusal = null;
        await using (var transaction = await LockAsync(requestId, cancellationToken))
        {
            var payment = await db.ServiceRequestPayments
                .Where(p => p.Id == paymentId && p.ServiceRequestId == requestId)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw (token is null ? PaymentNotFound() : LinkInvalid());
            await db.Entry(payment).ReloadAsync(cancellationToken);

            if (token is not null && !LinkIsUsable(payment, token, Now()))
                throw LinkInvalid();
            if (hostOrgId is { } host && payment.PayerOrgId != host)
                throw PaymentNotFound();
            if (payment.FundsModel != ServiceFundsModel.PlatformHeld)
                throw NotPayable();

            switch (payment.Status)
            {
                case ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded or ServicePaymentStatus.Released:
                    throw AlreadyPaid();
                case ServicePaymentStatus.Processing
                    or ServicePaymentStatus.NeedsReview
                    or ServicePaymentStatus.Authorized
                    or ServicePaymentStatus.Held
                    or ServicePaymentStatus.Disputed
                    or ServicePaymentStatus.TransferFailed:
                    throw InFlight();
                case ServicePaymentStatus.Canceled:
                    throw NotPayable();
            }

            // Requested or Failed: the supplier must still be able to be paid, or the money would be collected for nobody.
            var supplier = await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken);
            if (!supplier.CanReceivePayments)
                throw SupplierNotReady();

            var now = Now();
            PlatformCharge? intent = null;
            if (payment.StripePaymentIntentId is { } existingId)
            {
                var current = await ReadPlatformIntentAsync(existingId, cancellationToken);
                if (current is not null && ServiceCharges.IsPayable(current.Status))
                {
                    if (HeldIntentMatches(current, payment))
                    {
                        intent = current;
                    }
                    else
                    {
                        // The amount changed since (or it carries a fee it must not): that PaymentIntent is canceled and a new one made.
                        current = await Platform.CancelAsync(
                            existingId, ServiceCharges.CancellationIdempotencyKey(payment.Id, existingId), cancellationToken);
                    }
                }

                if (intent is null)
                {
                    if (current is null || current.Status == "canceled")
                    {
                        // Canceled, or gone: nothing to reuse and nothing the payer could still pay.
                        payment.StripePaymentIntentId = null;
                    }
                    else
                    {
                        // Paid or in progress on Stripe (a payment the webhook has not recorded yet): never a second PaymentIntent.
                        payment.Status = ServicePaymentStatus.Processing;
                        payment.UpdatedAt = now;
                        refusal = InFlight();
                    }
                }
            }

            if (refusal is null && intent is null)
            {
                payment.PaymentIntentCount++;
                var group = payment.TransferGroup ?? ServiceCharges.TransferGroup(payment.ServiceRequestId);
                intent = await Platform.CreateAsync(
                    new PlatformChargeRequest(
                        payment.AmountCents,
                        payment.Currency,
                        ManualCapture: false,
                        group,
                        new Dictionary<string, string>
                        {
                            [ServiceCharges.PaymentMetadataKey] = payment.Id.ToString(),
                            [ServiceCharges.RequestMetadataKey] = payment.ServiceRequestId.ToString(),
                            [ServiceCharges.SupplierMetadataKey] = payment.SupplierOrgId.ToString(),
                        },
                        ServiceCharges.CreationIdempotencyKey(payment.Id, payment.PaymentIntentCount),
                        $"Service request {payment.ServiceRequestId}"),
                    cancellationToken);
                payment.StripePaymentIntentId = intent.Id;
                payment.TransferGroup = group;
                payment.ConnectedAccountId = null;
                payment.UpdatedAt = now;
                logger.LogInformation(
                    "Held payment {PaymentId} of request {RequestId}: payment intent {PaymentIntentId} created on the platform",
                    payment.Id, payment.ServiceRequestId, intent.Id);
            }

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            if (refusal is null)
            {
                session = new ServicePaymentSession(
                    payment.Id,
                    intent!.ClientSecret ?? throw new PaymentProcessingException("Stripe returned a PaymentIntent without a client secret."),
                    configuration["Stripe:PublishableKey"] ?? string.Empty,
                    StripeAccountId: null,
                    payment.AmountCents,
                    payment.Currency.ToUpperInvariant());
            }
        }

        if (refusal is not null)
            throw refusal;

        return session!;
    }

    /// <summary>The PaymentIntent is still the right one: the amount and the currency of the payment, and no application fee.</summary>
    private static bool HeldIntentMatches(PlatformCharge current, ServiceRequestPayment payment) =>
        current.AmountCents == payment.AmountCents
        && string.Equals(current.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)
        && (current.ApplicationFeeCents ?? 0L) == 0L;
}
