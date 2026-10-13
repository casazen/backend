using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The emails that follow a webhook, a sync or a refund (SP-15b), sent <b>after</b> the transaction that changed the payment
/// committed: the receipt to the supplier, the new link of a payment that failed after it was accepted, the alert to the admins, the
/// refund to the payer and the supplier. A failure here is logged and never turned into an error: the change is already saved.
/// </summary>
public sealed partial class SupplierPaymentService
{
    public async Task CompleteAsync(IReadOnlyList<ServicePaymentNotice> notices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notices);
        foreach (var notice in notices)
        {
            try
            {
                await CompleteOneAsync(notice, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The {Kind} notice of service payment {PaymentId} could not be sent", notice.Kind, notice.PaymentId);
            }
        }
    }

    private async Task CompleteOneAsync(ServicePaymentNotice notice, CancellationToken cancellationToken)
    {
        var payment = await db.ServiceRequestPayments.AsNoTracking().Where(p => p.Id == notice.PaymentId).FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
            return;

        switch (notice.Kind)
        {
            case ServicePaymentNoticeKind.Received:
                await SendReceiptAsync(payment, cancellationToken);
                break;

            case ServicePaymentNoticeKind.FailedInFlight:
                // A new link for the payer: the same way the first one is issued, under the lock and only if it is still due.
                await IssueAndSendLinkAsync(payment.Id, payment.ServiceRequestId, LinkKind.Retry, cancellationToken);
                break;

            case ServicePaymentNoticeKind.NeedsReview:
            case ServicePaymentNoticeKind.Disputed:
                await SendAdminAlertAsync(payment, dispute: notice.Kind == ServicePaymentNoticeKind.Disputed, notice.Detail, cancellationToken);
                break;

            case ServicePaymentNoticeKind.Refunded when notice.RefundId is { } refundId:
                await SendRefundNoticesAsync(payment, refundId, cancellationToken);
                break;
        }
    }

    /// <summary>The receipt to the supplier: price gross, the commission, the net. The net is before Stripe's own fees and nothing is said about the payout.</summary>
    private async Task SendReceiptAsync(ServiceRequestPayment payment, CancellationToken cancellationToken)
    {
        var recipient = await ReadSupplierEmailAsync(payment.SupplierOrgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogWarning("Service payment {PaymentId} paid: supplier {SupplierOrgId} has no email, no receipt queued", payment.Id, payment.SupplierOrgId);
            return;
        }

        var context = await ReadContextAsync(payment, cancellationToken);
        var content = EmailTemplates.ServicePaymentReceived(
            EmailTemplates.DefaultCulture,
            context.ServiceName,
            context.PropertyName,
            payment.AmountCents,
            payment.CommissionPercent,
            payment.ApplicationFeeCents,
            payment.NetCents,
            payment.PaidAt ?? Now(),
            links.SupplierInbox());
        if (!emailQueue.Enqueue(recipient, content, EmailTemplates.Names.ServicePaymentReceived))
            logger.LogWarning("The receipt of service payment {PaymentId} was not queued", payment.Id);
    }

    /// <summary>The alert to every active platform admin: a payment to review, or a dispute to answer. Ids and codes only.</summary>
    private async Task SendAdminAlertAsync(ServiceRequestPayment payment, bool dispute, string? detail, CancellationToken cancellationToken)
    {
        // Users are not tenant-filtered; the platform admins are the ones with the Admin role who can still sign in.
        var admins = await db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.Role == UserRole.Admin && u.Email != string.Empty)
            .Select(u => u.Email)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (admins.Count == 0)
        {
            logger.LogError(
                "No active platform admin has an email: the {Kind} alert of service payment {PaymentId} could not be sent",
                dispute ? "dispute" : "review", payment.Id);
            return;
        }

        var context = await ReadContextAsync(payment, cancellationToken);
        var content = EmailTemplates.ServicePaymentAdminAlert(
            EmailTemplates.DefaultCulture, dispute, payment.Id, payment.ServiceRequestId, context.SupplierName, payment.AmountCents, detail);
        foreach (var admin in admins)
        {
            if (!emailQueue.Enqueue(admin, content, EmailTemplates.Names.ServicePaymentAdminAlert))
                logger.LogWarning("The alert of service payment {PaymentId} was not queued for one admin", payment.Id);
        }
    }

    /// <summary>The refund that succeeded, to the payer (the host org) and to the supplier.</summary>
    private async Task SendRefundNoticesAsync(ServiceRequestPayment payment, Guid refundId, CancellationToken cancellationToken)
    {
        var refund = await db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.Id == refundId).FirstOrDefaultAsync(cancellationToken);
        if (refund is null || refund.Status != ServicePaymentRefundStatus.Succeeded)
            return;

        var context = await ReadContextAsync(payment, cancellationToken);

        var payer = await ReadPayerEmailAsync(payment.PayerOrgId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(payer))
        {
            emailQueue.Enqueue(
                payer,
                EmailTemplates.ServicePaymentRefundedToPayer(
                    EmailTemplates.DefaultCulture, context.SupplierName, context.ServiceName, context.PropertyName, refund.AmountCents),
                EmailTemplates.Names.ServicePaymentRefundedPayer);
        }

        var supplier = await ReadSupplierEmailAsync(payment.SupplierOrgId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(supplier))
        {
            emailQueue.Enqueue(
                supplier,
                EmailTemplates.ServicePaymentRefundedToSupplier(
                    EmailTemplates.DefaultCulture,
                    context.ServiceName,
                    context.PropertyName,
                    refund.AmountCents,
                    refund.ApplicationFeeRefundedCents ?? 0,
                    links.SupplierInbox()),
                EmailTemplates.Names.ServicePaymentRefundedSupplier);
        }
    }

    /// <summary>The supplier's address: its profile's, else its org's contact address.</summary>
    private async Task<string?> ReadSupplierEmailAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
        var email = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => sp.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(email))
            return email;

        return await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == supplierOrgId)
            .Select(o => o.ContactEmail)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
