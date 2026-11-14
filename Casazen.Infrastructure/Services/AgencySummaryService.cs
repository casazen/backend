using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAgencySummaryService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Composed, never recomputed.</b> The stays and the revenue are the Home's (<see cref="IHostDashboardService"/>), the collections and the balances are the collections
/// report's, the costs of the channels the channel costs report's, the Italian tourist tax the tourist tax summary's, the report by owner <see cref="IOwnerReportService"/>'s: the
/// summary of the screen and the files of the package are made of the same calls. The rest is added up from what is stored: the fiscal documents (their stored amounts and VAT,
/// never <c>taxable × rate</c>), the payouts and the wire batches, and the owner ledger for the portal commissions of the accounts the agency holds.</item>
/// <item><b>Which day, which month.</b> The documents count on the day they were issued (Europe/Rome), the owner ledger by the statement month, the payouts on the day they were paid and
/// the wire batches on the day they were made; the reports their own way, said in their titles. A credit note takes away what it credits.</item>
/// <item><b>Amounts of different currencies are never added.</b> Every figure of the documents, the payouts and the wires is for a currency.</item>
/// <item><b>The owner accounting is whole-org and permission-bound.</b> Without <c>agency.statement.view</c> (the web layer) or with a scope that does not reach the whole org, the
/// commissions, the invoices to the owners, the payouts, the wires, the margin and the report by owner are empty and the summary says so; nothing of the owner accounting is read.</item>
/// <item><b>Margin.</b> The commissions and the recharges invoiced to the owners (taxable) less the commissions of the portals the agency holds the account of: the one cost
/// CasaZen can tell the agency bears. The card fees and the other costs are in the report of the channel costs.</item>
/// </list>
/// </remarks>
public sealed class AgencySummaryService(
    AppDbContext db,
    IHostDashboardService dashboard,
    IHostCollectionsReportService collections,
    IChannelCostsReportService channelCosts,
    ITouristTaxSummaryService touristTax,
    IOwnerReportService ownerReports,
    IPortalAccountHolderResolver holders,
    TimeProvider timeProvider) : IAgencySummaryService
{
    public async Task<AgencySummary> GetAsync(
        HostScope scope,
        AccountantPackageAccess access,
        AccountantPackagePeriod period,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(period);

        var ownerAccounting = access.AllowsOwnerAccounting(scope);
        var query = period.ReportQuery;

        // The figures of the services that already compute them, as they answer them.
        var performance = await dashboard.GetPerformanceAsync(scope, new HostPerformanceQuery(period.From, period.To), cancellationToken);
        var collected = await collections.GetCollectionsAsync(scope, query, cancellationToken);
        var balances = await collections.GetBalancesToCollectAsync(scope, new HostBalancesQuery(), cancellationToken);
        var costs = await channelCosts.GetChannelCostsAsync(scope, query, cancellationToken);
        var tax = await touristTax.GetSummaryAsync(scope, period.FiscalPeriod, cancellationToken);

        // What the fiscal documents say, for the properties the caller reaches.
        var fallback = await AccountantPackageFacts.CurrentIssuerAsync(db, scope.OrgId, cancellationToken);
        var guestDocuments = await AccountantPackageFacts.ReadAsync(db, scope, period, FiscalDocumentPurpose.GuestStay, cancellationToken);
        var swissTax = await AccountantPackageFacts.SwissTouristTaxAsync(db, scope.OrgId, guestDocuments, cancellationToken);

        var figures = performance.Total.Figures;
        var summary = new AgencySummary(
            period,
            timeProvider.TodayInRomeAsDateOnly(),
            ownerAccounting,
            new AgencyStaysFigures(figures.OccupiedNights, figures.AvailableNights, figures.OccupancyRate, figures.Revenue, figures.StayCount, figures.RevenueNights, figures.Adr, figures.RevPar),
            new AgencyCollectionsFigures(collected.Total.Amount, collected.Total.PaymentCount, balances.Totals),
            GuestDocumentRows(guestDocuments, fallback),
            [.. costs.Rows.Select(PortalCostOf), PortalCostOf(costs.Total)],
            new AgencyTouristTax(new AgencyItalianTouristTax(tax.Total.Owed, tax.Total.Paid, tax.Total.Balance, tax.Total.StaysWithoutAmount), swissTax),
            [],
            new AgencyOwnerInvoices([], []),
            [],
            [],
            [],
            [],
            []);
        return ownerAccounting ? await WithOwnerAccountingAsync(summary, scope, period, cancellationToken) : summary;
    }

    /// <summary>The owner part of the summary: the commissions and the invoices of the agency to the owners, the payouts, the wires, the portal commissions it bears, the margin and the report by owner.</summary>
    private async Task<AgencySummary> WithOwnerAccountingAsync(AgencySummary summary, HostScope scope, AccountantPackagePeriod period, CancellationToken cancellationToken)
    {
        var orgId = scope.OrgId;
        var invoices = await AccountantPackageFacts.ReadAsync(db, scope, period, FiscalDocumentPurpose.OwnerInvoice, cancellationToken);
        var names = await AccountantPackageFacts.OwnerNamesAsync(
            db, orgId, [.. invoices.Where(d => d.RecipientOwnerId is not null).Select(d => d.RecipientOwnerId!.Value).Distinct()], cancellationToken);
        var commissions = CommissionRows(invoices, names);
        var borne = await AccountantPackageFacts.PortalCommissionsBorneAsync(db, holders, orgId, period, cancellationToken);
        var report = await ownerReports.GetByOwnerAsync(scope, new HostPerformanceQuery(period.From, period.To), cancellationToken);

        return summary with
        {
            Commissions = commissions,
            OwnerInvoices = OwnerInvoicesOf(invoices),
            Payouts = await PayoutsAsync(orgId, period, cancellationToken),
            Wires = await WiresAsync(orgId, period, cancellationToken),
            PortalCommissionsBorne = borne,
            Margin = MarginOf(commissions, borne),
            Owners = [.. report.Rows.Select(OwnerRevenueOf), OwnerRevenueOf(report.Unassigned)],
        };
    }

    // ---------------------------------------------------------------- documents

    /// <summary>The documents of the guests for each issuer, kind and currency: how many and their stored amounts (a credit note negative).</summary>
    internal static IReadOnlyList<AgencyGuestDocumentRow> GuestDocumentRows(IReadOnlyList<PackageDocument> documents, FiscalIssuerSnapshot fallback) =>
        documents
            .Select(d => (Document: d, Issuer: AccountantPackageFacts.IssuerOf(d.IssuerSnapshot, fallback)))
            .GroupBy(x => (x.Issuer, x.Document.Kind, x.Document.Currency))
            .Select(g => new AgencyGuestDocumentRow(
                g.Key.Issuer,
                g.Key.Kind,
                g.Key.Currency,
                g.Count(),
                new AgencyAmounts(
                    g.Sum(x => x.Document.Sign * x.Document.TaxableBase),
                    g.Sum(x => x.Document.Sign * x.Document.VatAmount),
                    g.Sum(x => x.Document.Sign * x.Document.Total))))
            .OrderBy(r => r.Issuer.Key, StringComparer.Ordinal)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ThenBy(r => r.Kind)
            .ToList();

    /// <summary>
    /// The commission earned from each owner: the lines of the type commission (and of the recharges, and the expenses advanced) of the owner invoices issued in the period, with their
    /// stored VAT, credit notes taken away.
    /// </summary>
    internal static IReadOnlyList<AgencyCommissionRow> CommissionRows(IReadOnlyList<PackageDocument> invoices, IReadOnlyDictionary<Guid, string?> names) =>
        invoices
            .Where(d => d.RecipientOwnerId is not null)
            .GroupBy(d => (OwnerId: d.RecipientOwnerId!.Value, d.Currency))
            .Select(g => new AgencyCommissionRow(
                g.Key.OwnerId,
                names.GetValueOrDefault(g.Key.OwnerId),
                g.Key.Currency,
                g.Count(),
                AmountsOf(g, FiscalLineType.Commission),
                AmountsOf(g, FiscalLineType.Recharge),
                g.Sum(d => d.Sign * d.Lines.Where(l => l.LineType == FiscalLineType.AdvancedExpense).Sum(l => l.TotalPrice)),
                g.Sum(d => d.Sign * d.Total)))
            .OrderBy(r => r.OwnerName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.OwnerId)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();

    private static AgencyAmounts AmountsOf(IEnumerable<PackageDocument> documents, FiscalLineType type)
    {
        var lines = documents.SelectMany(d => d.Lines.Where(l => l.LineType == type).Select(l => (Document: d, Line: l))).ToList();
        return AgencyAmounts.Of(
            lines.Sum(x => x.Document.Sign * x.Line.TotalPrice),
            lines.Sum(x => x.Document.Sign * AccountantPackageFacts.VatOfLine(x.Line)));
    }

    /// <summary>The owner invoices issued in the period: how many invoices and credit notes for each currency, and the totals for each currency and rate (credit notes taken away).</summary>
    internal static AgencyOwnerInvoices OwnerInvoicesOf(IReadOnlyList<PackageDocument> invoices)
    {
        var counts = invoices
            .GroupBy(d => d.Currency)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AgencyOwnerInvoiceCount(g.Key, g.Count(d => d.Kind != FiscalDocumentKind.CreditNote), g.Count(d => d.Kind == FiscalDocumentKind.CreditNote)))
            .ToList();
        var rates = invoices
            .SelectMany(d => AccountantPackageFacts.VatOf(d).Select(r => (Document: d, Row: r)))
            .GroupBy(x => (x.Document.Currency, x.Row.RatePercent, Nature: x.Row.RatePercent == 0m ? x.Row.NatureCode : null))
            .OrderBy(g => g.Key.Currency, StringComparer.Ordinal)
            .ThenByDescending(g => g.Key.RatePercent ?? -1m)
            .ThenBy(g => g.Key.Nature, StringComparer.Ordinal)
            .Select(g => new AgencyRateRow(
                g.Key.Currency,
                g.Key.RatePercent,
                g.Key.Nature,
                AgencyAmounts.Of(g.Sum(x => x.Document.Sign * x.Row.Taxable), g.Sum(x => x.Document.Sign * x.Row.Vat))))
            .ToList();
        return new AgencyOwnerInvoices(counts, rates);
    }

    // ---------------------------------------------------------------- the owner ledger side: payouts, wires, margin

    /// <summary>The payouts to the owners paid on a day of the period and not reversed, for each currency.</summary>
    private async Task<IReadOnlyList<AgencyPayoutRow>> PayoutsAsync(Guid orgId, AccountantPackagePeriod period, CancellationToken cancellationToken)
    {
        var from = period.From;
        var to = period.To;
        var payouts = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Status == OwnerPayoutStatus.Confirmed && p.PaidOn >= from && p.PaidOn <= to)
            .Select(p => new { p.Currency, p.GrossAmount, p.WithholdingAmount, p.NetAmount })
            .ToListAsync(cancellationToken);
        return payouts
            .GroupBy(p => p.Currency)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AgencyPayoutRow(g.Key, g.Count(), g.Sum(p => p.GrossAmount), g.Sum(p => p.WithholdingAmount), g.Sum(p => p.NetAmount)))
            .ToList();
    }

    /// <summary>The wire batches made on a day of the period, for each currency and state. The file of a batch, its IBAN and its owners' names are never read.</summary>
    private async Task<IReadOnlyList<AgencyWireRow>> WiresAsync(Guid orgId, AccountantPackagePeriod period, CancellationToken cancellationToken)
    {
        var start = RomeCalendar.StartOfDayUtc(period.From);
        var end = RomeCalendar.StartOfDayUtc(period.To.AddDays(1));
        var batches = await db.WireBatches.AsNoTracking()
            .Where(b => b.OrgId == orgId && b.CreatedAt >= start && b.CreatedAt < end)
            .Select(b => new { b.Currency, b.Status, b.TransferCount, b.TotalAmount })
            .ToListAsync(cancellationToken);
        return batches
            .GroupBy(b => (b.Currency, b.Status))
            .OrderBy(g => g.Key.Currency, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Status)
            .Select(g => new AgencyWireRow(g.Key.Currency, g.Key.Status, g.Count(), g.Sum(b => b.TransferCount), g.Sum(b => b.TotalAmount)))
            .ToList();
    }

    /// <summary>The margin for each currency: the taxable commissions and recharges invoiced, less the portal commissions the agency bears.</summary>
    internal static IReadOnlyList<AgencyMarginRow> MarginOf(IReadOnlyList<AgencyCommissionRow> commissions, IReadOnlyList<AgencyPortalCommissionBorneRow> borne) =>
        commissions.Select(c => c.Currency)
            .Concat(borne.Select(b => b.Currency))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(currency =>
            {
                var invoiced = commissions.Where(c => c.Currency == currency).Sum(c => c.Commission.Taxable);
                var recharged = commissions.Where(c => c.Currency == currency).Sum(c => c.Recharges.Taxable);
                var cost = borne.Where(b => b.Currency == currency).Sum(b => b.Amount);
                return new AgencyMarginRow(currency, invoiced, recharged, cost, invoiced + recharged - cost);
            })
            .ToList();

    // ---------------------------------------------------------------- the reports' rows as the summary holds them

    private static AgencyPortalCostRow PortalCostOf(ChannelCostRow row) =>
        new(row.Source, row.Kind, row.StayCount, row.Gross, row.Commission, row.Net, row.CostRate);

    private static AgencyOwnerRevenueRow OwnerRevenueOf(OwnerPerformanceRow row) =>
        new(row.OwnerId, row.DisplayName, row.IsAnonymized, row.PropertyCount, row.Figures);
}
