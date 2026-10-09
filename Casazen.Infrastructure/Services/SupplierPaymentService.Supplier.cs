using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>What the supplier does with the payment of a request: asks for it (or reminds), and records one received outside CasaZen.</summary>
public sealed partial class SupplierPaymentService
{
    // ─── Ask for the payment ─────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceRequestPayment> RequestPaymentAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The state of the request is checked before the lock (cheap, and the usual refusals) and again after it.
        EnsureRequestable(request);
        var minimum = options.Value.MinAmountCents;
        if (request.FinalAmountCents is not { } amount || amount < minimum)
            throw new DomainRuleException(ServicePaymentErrors.AmountRequired, ServicePaymentErrors.AmountRequiredMessageKey);

        var supplier = await ReadSupplierAsync(request.SupplierOrgId, cancellationToken);
        if (!supplier.CanReceivePayments)
            throw new DomainRuleException(SupplierPaymentsErrors.NotReady, SupplierPaymentsErrors.NotReadyMessageKey);

        if (string.IsNullOrWhiteSpace(await ReadPayerEmailAsync(request.OrgId, cancellationToken)))
            throw new DomainRuleException(ServicePaymentErrors.NoRecipient, ServicePaymentErrors.NoRecipientMessageKey);

        ServiceRequestPayment payment;
        string token;
        string? previousHash;
        DateTime? previousRequestedAt;
        DateTime? previousSentAt;
        int previousSentCount;
        bool reminder;
        await using (var transaction = await LockAsync(request.Id, cancellationToken))
        {
            // Read again under the lock: the request may have been paid or cancelled, the payment sent or paid, meanwhile.
            await db.Entry(request).ReloadAsync(cancellationToken);
            EnsureRequestable(request);

            var now = Now();
            payment = await LivePaymentOf(request.Id).FirstOrDefaultAsync(cancellationToken)
                ?? await NewPaymentAsync(request, amount, ServiceRequestJson.ReadPriceLines(request.PriceLinesJson), now, cancellationToken);
            var isNew = db.Entry(payment).State == EntityState.Detached;

            switch (payment.Status)
            {
                case ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded:
                    throw AlreadyPaid();
                case ServicePaymentStatus.Processing or ServicePaymentStatus.NeedsReview:
                    throw InFlight();
            }

            if (payment.LastSentAt is { } lastSent && now - lastSent < TimeSpan.FromHours(ServicePaymentLimits.MinHoursBetweenRequests))
            {
                throw new DomainRuleException(
                    ServicePaymentErrors.RequestTooSoon,
                    ServicePaymentErrors.RequestTooSoonMessageKey,
                    ServicePaymentLimits.MinHoursBetweenRequests);
            }

            previousHash = payment.PaymentTokenHash;
            previousRequestedAt = payment.RequestedAt;
            previousSentAt = payment.LastSentAt;
            previousSentCount = payment.SentCount;
            token = IssueToken(payment, now);
            reminder = previousSentCount > 0;

            if (isNew)
                db.ServiceRequestPayments.Add(payment);

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        // The new link is committed: sending it, or taking it back if it cannot be sent, does not depend on the caller still
        // being there (otherwise a dropped connection would leave the payer's old link dead and nothing sent).
        bool queued;
        try
        {
            queued = await QueueLinkEmailAsync(payment, token, reminder, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Payment request email of payment {PaymentId} could not be prepared", payment.Id);
            queued = false;
        }

        if (!queued)
        {
            // Nothing was sent: the link that was issued is taken back, so the payer's previous link (if any) works as before.
            RevokeLink(payment, previousHash, previousRequestedAt, previousSentAt, previousSentCount, Now());
            await db.SaveChangesAsync(CancellationToken.None);
            throw new DomainRuleException(ServicePaymentErrors.RequestNotSent, ServicePaymentErrors.RequestNotSentMessageKey);
        }

        logger.LogInformation(
            "Payment {PaymentId} of request {RequestId}: {Kind} queued ({SentCount} sent)",
            payment.Id, request.Id, reminder ? "reminder" : "payment request", payment.SentCount);
        return payment;
    }

    /// <summary>The request can have its payment asked for: paid inside CasaZen, completed, with an amount nobody still has to confirm.</summary>
    private static void EnsureRequestable(ServiceRequest request)
    {
        if (request.PaymentMode != ServiceRequestPaymentMode.Online)
            throw new DomainRuleException(ServicePaymentErrors.NotOnline, ServicePaymentErrors.NotOnlineMessageKey);
        if (request.Status != ServiceRequestStatus.Completato)
            throw new DomainRuleException(ServicePaymentErrors.NotRequestable, ServicePaymentErrors.NotRequestableMessageKey);
        if (request.FinalAmountNeedsConfirmation)
            throw new DomainRuleException(ServicePaymentErrors.AmountUnconfirmed, ServicePaymentErrors.AmountUnconfirmedMessageKey);
    }

    // ─── Paid outside CasaZen ────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceRequestPayment> RecordOfflineAsync(
        ServiceRequest request,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var note = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note?.Length ?? 0, ServicePaymentLimits.OfflineNoteMaxLength, nameof(reason));

        // Decision D5: a request paid inside CasaZen is recorded as paid outside only as a traced exception.
        var wasOnline = request.PaymentMode == ServiceRequestPaymentMode.Online;
        if (wasOnline && note is null)
            throw new DomainRuleException(ServicePaymentErrors.OfflineReasonRequired, ServicePaymentErrors.OfflineReasonRequiredMessageKey);

        EnsureCanBePaid(request);
        if (request.FinalAmountCents is not { } amount)
            throw new DomainRuleException(ServicePaymentErrors.AmountRequired, ServicePaymentErrors.AmountRequiredMessageKey);

        ServiceRequestPayment recorded;
        await using (var transaction = await LockAsync(request.Id, cancellationToken))
        {
            await db.Entry(request).ReloadAsync(cancellationToken);
            EnsureCanBePaid(request);

            var now = Now();
            var live = await LivePaymentOf(request.Id).ToListAsync(cancellationToken);
            foreach (var open in live)
            {
                switch (open.Status)
                {
                    case ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded:
                        throw AlreadyPaid();
                    case ServicePaymentStatus.Processing or ServicePaymentStatus.NeedsReview:
                        throw InFlight();
                }

                // Requested or Failed: the payer must not be able to pay it online after the supplier declared it paid, so its
                // PaymentIntent is canceled first. One that was paid or is in progress on Stripe meanwhile wins: it is recorded
                // as processing and the declaration is refused, so the same money is never counted twice.
                if (open.StripePaymentIntentId is { } intentId && open.ConnectedAccountId is { } account)
                {
                    var current = await ReadIntentAsync(intentId, account, cancellationToken);
                    if (current is not null && ServiceCharges.IsPayable(current.Status))
                    {
                        current = await gateway.CancelAsync(
                            intentId, account, ServiceCharges.CancellationIdempotencyKey(open.Id, intentId), cancellationToken);
                    }

                    // A PaymentIntent that is gone (with its account) cannot be paid: there is nothing to wait for.
                    if (current is not null && current.Status != "canceled")
                    {
                        open.Status = ServicePaymentStatus.Processing;
                        open.UpdatedAt = now;
                        await SaveAsync(cancellationToken);
                        await CommitAsync(transaction, cancellationToken);
                        throw InFlight();
                    }
                }

                open.Status = ServicePaymentStatus.Canceled;
                open.CanceledAt = now;
                open.UpdatedAt = now;
            }

            // The request becomes paid in the same save as the withdrawn payments; the new row follows in a second save of the same
            // transaction: the unique index "one live payment per request" would refuse it before the old ones are canceled.
            request.Status = ServiceRequestStatus.Pagato;
            request.PaidAt = now;
            request.PaidBy = ServiceRequestActorParty.Supplier;
            request.UpdatedAt = now;
            await SaveAsync(cancellationToken);

            recorded = new ServiceRequestPayment
            {
                ServiceRequestId = request.Id,
                SupplierOrgId = request.SupplierOrgId,
                PayerKind = ServicePayerKind.Host,
                PayerOrgId = request.OrgId,
                AmountCents = amount,
                Currency = ServiceCharges.Currency,

                // No money went through CasaZen, so no commission (decision D5).
                CommissionPercent = 0m,
                ApplicationFeeCents = 0,
                NetCents = amount,
                Status = ServicePaymentStatus.Paid,
                PaidAt = now,
                PaidVia = ServicePaymentChannel.Offline,
                LineItemsJson = request.PriceLinesJson,
                OfflineNote = note,
                MarkedPaidByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ServiceRequestPayments.Add(recorded);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        if (wasOnline)
        {
            logger.LogWarning(
                "Request {RequestId} paid inside CasaZen was recorded as paid outside by supplier user {UserId}: a traced exception (decision D5)",
                request.Id, userId);
        }
        else
        {
            logger.LogInformation("Request {RequestId} was recorded as paid outside CasaZen", request.Id);
        }

        // Best effort after the commit, whether or not the caller is still there.
        await NotifyHostOfflineRecordedAsync(request, recorded, note, CancellationToken.None);
        return recorded;
    }

    private static void EnsureCanBePaid(ServiceRequest request)
    {
        if (!ServiceRequestStateMachine.CanTransition(request.Status, ServiceRequestStatus.Pagato))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotMarkPaidMessageKey);
        }
    }

    /// <summary>Tells the host org that the supplier recorded a payment received outside CasaZen. A failure is logged: the payment is already recorded.</summary>
    private async Task NotifyHostOfflineRecordedAsync(
        ServiceRequest request,
        ServiceRequestPayment payment,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var recipient = await ReadPayerEmailAsync(payment.PayerOrgId, cancellationToken);
            if (string.IsNullOrWhiteSpace(recipient))
                return;

            var context = await ReadContextAsync(payment, cancellationToken);
            emailQueue.Enqueue(
                recipient,
                EmailTemplates.ServicePaymentOfflineRecorded(
                    EmailTemplates.DefaultCulture, context.SupplierName, context.ServiceName, context.PropertyName, payment.AmountCents, reason),
                EmailTemplates.Names.ServicePaymentOfflineRecorded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Host notice of the offline payment of request {RequestId} could not be queued", request.Id);
        }
    }

    // ─── Queries ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The payment of a request that is not canceled (at most one: the unique index), tracked.</summary>
    private IQueryable<ServiceRequestPayment> LivePaymentOf(Guid requestId) =>
        db.ServiceRequestPayments.Where(p => p.ServiceRequestId == requestId && p.Status != ServicePaymentStatus.Canceled);
}
