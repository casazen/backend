using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// The agency summary of a period (<c>GET /api/accountant-package/agency-summary</c>, E15/F01/US01/T01): the same figures the ZIP holds in <c>riepilogo-agenzia/</c>, because
/// both are made from the one <see cref="AgencySummary"/>. Amounts are numbers to the cent in the currency named beside them (credit notes are negative, a figure nobody reported is
/// <c>null</c>); every word is a key (the kinds, the channels, the states), never a text to show. The fields of the owner accounting are empty arrays, and
/// <c>ownerAccountingIncluded</c> is false, for a caller who may not read it. Definitions: <c>docs/TECHNICAL.md</c>, section «Accountant package».
/// </summary>
public sealed class AgencySummaryDto
{
    public string Period { get; init; } = string.Empty;

    public string PeriodKind { get; init; } = string.Empty;

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public DateOnly GeneratedOn { get; init; }

    public bool OwnerAccountingIncluded { get; init; }

    public AgencyStaysDto Stays { get; init; } = new();

    public AgencyCollectionsDto Collections { get; init; } = new();

    public IReadOnlyList<AgencyGuestDocumentsDto> GuestDocuments { get; init; } = [];

    public IReadOnlyList<AgencyPortalCostDto> PortalCosts { get; init; } = [];

    public AgencyTouristTaxDto TouristTax { get; init; } = new();

    public IReadOnlyList<AgencyCommissionDto> Commissions { get; init; } = [];

    public AgencyOwnerInvoicesDto OwnerInvoices { get; init; } = new();

    public IReadOnlyList<AgencyPayoutDto> Payouts { get; init; } = [];

    public IReadOnlyList<AgencyWireDto> Wires { get; init; } = [];

    public IReadOnlyList<AgencyPortalCommissionBorneDto> PortalCommissionsBorne { get; init; } = [];

    public IReadOnlyList<AgencyMarginDto> Margin { get; init; } = [];

    public IReadOnlyList<AgencyOwnerRevenueDto> Owners { get; init; } = [];

    public static AgencySummaryDto Of(AgencySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new AgencySummaryDto
        {
            Period = summary.Period.Key,
            PeriodKind = Key(summary.Period.Kind),
            From = summary.Period.From,
            To = summary.Period.To,
            GeneratedOn = summary.GeneratedOn,
            OwnerAccountingIncluded = summary.OwnerAccountingIncluded,
            Stays = AgencyStaysDto.From(summary.Stays),
            Collections = AgencyCollectionsDto.From(summary.Collections),
            GuestDocuments = [.. summary.GuestDocuments.Select(AgencyGuestDocumentsDto.From)],
            PortalCosts = [.. summary.PortalCosts.Select(AgencyPortalCostDto.From)],
            TouristTax = AgencyTouristTaxDto.From(summary.TouristTax),
            Commissions = [.. summary.Commissions.Select(AgencyCommissionDto.From)],
            OwnerInvoices = AgencyOwnerInvoicesDto.From(summary.OwnerInvoices),
            Payouts = [.. summary.Payouts.Select(AgencyPayoutDto.From)],
            Wires = [.. summary.Wires.Select(AgencyWireDto.From)],
            PortalCommissionsBorne = [.. summary.PortalCommissionsBorne.Select(AgencyPortalCommissionBorneDto.From)],
            Margin = [.. summary.Margin.Select(AgencyMarginDto.From)],
            Owners = [.. summary.Owners.Select(AgencyOwnerRevenueDto.From)],
        };
    }

    internal static string Key<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

/// <summary>A taxable amount, its VAT and their total (the total the stored one for a document).</summary>
public sealed class AgencyAmountsDto
{
    public decimal Taxable { get; init; }

    public decimal Vat { get; init; }

    public decimal Total { get; init; }

    public static AgencyAmountsDto From(AgencyAmounts amounts) => new() { Taxable = amounts.Taxable, Vat = amounts.Vat, Total = amounts.Total };
}

public sealed class AgencyStaysDto
{
    public int OccupiedNights { get; init; }

    public int AvailableNights { get; init; }

    public decimal? OccupancyRate { get; init; }

    public decimal Revenue { get; init; }

    public int Stays { get; init; }

    public int RevenueNights { get; init; }

    public decimal? Adr { get; init; }

    public decimal? RevPar { get; init; }

    public static AgencyStaysDto From(AgencyStaysFigures figures) => new()
    {
        OccupiedNights = figures.OccupiedNights,
        AvailableNights = figures.AvailableNights,
        OccupancyRate = figures.OccupancyRate,
        Revenue = figures.Revenue,
        Stays = figures.Stays,
        RevenueNights = figures.RevenueNights,
        Adr = figures.Adr,
        RevPar = figures.RevPar,
    };
}

public sealed class AgencyCollectionsDto
{
    public decimal Collected { get; init; }

    public int Payments { get; init; }

    public IReadOnlyList<AgencyBalanceDto> ToCollect { get; init; } = [];

    public static AgencyCollectionsDto From(AgencyCollectionsFigures figures) => new()
    {
        Collected = figures.Collected,
        Payments = figures.Payments,
        ToCollect = [.. figures.ToCollect.Select(b => new AgencyBalanceDto { Currency = b.Currency, Amount = b.Amount, Stays = b.StayCount })],
    };
}

public sealed class AgencyBalanceDto
{
    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public int Stays { get; init; }
}

public sealed class AgencyIssuerDto
{
    public string Name { get; init; } = string.Empty;

    public string CountryCode { get; init; } = string.Empty;

    /// <summary>The Swiss VAT number, the Italian VAT number or the fiscal code; <c>null</c> when the issuer has none.</summary>
    public string? TaxId { get; init; }

    public static AgencyIssuerDto From(AgencyIssuer issuer) => new() { Name = issuer.Name, CountryCode = issuer.CountryCode, TaxId = issuer.TaxId };
}

public sealed class AgencyGuestDocumentsDto
{
    public AgencyIssuerDto Issuer { get; init; } = new();

    public string Kind { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public int Documents { get; init; }

    public AgencyAmountsDto Amounts { get; init; } = new();

    public static AgencyGuestDocumentsDto From(AgencyGuestDocumentRow row) => new()
    {
        Issuer = AgencyIssuerDto.From(row.Issuer),
        Kind = AgencySummaryDto.Key(row.Kind),
        Currency = row.Currency,
        Documents = row.Documents,
        Amounts = AgencyAmountsDto.From(row.Amounts),
    };
}

public sealed class AgencyPortalCostDto
{
    public string? Source { get; init; }

    public string? Kind { get; init; }

    public int Stays { get; init; }

    public decimal Gross { get; init; }

    public decimal? Cost { get; init; }

    public decimal? Net { get; init; }

    public decimal? CostRate { get; init; }

    public static AgencyPortalCostDto From(AgencyPortalCostRow row) => new()
    {
        Source = row.Source is { } source ? AgencySummaryDto.Key(source) : null,
        Kind = row.Kind is { } kind ? AgencySummaryDto.Key(kind) : null,
        Stays = row.Stays,
        Gross = row.Gross,
        Cost = row.Cost,
        Net = row.Net,
        CostRate = row.CostRate,
    };
}

public sealed class AgencyTouristTaxDto
{
    public AgencyItalianTouristTaxDto Italy { get; init; } = new();

    public IReadOnlyList<AgencySwissTouristTaxDto> Switzerland { get; init; } = [];

    public static AgencyTouristTaxDto From(AgencyTouristTax tax) => new()
    {
        Italy = new AgencyItalianTouristTaxDto
        {
            Owed = tax.Italy.Owed,
            Paid = tax.Italy.Paid,
            ToRemit = tax.Italy.ToRemit,
            StaysWithoutAmount = tax.Italy.StaysWithoutAmount,
        },
        Switzerland = [.. tax.Switzerland.Select(r => new AgencySwissTouristTaxDto { Canton = r.Canton, Currency = r.Currency, Documents = r.Documents, Collected = r.Collected })],
    };
}

public sealed class AgencyItalianTouristTaxDto
{
    public decimal Owed { get; init; }

    public decimal? Paid { get; init; }

    public decimal? ToRemit { get; init; }

    public int StaysWithoutAmount { get; init; }
}

public sealed class AgencySwissTouristTaxDto
{
    public string Canton { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public int Documents { get; init; }

    public decimal Collected { get; init; }
}

public sealed class AgencyCommissionDto
{
    public Guid OwnerId { get; init; }

    public string? OwnerName { get; init; }

    public string Currency { get; init; } = string.Empty;

    public int Documents { get; init; }

    public AgencyAmountsDto Commission { get; init; } = new();

    public AgencyAmountsDto Recharges { get; init; } = new();

    /// <summary>The expenses advanced in the name of the owner, outside the VAT.</summary>
    public decimal AdvancedExpenses { get; init; }

    public decimal InvoicedTotal { get; init; }

    public static AgencyCommissionDto From(AgencyCommissionRow row) => new()
    {
        OwnerId = row.OwnerId,
        OwnerName = row.OwnerName,
        Currency = row.Currency,
        Documents = row.Documents,
        Commission = AgencyAmountsDto.From(row.Commission),
        Recharges = AgencyAmountsDto.From(row.Recharges),
        AdvancedExpenses = row.AdvancedExpenses,
        InvoicedTotal = row.InvoicedTotal,
    };
}

public sealed class AgencyOwnerInvoicesDto
{
    public IReadOnlyList<AgencyOwnerInvoiceCountDto> Counts { get; init; } = [];

    /// <summary>The totals for each currency and VAT rate, credit notes taken away.</summary>
    public IReadOnlyList<AgencyRateDto> Rates { get; init; } = [];

    public static AgencyOwnerInvoicesDto From(AgencyOwnerInvoices invoices) => new()
    {
        Counts = [.. invoices.Counts.Select(c => new AgencyOwnerInvoiceCountDto { Currency = c.Currency, Invoices = c.Invoices, CreditNotes = c.CreditNotes })],
        Rates = [.. invoices.Rates.Select(r => new AgencyRateDto
        {
            Currency = r.Currency,
            RatePercent = r.RatePercent,
            NatureCode = r.NatureCode,
            Amounts = AgencyAmountsDto.From(r.Amounts),
        })],
    };
}

public sealed class AgencyOwnerInvoiceCountDto
{
    public string Currency { get; init; } = string.Empty;

    public int Invoices { get; init; }

    public int CreditNotes { get; init; }
}

public sealed class AgencyRateDto
{
    public string Currency { get; init; } = string.Empty;

    /// <summary>The rate in percent; <c>0</c> with a nature code for an operation without VAT; <c>null</c> when a document does not state it.</summary>
    public decimal? RatePercent { get; init; }

    public string? NatureCode { get; init; }

    public AgencyAmountsDto Amounts { get; init; } = new();
}

public sealed class AgencyPayoutDto
{
    public string Currency { get; init; } = string.Empty;

    public int Payouts { get; init; }

    public decimal Gross { get; init; }

    public decimal Withholding { get; init; }

    public decimal Net { get; init; }

    public static AgencyPayoutDto From(AgencyPayoutRow row) => new()
    {
        Currency = row.Currency,
        Payouts = row.Payouts,
        Gross = row.Gross,
        Withholding = row.Withholding,
        Net = row.Net,
    };
}

public sealed class AgencyWireDto
{
    public string Currency { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public int Batches { get; init; }

    public int Transfers { get; init; }

    public decimal Total { get; init; }

    public static AgencyWireDto From(AgencyWireRow row) => new()
    {
        Currency = row.Currency,
        Status = AgencySummaryDto.Key(row.Status),
        Batches = row.Batches,
        Transfers = row.Transfers,
        Total = row.Total,
    };
}

public sealed class AgencyPortalCommissionBorneDto
{
    public string Source { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public static AgencyPortalCommissionBorneDto From(AgencyPortalCommissionBorneRow row) => new()
    {
        Source = AgencySummaryDto.Key(row.Source),
        Currency = row.Currency,
        Amount = row.Amount,
    };
}

public sealed class AgencyMarginDto
{
    public string Currency { get; init; } = string.Empty;

    public decimal Commissions { get; init; }

    public decimal Recharges { get; init; }

    public decimal PortalCommissionsBorne { get; init; }

    public decimal Margin { get; init; }

    public static AgencyMarginDto From(AgencyMarginRow row) => new()
    {
        Currency = row.Currency,
        Commissions = row.Commissions,
        Recharges = row.Recharges,
        PortalCommissionsBorne = row.PortalCommissionsBorne,
        Margin = row.Margin,
    };
}

public sealed class AgencyOwnerRevenueDto
{
    public Guid? OwnerId { get; init; }

    public string? OwnerName { get; init; }

    public bool Anonymized { get; init; }

    public int Properties { get; init; }

    public decimal Revenue { get; init; }

    public decimal OccupiedNights { get; init; }

    public decimal AvailableNights { get; init; }

    public decimal? OccupancyRate { get; init; }

    public decimal Stays { get; init; }

    public static AgencyOwnerRevenueDto From(AgencyOwnerRevenueRow row) => new()
    {
        OwnerId = row.OwnerId,
        OwnerName = row.OwnerName,
        Anonymized = row.Anonymized,
        Properties = row.Properties,
        Revenue = row.Figures.Revenue,
        OccupiedNights = row.Figures.OccupiedNights,
        AvailableNights = row.Figures.AvailableNights,
        OccupancyRate = row.Figures.OccupancyRate,
        Stays = row.Figures.StayCount,
    };
}
