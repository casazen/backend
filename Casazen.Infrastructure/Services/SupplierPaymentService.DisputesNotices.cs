using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The e-mails of the complaint about a job paid through CasaZen (SP-15c-c), sent <b>after</b> the transaction that changed the dispute committed, through
/// the same <c>CompleteAsync</c> as every other notice of the payments: a failure is logged and never turned into an error, the change is already saved.
/// </summary>
/// <remarks>
/// The supplier's and the payer's e-mails never carry the words of either party (they are read in the app, where they are protected), nor the admin's note.
/// They are not subject to the notification switches of the supplier's console: a complaint that stops the money is not news about a request, it is
/// money waiting. The admins get ids, an amount and a code. Logs carry ids only.
/// </remarks>
public sealed partial class SupplierPaymentService
{
    private async Task SendDisputeNoticeAsync(ServiceRequestPayment payment, ServicePaymentNotice notice, CancellationToken cancellationToken)
    {
        if (notice.DisputeId is not { } disputeId)
            return;

        var dispute = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Id == disputeId)
            .FirstOrDefaultAsync(cancellationToken);
        if (dispute is null)
            return;

        var context = await ReadContextAsync(payment, cancellationToken);
        switch (notice.Kind)
        {
            case ServicePaymentNoticeKind.DisputeOpened:
                await SendDisputeOpenedAsync(payment, dispute, context, cancellationToken);
                break;
            case ServicePaymentNoticeKind.DisputeReplied:
                await SendDisputeRepliedAsync(payment, context, cancellationToken);
                break;
            case ServicePaymentNoticeKind.DisputeResolved:
                await SendDisputeResolvedAsync(payment, dispute, context, cancellationToken);
                break;
            case ServicePaymentNoticeKind.DisputeWithdrawn:
                await SendDisputeWithdrawnAsync(payment, context, cancellationToken);
                break;
            case ServicePaymentNoticeKind.DisputeAdminAlert:
                await SendDisputeAdminAlertAsync(payment, dispute, context, notice.Detail, cancellationToken);
                break;
        }
    }

    private async Task SendDisputeOpenedAsync(
        ServiceRequestPayment payment,
        ServicePaymentDispute dispute,
        PaymentContext context,
        CancellationToken cancellationToken)
    {
        var supplier = await ReadSupplierEmailAsync(payment.SupplierOrgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(supplier))
        {
            logger.LogWarning("Dispute {DisputeId}: supplier {SupplierOrgId} has no email, the notice was not queued", dispute.Id, payment.SupplierOrgId);
        }
        else
        {
            var content = EmailTemplates.ServiceDisputeOpenedToSupplier(
                EmailTemplates.DefaultCulture,
                context.ServiceName,
                dispute.Reason,
                dispute.NoShow,
                PayableToSupplier(payment),
                dispute.SupplierReplyDueAt,
                links.SupplierRequest(payment.ServiceRequestId));
            if (!emailQueue.Enqueue(supplier, content, EmailTemplates.Names.ServiceDisputeOpenedSupplier))
                logger.LogWarning("The notice of dispute {DisputeId} to the supplier was not queued", dispute.Id);
        }

        var payer = await ReadPayerContactAsync(payment, cancellationToken);
        if (payer is not { } contact)
            return;

        var content2 = EmailTemplates.ServiceDisputeOpenedToPayer(
            contact.Culture,
            context.SupplierName,
            context.ServiceName,
            byCasaZen: dispute.OpenedBy == ServiceDisputeOpener.System,
            dispute.SupplierReplyDueAt,
            await ReadBookingManageUrlAsync(payment, cancellationToken));
        if (!emailQueue.Enqueue(contact.Email, content2, EmailTemplates.Names.ServiceDisputeOpenedPayer))
            logger.LogWarning("The notice of dispute {DisputeId} to the payer was not queued", dispute.Id);
    }

    private async Task SendDisputeRepliedAsync(ServiceRequestPayment payment, PaymentContext context, CancellationToken cancellationToken)
    {
        var payer = await ReadPayerContactAsync(payment, cancellationToken);
        if (payer is not { } contact)
            return;

        emailQueue.Enqueue(
            contact.Email,
            EmailTemplates.ServiceDisputeRepliedToPayer(
                contact.Culture, context.SupplierName, context.ServiceName, await ReadBookingManageUrlAsync(payment, cancellationToken)),
            EmailTemplates.Names.ServiceDisputeRepliedPayer);
    }

    private async Task SendDisputeResolvedAsync(
        ServiceRequestPayment payment,
        ServicePaymentDispute dispute,
        PaymentContext context,
        CancellationToken cancellationToken)
    {
        var forCustomer = dispute.Status == ServiceDisputeStatus.ResolvedForCustomer;
        var refund = forCustomer ? dispute.DecidedRefundCents ?? 0 : 0;

        var payer = await ReadPayerContactAsync(payment, cancellationToken);
        if (payer is { } contact)
        {
            emailQueue.Enqueue(
                contact.Email,
                EmailTemplates.ServiceDisputeResolvedToPayer(contact.Culture, context.SupplierName, context.ServiceName, forCustomer, refund),
                EmailTemplates.Names.ServiceDisputeResolvedPayer);
        }

        var supplier = await ReadSupplierEmailAsync(payment.SupplierOrgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(supplier))
            return;

        var remaining = forCustomer ? Math.Max(0, payment.NetCents - SupplierRefundShare(payment, dispute.RefundTargetCents ?? refund)) : PayableToSupplier(payment);
        emailQueue.Enqueue(
            supplier,
            EmailTemplates.ServiceDisputeResolvedToSupplier(
                EmailTemplates.DefaultCulture,
                context.ServiceName,
                forCustomer,
                PayableToSupplier(payment),
                refund,
                remaining,
                links.SupplierRequest(payment.ServiceRequestId)),
            EmailTemplates.Names.ServiceDisputeResolvedSupplier);
    }

    private async Task SendDisputeWithdrawnAsync(ServiceRequestPayment payment, PaymentContext context, CancellationToken cancellationToken)
    {
        var supplier = await ReadSupplierEmailAsync(payment.SupplierOrgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(supplier))
            return;

        emailQueue.Enqueue(
            supplier,
            EmailTemplates.ServiceDisputeWithdrawnToSupplier(EmailTemplates.DefaultCulture, context.ServiceName, PayableToSupplier(payment)),
            EmailTemplates.Names.ServiceDisputeWithdrawnSupplier);
    }

    private async Task SendDisputeAdminAlertAsync(
        ServiceRequestPayment payment,
        ServicePaymentDispute dispute,
        PaymentContext context,
        string? detail,
        CancellationToken cancellationToken)
    {
        var admins = await db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.Role == UserRole.Admin && u.Email != string.Empty)
            .Select(u => u.Email)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (admins.Count == 0)
        {
            logger.LogError("No active platform admin has an email: the alert of dispute {DisputeId} could not be sent", dispute.Id);
            return;
        }

        var content = EmailTemplates.ServiceDisputeAdminAlert(
            EmailTemplates.DefaultCulture, dispute.Id, payment.Id, payment.ServiceRequestId, context.SupplierName, payment.AmountCents, detail);
        foreach (var admin in admins)
        {
            if (!emailQueue.Enqueue(admin, content, EmailTemplates.Names.ServiceDisputeAdminAlert))
                logger.LogWarning("The alert of dispute {DisputeId} was not queued for one admin", dispute.Id);
        }
    }

    private static int PayableToSupplier(ServiceRequestPayment payment) =>
        HeldPaymentRules.ReleasableCents(payment.NetCents, SupplierRefundShare(payment, payment.RefundedCents), 0);

    private async Task<string?> ReadBookingManageUrlAsync(ServiceRequestPayment payment, CancellationToken cancellationToken)
    {
        if (payment.PayerOrgId is not null)
            return null;

        var code = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == payment.ServiceRequestId)
            .Select(r => r.PublicCode)
            .FirstOrDefaultAsync(cancellationToken);

        var slug = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == payment.SupplierOrgId)
            .Select(sp => sp.ShowcaseSlug)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(slug) ? null : links.SupplierBookingRequest(slug, code);
    }
}
