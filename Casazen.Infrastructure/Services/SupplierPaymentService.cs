using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierPaymentService"/>
/// <remarks>
/// <para><b>Payment model (decision D2).</b> A <b>direct charge</b> on the supplier's own Stripe connected account with the platform
/// commission as <c>application_fee_amount</c>, created by <see cref="ISupplierPaymentGateway"/>; CasaZen holds no funds. The
/// PaymentIntent is created when the payer opens the payment session, never before (the amount and the supplier's account are
/// checked then), and reused while payable with the same amount, fee and account.</para>
/// <para><b>Concurrency.</b> Everything that creates, reuses or drops the PaymentIntent of a request, re-issues a link (the
/// supplier's request or reminder) or records an offline payment runs under the advisory lock
/// <see cref="PostgresAdvisoryLocks.Scope.ServiceRequestPayment"/> (key: the request id) in a READ COMMITTED transaction, held for
/// the Stripe calls, with the payment read again after the lock. The first link, issued with the completion or the host's
/// confirmation, takes no lock: the <c>xmin</c> check of the request and the unique partial index (one payment per request that
/// is not canceled) decide the race, and the loser gets a 409. Emails are queued after the commit, whether or not the caller is
/// still there. Logs carry request, payment and Stripe ids only: no names, no email, no link, no client secret.</para>
/// <para><b>SP-15b.</b> The same class holds, in its own files, the Stripe webhook that makes a payment <c>Paid</c> after checking
/// account, amount, currency and fee (<see cref="ISupplierPaymentWebhookService"/>, <c>.Webhook.cs</c>), the sync, reminder and
/// pending-request jobs (<see cref="ISupplierPaymentJobService"/>, <c>.Jobs.cs</c>) and the admin refunds
/// (<see cref="ISupplierPaymentRefundService"/>, <c>.Refunds.cs</c>): one payment lock, one way to issue a link and one way to
/// read a PaymentIntent for all of them. The admin reads are in <c>SupplierPaymentAdminService</c>.</para>
/// </remarks>
public sealed partial class SupplierPaymentService(
    AppDbContext db,
    ISupplierPaymentGateway gateway,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IFeatureFlags features,
    IOptions<SupplierPaymentsOptions> options,
    IConfiguration configuration,
    ISupplierPaymentJobScheduler jobScheduler,
    ILogger<SupplierPaymentService> logger,
    TimeProvider? timeProvider = null)
    : ISupplierPaymentService, ISupplierPaymentWebhookService, ISupplierPaymentJobService, ISupplierPaymentRefundService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The supplier's side of a payment as it is now: its Stripe account, whether it can be paid, and its name.</summary>
    private sealed record SupplierState(string? AccountId, bool CanReceivePayments, string Name);

    /// <summary>What the emails and the payment page name: the supplier, the work and the property.</summary>
    private sealed record PaymentContext(string SupplierName, string ServiceName, string PropertyName, DateTime? CompletedAt);

    // ─── Mode and plan ───────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceRequestPaymentMode> ResolveModeAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        if (!features.IsEnabled(FeatureFlags.SupplierOnlinePayments))
            return ServiceRequestPaymentMode.Manual;

        var supplier = await ReadSupplierAsync(supplierOrgId, cancellationToken);
        return supplier.CanReceivePayments ? ServiceRequestPaymentMode.Online : ServiceRequestPaymentMode.Manual;
    }

    public async Task<ServicePaymentPlan> PlanAsync(
        ServiceRequest request,
        int? finalAmountCents,
        IReadOnlyList<ServiceRequestPriceLine> lines,
        bool needsConfirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lines);

        if (request.PaymentMode != ServiceRequestPaymentMode.Online)
            return new ServicePaymentPlan(ServiceRequestPaymentMode.Manual, null, null);

        // The flag stops the creation of payment requests; what exists stays payable (money in flight). A request taken while it was
        // on is completed as a manual one now: nothing is stuck, the host marks it paid as before.
        if (!features.IsEnabled(FeatureFlags.SupplierOnlinePayments))
        {
            logger.LogInformation(
                "ServiceRequest {Id}: online payments are switched off, the request is completed as a manual one", request.Id);
            return new ServicePaymentPlan(ServiceRequestPaymentMode.Manual, null, null);
        }

        if (finalAmountCents is not { } amount || amount < options.Value.MinAmountCents)
        {
            logger.LogInformation(
                "ServiceRequest {Id}: the final amount cannot be charged online (missing or below {MinAmountCents} cents), the request is completed as a manual one",
                request.Id, options.Value.MinAmountCents);
            return new ServicePaymentPlan(ServiceRequestPaymentMode.Manual, null, null);
        }

        // Decision D7: the amount above the quote waits for the host; the payment is created when it confirms.
        if (needsConfirmation)
            return new ServicePaymentPlan(ServiceRequestPaymentMode.Online, null, null);

        var now = Now();
        var payment = await NewPaymentAsync(request, amount, lines, now, cancellationToken);
        var supplier = await ReadSupplierAsync(request.SupplierOrgId, cancellationToken);

        // The link is issued only if it can be sent now: the supplier can be paid and the payer has an address. Otherwise the payment
        // is pending (no token, nothing sent) and the supplier asks for it once it can (SP-15b also sends the pending ones).
        string? token = null;
        if (supplier.CanReceivePayments && !string.IsNullOrWhiteSpace(await ReadPayerEmailAsync(payment.PayerOrgId, cancellationToken)))
            token = IssueToken(payment, now);

        db.ServiceRequestPayments.Add(payment);
        return new ServicePaymentPlan(ServiceRequestPaymentMode.Online, payment, token);
    }

    public async Task AnnounceAsync(ServicePaymentPlan plan, ServiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan is not { Payment: { } payment, Token: { } token })
            return;

        // The link is already saved. Queueing its email, and taking the link back if that fails, must not depend on the caller
        // still being there: a dropped connection would leave a link that exists and was never sent.
        var queued = false;
        try
        {
            queued = await QueueLinkEmailAsync(payment, token, reminder: false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The request is saved: a failure here is never an error for the supplier who completed the work.
            logger.LogError(ex, "Payment request email of payment {PaymentId} could not be prepared", payment.Id);
        }

        if (queued)
        {
            logger.LogInformation("Payment {PaymentId} of request {RequestId}: the payment request was queued", payment.Id, request.Id);
            return;
        }

        // Not sent (email provider not configured, queue down): take the link back, so the payment is plainly pending and the
        // supplier can ask for it again. Nothing the payer could use stays around.
        try
        {
            var stored = await db.ServiceRequestPayments.Where(p => p.Id == payment.Id).FirstAsync(CancellationToken.None);
            RevokeLink(stored, previousHash: null, previousRequestedAt: null, previousSentAt: null, previousSentCount: 0, Now());
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Payment {PaymentId} of request {RequestId}: the payment request could not be sent, it stays pending", payment.Id, request.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Payment {PaymentId}: the link that was not sent could not be taken back", payment.Id);
        }
    }

    public void Discard(ServicePaymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Payment is { } payment && db.Entry(payment).State == EntityState.Added)
            db.Entry(payment).State = EntityState.Detached;
    }

    // ─── The row ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A new <see cref="ServicePaymentStatus.Requested"/> payment for the final <paramref name="amountCents"/> of the request, with
    /// the commission snapshotted: the supplier's own percentage, else the platform's.
    /// </summary>
    private async Task<ServiceRequestPayment> NewPaymentAsync(
        ServiceRequest request,
        int amountCents,
        IReadOnlyList<ServiceRequestPriceLine> lines,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var percent = await ReadCommissionPercentAsync(request.SupplierOrgId, cancellationToken);
        var fee = SupplierCommission.FeeCents(amountCents, percent);

        return new ServiceRequestPayment
        {
            ServiceRequestId = request.Id,
            SupplierOrgId = request.SupplierOrgId,
            PayerKind = ServicePayerKind.Host,
            PayerOrgId = request.OrgId,
            AmountCents = amountCents,
            Currency = ServiceCharges.Currency,
            CommissionPercent = percent,
            ApplicationFeeCents = fee,
            NetCents = SupplierCommission.NetCents(amountCents, fee),
            Status = ServicePaymentStatus.Requested,
            LineItemsJson = ServiceRequestJson.Serialize(lines),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The percentage a payment is created with: the supplier's own while its override is in force (no end, or an end that has not
    /// passed: SP-15b "periodo gratuito"), else the platform's (<c>SupplierPayments:CommissionPercent</c>).
    /// </summary>
    private async Task<decimal> ReadCommissionPercentAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
        var own = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => new { sp.CommissionPercentOverride, sp.CommissionOverrideUntil })
            .FirstOrDefaultAsync(cancellationToken);
        return SupplierCommission.EffectivePercent(
            options.Value.RequireCommissionPercent(), own?.CommissionPercentOverride, own?.CommissionOverrideUntil, Now());
    }

    /// <summary>The supplier's Stripe account and name as they are now. An unknown org cannot be paid.</summary>
    private async Task<SupplierState> ReadSupplierAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        // Org is the tenant itself (no tenant filter, TN-2 allow-list); scoped by the id.
        var org = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == supplierOrgId)
            .Select(o => new { o.StripeConnectedAccountId, o.ConnectChargesEnabled, o.ConnectPayoutsEnabled, o.DisplayName, o.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (org is null)
            return new SupplierState(null, false, string.Empty);

        return new SupplierState(
            org.StripeConnectedAccountId,
            SupplierVerification.CanReceivePayments(org.StripeConnectedAccountId, org.ConnectChargesEnabled, org.ConnectPayoutsEnabled),
            string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName);
    }

    private async Task<string?> ReadPayerEmailAsync(Guid? payerOrgId, CancellationToken cancellationToken)
    {
        if (payerOrgId is not { } orgId)
            return null;

        return await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.ContactEmail)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<PaymentContext> ReadContextAsync(ServiceRequestPayment payment, CancellationToken cancellationToken)
    {
        // The request has two parties and is not tenant-filtered; scoped by the id the payment points to.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == payment.ServiceRequestId)
            .Select(r => new { r.ServiceNameSnapshot, r.Category, r.PropertyId, r.CompletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        // The property of the request is the payer's own; read by id, including a property deleted since (the payment outlives it).
        var propertyName = request is null
            ? string.Empty
            : await db.Properties
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.Id == request.PropertyId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var supplier = await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken);
        var serviceName = !string.IsNullOrWhiteSpace(request?.ServiceNameSnapshot)
            ? request.ServiceNameSnapshot
            : EmailTemplates.ServiceCategoryLabel(EmailTemplates.DefaultCulture, request?.Category ?? string.Empty);
        return new PaymentContext(supplier.Name, serviceName, propertyName, request?.CompletedAt);
    }

    // ─── The link ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a new payment link: a new token whose hash replaces the previous one (the old link stops working), counted from
    /// <paramref name="now"/>. Returns the raw token, which only the email carries.
    /// </summary>
    private static string IssueToken(ServiceRequestPayment payment, DateTime now)
    {
        var token = CheckoutOutcomes.NewToken();
        payment.PaymentTokenHash = CheckoutOutcomes.HashToken(token);
        payment.RequestedAt ??= now;
        payment.LastSentAt = now;
        payment.SentCount++;
        payment.UpdatedAt = now;
        return token;
    }

    /// <summary>Puts the link fields back to what they were before <see cref="IssueToken"/> (the email could not be queued).</summary>
    private static void RevokeLink(
        ServiceRequestPayment payment,
        string? previousHash,
        DateTime? previousRequestedAt,
        DateTime? previousSentAt,
        int previousSentCount,
        DateTime now)
    {
        payment.PaymentTokenHash = previousHash;
        payment.RequestedAt = previousRequestedAt;
        payment.LastSentAt = previousSentAt;
        payment.SentCount = previousSentCount;
        payment.UpdatedAt = now;
    }

    /// <summary>When the current link stops working: <c>PaymentLinkValidityDays</c> after the email that carried it.</summary>
    private DateTime? ValidUntil(ServiceRequestPayment payment) =>
        payment.LastSentAt?.AddDays(options.Value.PaymentLinkValidityDays);

    /// <summary>Whether <paramref name="token"/> opens the payment: it matches the stored hash and, while the payment is still to be made, has not expired.</summary>
    private bool LinkIsUsable(ServiceRequestPayment payment, string? token, DateTime now)
    {
        if (!CheckoutOutcomes.TokenMatches(payment.PaymentTokenHash, token))
            return false;

        // A paid or processing payment keeps showing its state; a payment still to be made needs a link that is within its validity.
        return !IsOpen(payment.Status) || ValidUntil(payment) is { } validUntil && validUntil > now;
    }

    /// <summary>The payment can still be paid by the payer: nothing was collected and it was not dropped.</summary>
    private static bool IsOpen(ServicePaymentStatus status) => status is ServicePaymentStatus.Requested or ServicePaymentStatus.Failed;

    /// <summary>
    /// Queues the payment request (or the reminder) to the payer org's address with the link of <paramref name="token"/>; false when it
    /// is not queued (no address, email provider not configured, queue down). The payment is loaded with what the email names.
    /// </summary>
    private async Task<bool> QueueLinkEmailAsync(
        ServiceRequestPayment payment,
        string token,
        bool reminder,
        CancellationToken cancellationToken)
    {
        var recipient = await ReadPayerEmailAsync(payment.PayerOrgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
            return false;

        var context = await ReadContextAsync(payment, cancellationToken);
        var validUntil = ValidUntil(payment) ?? Now().AddDays(options.Value.PaymentLinkValidityDays);
        var url = links.ServicePayment(payment.Id, token);

        var content = reminder
            ? EmailTemplates.ServicePaymentReminder(
                EmailTemplates.DefaultCulture, context.SupplierName, context.ServiceName, context.PropertyName, payment.AmountCents, validUntil, url)
            : EmailTemplates.ServicePaymentRequest(
                EmailTemplates.DefaultCulture, context.SupplierName, context.ServiceName, context.PropertyName, payment.AmountCents, validUntil, url);
        return emailQueue.Enqueue(
            recipient,
            content,
            reminder ? EmailTemplates.Names.ServicePaymentReminder : EmailTemplates.Names.ServicePaymentRequest);
    }

    // ─── Stripe ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The PaymentIntent as Stripe has it now, or null when it (or the connected account it lives on) no longer exists: Stripe says
    /// <c>resource_missing</c> or <c>account_invalid</c>. That is what a supplier that replaced its Stripe account leaves behind (SP-14
    /// replaces an account only when Stripe says the old one is gone), and it must never leave a payment impossible to pay or to
    /// withdraw. Any other Stripe failure is rethrown (503 <c>payment_provider_error</c>): only a certain "gone" is acted upon.
    /// </summary>
    private async Task<ServiceChargeIntent?> ReadIntentAsync(
        string paymentIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.GetAsync(paymentIntentId, connectedAccountId, cancellationToken);
        }
        catch (Stripe.StripeException ex) when (ex.StripeError?.Code is "resource_missing" or "account_invalid")
        {
            logger.LogWarning(
                "Payment intent {PaymentIntentId} no longer exists on Stripe ({StripeCode}): the payment does not depend on it any more",
                paymentIntentId, ex.StripeError!.Code);
            return null;
        }
    }

    // ─── Locks and saves ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a READ COMMITTED transaction holding the payment lock of the request (key: the request id), or null outside PostgreSQL:
    /// everything read after it sees what the previous holder committed.
    /// </summary>
    private Task<IDbContextTransaction?> LockAsync(Guid requestId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.ServiceRequestPayment, requestId.ToString("N")));

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the changes. A request changed meanwhile (the <c>xmin</c> token) and a second live payment (the unique index) are both a
    /// 409: nothing is saved, and the caller can read the new state and decide again.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("A service request changed while its payment was being saved: nothing was saved");
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("A second live payment for a service request was refused by the database: nothing was saved");
            throw new DomainConflictException(ServicePaymentErrors.StateChanged, ServicePaymentErrors.StateChangedMessageKey);
        }
    }

    // ─── Errors ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static NotFoundException LinkInvalid() =>
        new("The service payment link does not match a payment")
        {
            Code = ServicePaymentErrors.LinkInvalid,
            MessageKey = ServicePaymentErrors.LinkInvalidMessageKey,
        };

    private static NotFoundException PaymentNotFound() =>
        new("The service request has no payment for the caller")
        {
            Code = ServicePaymentErrors.NotFound,
            MessageKey = ServicePaymentErrors.NotFoundMessageKey,
        };

    private static DomainConflictException AlreadyPaid() =>
        new(ServicePaymentErrors.NotPayable, ServicePaymentErrors.AlreadyPaidMessageKey);

    private static DomainConflictException NotPayable() =>
        new(ServicePaymentErrors.NotPayable, ServicePaymentErrors.NotPayableMessageKey);

    private static DomainConflictException InFlight() =>
        new(ServicePaymentErrors.InFlight, ServicePaymentErrors.InFlightMessageKey);

    private static DomainConflictException SupplierNotReady() =>
        new(ServicePaymentErrors.SupplierNotReady, ServicePaymentErrors.SupplierNotReadyMessageKey);
}
