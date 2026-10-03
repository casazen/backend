using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>A page of platform invoices for the admin e-invoicing queue.</summary>
public sealed record PlatformInvoicePage(IReadOnlyList<PlatformInvoice> Items, int TotalCount);

/// <summary>
/// CasaZen SaaS invoices (PL-13, A1-08): records a paid Stripe invoice with the tax computed by Stripe Tax and tracks
/// its Italian e-invoice (SDI) honestly: submitted by a configured <see cref="ISdiEInvoiceProvider"/>, otherwise
/// "to be issued manually" until a platform admin declares it issued.
/// </summary>
public interface IPlatformInvoiceService
{
    /// <summary>
    /// Records <paramref name="invoice"/> for <paramref name="org"/> inside the Stripe event transaction. Reads the tax
    /// rates (and, for a reverse charge, the customer's tax ids) from Stripe: a Stripe error rolls the event back and
    /// Hangfire retries it. Returns the new row; the caller saves it.
    /// </summary>
    Task<PlatformInvoice> RecordPaidInvoiceAsync(Invoice invoice, Org org, CancellationToken cancellationToken = default);

    /// <summary>
    /// After the event is committed: submits a <see cref="PlatformInvoiceSdiStatuses.Pending"/> invoice to the configured
    /// provider and stores the outcome. Never throws for a provider error: the invoice becomes
    /// <see cref="PlatformInvoiceSdiStatuses.Failed"/> and stays visible in the admin queue.
    /// </summary>
    Task SubmitToSdiAsync(Guid platformInvoiceId, CancellationToken cancellationToken = default);

    /// <summary>Platform-wide list for admins, newest first, optionally by SDI status and/or tax review flag.</summary>
    Task<PlatformInvoicePage> ListAsync(string? sdiStatus, bool? taxReviewOnly, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// A platform admin declares the e-invoice issued by hand, with its reference (e.g. the SDI file name or the number
    /// of the invoice in the accounting software). 404 when missing, 409 when already submitted or issued.
    /// </summary>
    Task<PlatformInvoice> MarkSdiIssuedManuallyAsync(Guid platformInvoiceId, string reference, CancellationToken cancellationToken = default);
}

public sealed class PlatformInvoiceService(
    AppDbContext dbContext,
    IStripeBillingService stripeBillingService,
    ISdiEInvoiceProvider sdiProvider,
    ILogger<PlatformInvoiceService> logger) : IPlatformInvoiceService
{
    public const string SdiAlreadyIssuedCode = "platform_invoice_sdi_already_issued";
    public const string SdiAlreadyIssuedMessageKey = "PlatformInvoiceSdiAlreadyIssued";

    public async Task<PlatformInvoice> RecordPaidInvoiceAsync(Invoice invoice, Org org, CancellationToken cancellationToken = default)
    {
        var rateIds = PlatformInvoiceTaxClassifier.TaxRateIdsOf(invoice);
        var rates = rateIds.Count == 0
            ? []
            : await stripeBillingService.GetTaxRatesAsync(rateIds, cancellationToken);

        IReadOnlyList<StripeCustomerTaxId>? customerTaxIds = null;
        if (PlatformInvoiceTaxClassifier.HasReverseCharge(invoice) && !string.IsNullOrWhiteSpace(invoice.CustomerId))
            customerTaxIds = await stripeBillingService.ListCustomerTaxIdsAsync(invoice.CustomerId, cancellationToken);

        var tax = PlatformInvoiceTaxClassifier.Classify(invoice, rates, customerTaxIds);
        var now = DateTime.UtcNow;
        var platformInvoice = new PlatformInvoice
        {
            OrgId = org.Id,
            StripeInvoiceId = invoice.Id,
            StripeInvoiceNumber = invoice.Number,
            Currency = invoice.Currency,
            AmountExVat = tax.AmountExVat,
            VatAmount = tax.VatAmount,
            TotalAmount = tax.TotalAmount,
            VatTreatment = tax.VatTreatment,
            OssApplied = tax.OssApplied,
            CustomerCountry = tax.CustomerCountry,
            TaxCountry = tax.TaxCountry,
            VatRatePercent = tax.VatRatePercent,
            TaxabilityReasons = tax.TaxabilityReasons,
            CustomerVatIdVerification = tax.CustomerVatIdVerification,
            TaxReviewReason = tax.TaxReviewReason,
            // Every invoice goes through SDI (Italian customers: e-invoice; foreign customers: data transmission,
            // fiscale.md P13). Without a provider it is issued by hand: never "sent" without an answer.
            SdiStatus = sdiProvider.IsConfigured ? PlatformInvoiceSdiStatuses.Pending : PlatformInvoiceSdiStatuses.ManualRequired,
            SdiStatusUpdatedAt = now,
            CreatedAt = now,
        };
        dbContext.PlatformInvoices.Add(platformInvoice);

        if (tax.TaxReviewReason is not null)
        {
            logger.LogWarning(
                "Platform invoice {StripeInvoiceId} of org {OrgId} recorded with tax review {TaxReviewReason} (treatment {VatTreatment})",
                invoice.Id,
                org.Id,
                tax.TaxReviewReason,
                tax.VatTreatment);
        }

        if (!sdiProvider.IsConfigured)
        {
            logger.LogWarning(
                "No SDI provider configured: e-invoice of platform invoice {StripeInvoiceId} (org {OrgId}) must be issued manually",
                invoice.Id,
                org.Id);
        }

        return platformInvoice;
    }

    public async Task SubmitToSdiAsync(Guid platformInvoiceId, CancellationToken cancellationToken = default)
    {
        if (!sdiProvider.IsConfigured)
            return;

        // System context (Stripe webhook job): the invoice is read by id across orgs, as the event concerns one org.
        var invoice = await dbContext.PlatformInvoices
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Include(i => i.Org)
            .FirstOrDefaultAsync(i => i.Id == platformInvoiceId, cancellationToken);
        if (invoice is null || invoice.SdiStatus != PlatformInvoiceSdiStatuses.Pending)
            return;

        SdiSubmissionResult result;
        try
        {
            result = await sdiProvider.SubmitAsync(invoice, invoice.Org, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "SDI provider {Provider} failed for platform invoice {PlatformInvoiceId} (org {OrgId})",
                sdiProvider.Name,
                invoice.Id,
                invoice.OrgId);
            result = SdiSubmissionResult.Failure($"provider_error: {ex.GetType().Name}");
        }

        invoice.SdiStatus = result.Accepted && !string.IsNullOrWhiteSpace(result.TransmissionId)
            ? PlatformInvoiceSdiStatuses.Submitted
            : PlatformInvoiceSdiStatuses.Failed;
        invoice.SdiTransmissionId = result.Accepted ? result.TransmissionId : null;
        invoice.SdiError = result.Accepted ? null : Truncate(result.Error ?? "rejected", 500);
        invoice.SdiStatusUpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Platform invoice {PlatformInvoiceId} (org {OrgId}) SDI status {SdiStatus} via {Provider}",
            invoice.Id,
            invoice.OrgId,
            invoice.SdiStatus,
            sdiProvider.Name);
    }

    public async Task<PlatformInvoicePage> ListAsync(
        string? sdiStatus,
        bool? taxReviewOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // AdminOnly: the platform e-invoicing queue spans every org, so the tenant filter is bypassed on purpose.
        var query = dbContext.PlatformInvoices
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Include(i => i.Org)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(sdiStatus))
            query = query.Where(i => i.SdiStatus == sdiStatus);
        if (taxReviewOnly == true)
            query = query.Where(i => i.TaxReviewReason != null);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PlatformInvoicePage(items, total);
    }

    public async Task<PlatformInvoice> MarkSdiIssuedManuallyAsync(
        Guid platformInvoiceId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        // AdminOnly, platform-wide queue: the tenant filter is bypassed on purpose.
        var invoice = await dbContext.PlatformInvoices
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Include(i => i.Org)
            .FirstOrDefaultAsync(i => i.Id == platformInvoiceId, cancellationToken)
            ?? throw new NotFoundException($"Platform invoice {platformInvoiceId} not found")
            {
                Code = "platform_invoice_not_found",
                MessageKey = "PlatformInvoiceNotFound",
            };

        if (invoice.SdiStatus is PlatformInvoiceSdiStatuses.Submitted or PlatformInvoiceSdiStatuses.ManualIssued)
            throw new DomainConflictException(SdiAlreadyIssuedCode, SdiAlreadyIssuedMessageKey);

        invoice.SdiStatus = PlatformInvoiceSdiStatuses.ManualIssued;
        invoice.SdiTransmissionId = reference.Trim();
        invoice.SdiError = null;
        invoice.SdiStatusUpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Platform invoice {PlatformInvoiceId} (org {OrgId}) e-invoice declared issued manually",
            invoice.Id,
            invoice.OrgId);
        return invoice;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
