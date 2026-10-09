using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>The payer's side of the payment: the page of the link and the Stripe session, for the anonymous payer and for the signed-in host.</summary>
public sealed partial class SupplierPaymentService
{
    // ─── The page of the link (anonymous) ────────────────────────────────────────────────────────────────────────────

    public async Task<PublicServicePayment> GetPublicAsync(Guid paymentId, string token, CancellationToken cancellationToken = default)
    {
        var payment = await FindByLinkAsync(paymentId, token, cancellationToken);
        var context = await ReadContextAsync(payment, cancellationToken);
        var supplier = await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken);

        var state = payment.Status switch
        {
            ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded => PublicServicePaymentState.Paid,
            ServicePaymentStatus.Processing or ServicePaymentStatus.NeedsReview => PublicServicePaymentState.Processing,
            ServicePaymentStatus.Requested or ServicePaymentStatus.Failed when supplier.CanReceivePayments => PublicServicePaymentState.Payable,
            _ => PublicServicePaymentState.Unavailable,
        };

        return new PublicServicePayment(
            payment.Id,
            context.SupplierName,
            context.ServiceName,
            context.PropertyName,
            context.CompletedAt,
            payment.AmountCents,
            payment.Currency.ToUpperInvariant(),
            ServiceRequestJson.ReadPriceLines(payment.LineItemsJson),
            state,
            payment.Status == ServicePaymentStatus.Failed,
            state == PublicServicePaymentState.Payable ? ValidUntil(payment) : null);
    }

    public async Task<ServicePaymentSession> CreatePublicSessionAsync(
        Guid paymentId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var found = await FindByLinkAsync(paymentId, token, cancellationToken);
        return await OpenSessionAsync(found.ServiceRequestId, found.Id, token, hostOrgId: null, cancellationToken);
    }

    public async Task<ServicePaymentSession> CreateHostSessionAsync(
        Guid requestId,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        // The live payment of the request that is this host's: any other (a request of another org, one without a payment) is a 404.
        var found = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.ServiceRequestId == requestId && p.PayerOrgId == hostOrgId && p.Status != ServicePaymentStatus.Canceled)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PaymentNotFound();

        return await OpenSessionAsync(found.ServiceRequestId, found.Id, token: null, hostOrgId, cancellationToken);
    }

    /// <summary>The payment a link belongs to, read without tracking; a wrong id, a wrong token and an expired link are the same 404.</summary>
    private async Task<ServiceRequestPayment> FindByLinkAsync(Guid paymentId, string token, CancellationToken cancellationToken)
    {
        // Anonymous: the token is the only access check. The payment is read by its id alone, then the token decides.
        var payment = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Id == paymentId)
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null || !LinkIsUsable(payment, token, Now()))
            throw LinkInvalid();

        return payment;
    }

    // ─── The session ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The PaymentIntent of a payment, created or reused under the payment lock of its request. The payment is read again after the
    /// lock and the link (or the host) checked again: the webhook, the supplier or another session may have changed it meanwhile.
    /// </summary>
    private async Task<ServicePaymentSession> OpenSessionAsync(
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

            switch (payment.Status)
            {
                case ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded:
                    throw AlreadyPaid();
                case ServicePaymentStatus.Processing or ServicePaymentStatus.NeedsReview:
                    throw InFlight();
                case ServicePaymentStatus.Canceled:
                    throw NotPayable();
            }

            // Requested or Failed: the supplier must still be able to take the payment.
            var supplier = await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken);
            if (!supplier.CanReceivePayments)
                throw SupplierNotReady();

            var account = supplier.AccountId!;
            long? fee = payment.ApplicationFeeCents > 0 ? payment.ApplicationFeeCents : null;
            var now = Now();

            ServiceChargeIntent? intent = null;
            if (payment.StripePaymentIntentId is { } existingId)
            {
                var existingAccount = payment.ConnectedAccountId ?? account;
                var current = await ReadIntentAsync(existingId, existingAccount, cancellationToken);
                if (current is not null && ServiceCharges.IsPayable(current.Status))
                {
                    if (Matches(current, payment, fee, existingAccount, account))
                    {
                        intent = current;
                    }
                    else
                    {
                        // The amount, the commission or the account changed since: that PaymentIntent is canceled and a new one made.
                        current = await gateway.CancelAsync(
                            existingId, existingAccount, ServiceCharges.CancellationIdempotencyKey(payment.Id, existingId), cancellationToken);
                    }
                }

                if (intent is null)
                {
                    if (current is null || current.Status == "canceled")
                    {
                        // Canceled, or gone with its account: nothing to reuse and nothing the payer could still pay.
                        payment.StripePaymentIntentId = null;
                    }
                    else
                    {
                        // Paid or in progress on Stripe (a payment the webhook has not recorded yet): never a second PaymentIntent.
                        // The payment is shown as processing; the webhook of SP-15b settles it after checking account, amount and fee.
                        payment.Status = ServicePaymentStatus.Processing;
                        payment.UpdatedAt = now;
                        refusal = InFlight();
                    }
                }
            }

            if (refusal is null && intent is null)
            {
                payment.PaymentIntentCount++;
                intent = await gateway.CreateAsync(
                    new ServiceChargeIntentRequest(
                        account,
                        payment.AmountCents,
                        payment.Currency,
                        fee,
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
                payment.ConnectedAccountId = account;
                payment.UpdatedAt = now;
                logger.LogInformation(
                    "Payment {PaymentId} of request {RequestId}: payment intent {PaymentIntentId} created on {AccountId}",
                    payment.Id, payment.ServiceRequestId, intent.Id, account);
            }

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            if (refusal is null)
            {
                session = new ServicePaymentSession(
                    payment.Id,
                    intent!.ClientSecret ?? throw new PaymentProcessingException("Stripe returned a PaymentIntent without a client secret."),
                    configuration["Stripe:PublishableKey"] ?? string.Empty,
                    account,
                    payment.AmountCents,
                    payment.Currency.ToUpperInvariant());
            }
        }

        if (refusal is not null)
            throw refusal;

        return session!;
    }

    /// <summary>
    /// The PaymentIntent is still the right one: same amount, currency and commission as the payment, on the account the payment
    /// is charged on now (the supplier did not replace its Stripe account since).
    /// </summary>
    private static bool Matches(ServiceChargeIntent current, ServiceRequestPayment payment, long? fee, string intentAccount, string account) =>
        current.AmountCents == payment.AmountCents
        && string.Equals(current.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)
        && current.ApplicationFeeCents == fee
        && string.Equals(intentAccount, account, StringComparison.Ordinal);
}
