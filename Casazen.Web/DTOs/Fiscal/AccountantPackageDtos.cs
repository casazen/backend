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
    /// <summary>The period as asked: <c>2026-10</c> or <c>2026</c>.</summary>
    public string Period { get; init; } = string.Empty;

    /// <summary><c>month</c> or <c>year</c> (the summary of a year is the annual summary of the commissions).</summary>
    public string PeriodKind { get; init; } = string.Empty;

    /// <summary>The first day of the period (Europe/Rome).</summary>
    public DateOnly From { get; init; }

    /// <summary>The last day of the period, included.</summary>
    public DateOnly To { get; init; }

    /// <summary>The day the figures were read (Europe/Rome).</summary>
    public DateOnly GeneratedOn { get; init; }

    /// <summary>False when the caller may not read the owner accounting (<c>agency.statement.view</c> and the whole org): the owner fields are empty.</summary>
    public bool OwnerAccountingIncluded { get; init; }

    public AgencyStaysDto Stays { get; init; } = new();

    public AgencyCollectionsDto Collections { get; init; } = new();

    /// <summary>The documents issued to the guests, for each issuer, kind and currency (a credit note negative).</summary>
    public IReadOnlyList<AgencyGuestDocumentsDto> GuestDocuments { get; init; } = [];

    /// <summary>The costs of the channels as the channel costs report says them; the last row (<c>source</c> null) is the total.</summary>
    public IReadOnlyList<AgencyPortalCostDto> PortalCosts { get; init; } = [];

    public AgencyTouristTaxDto TouristTax { get; init; } = new();

    /// <summary>The commission earned from each owner by the invoices issued in the period (owner accounting).</summary>
    public IReadOnlyList<AgencyCommissionDto> Commissions { get; init; } = [];

    public AgencyOwnerInvoicesDto OwnerInvoices { get; init; } = new();

    /// <summary>The payouts to the owners paid on a day of the period (owner accounting).</summary>
    public IReadOnlyList<AgencyPayoutDto> Payouts { get; init; } = [];

    /// <summary>The wire batches made in the period, for each currency and state (owner accounting).</summary>
    public IReadOnlyList<AgencyWireDto> Wires { get; init; } = [];

    /// <summary>The portal commissions of the listings whose account the agency holds, for each channel and currency, for information (owner accounting).</summary>
    public IReadOnlyList<AgencyPortalCommissionBorneDto> PortalCommissionsBorne { get; init; } = [];

    /// <summary>The margin for each currency: commissions and recharges invoiced, less the portal commissions the agency bears (owner accounting).</summary>
    public IReadOnlyList<AgencyMarginDto> Margin { get; init; } = [];

    /// <summary>The performance of the properties of each owner, and of the properties nobody owns (<c>ownerId</c> null) (owner accounting).</summary>
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

    /// <summary>The key of an enum value: its name in camel case (<c>creditNote</c>, <c>bookingCom</c>), the way the rest of the API names a kind.</summary>
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

/// <summary>Occupancy and revenue of the period, as the Home counts them (by accrual, the tourist tax excluded).</summary>
public sealed class AgencyStaysDto
{
    public int OccupiedNights { get; init; }

    public int AvailableNights { get; init; }

    /// <summary>Occupied over available, 0 to 1; <c>null</c> when nothing is available.</summary>
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

/// <summary>What was collected in the period (by cash, net of refunds) and what is still to collect, for each currency, as of the day the summary was read.</summary>
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

/// <summary>What is still to collect in one currency.</summary>
public sealed class AgencyBalanceDto
{
    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public int Stays { get; init; }
}

/// <summary>An issuer of fiscal documents.</summary>
public sealed class AgencyIssuerDto
{
    public string Name { get; init; } = string.Empty;

    /// <summary><c>IT</c> or <c>CH</c>.</summary>
    public string CountryCode { get; init; } = string.Empty;

    /// <summary>The Swiss VAT number, the Italian VAT number or the fiscal code; <c>null</c> when the issuer has none.</summary>
    public string? TaxId { get; init; }

    public static AgencyIssuerDto From(AgencyIssuer issuer) => new() { Name = issuer.Name, CountryCode = issuer.CountryCode, TaxId = issuer.TaxId };
}

/// <summary>The documents an issuer made for the guests, for one kind and currency.</summary>
public sealed class AgencyGuestDocumentsDto
{
    public AgencyIssuerDto Issuer { get; init; } = new();

    /// <summary><c>invoice</c>, <c>receipt</c> or <c>creditNote</c> (negative amounts).</summary>
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

/// <summary>What a channel costs in the period, as the channel costs report says it.</summary>
public sealed class AgencyPortalCostDto
{
    /// <summary>The channel (<c>bookingCom</c>, <c>airbnb</c>, <c>direct</c>...); <c>null</c> for the total.</summary>
    public string? Source { get; init; }

    /// <summary><c>channelCommission</c>, <c>paymentFee</c> or <c>noCost</c>; <c>null</c> for the total.</summary>
    public string? Kind { get; init; }

    public int Stays { get; init; }

    public decimal Gross { get; init; }

    /// <summary>The cost the channel manager or the provider reported; <c>null</c> when nobody did (never zero).</summary>
    public decimal? Cost { get; init; }

    public decimal? Net { get; init; }

    /// <summary>The cost over the gross of the stays that report it, 0 to 1.</summary>
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

/// <summary>The tourist tax of the period: Italy by the tourist tax summary, Switzerland by canton from the documents.</summary>
public sealed class AgencyTouristTaxDto
{
    public AgencyItalianTouristTaxDto Italy { get; init; } = new();

    /// <summary>The Swiss tax the documents collected for each canton and currency; what was remitted is not recorded in CasaZen.</summary>
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

/// <summary>The Italian tourist tax in euros: what the stays record as due, what was remitted and what is left to remit (<c>null</c> for a caller who reaches only some properties).</summary>
public sealed class AgencyItalianTouristTaxDto
{
    public decimal Owed { get; init; }

    public decimal? Paid { get; init; }

    public decimal? ToRemit { get; init; }

    /// <summary>The stays with no amount recorded: above zero, <c>owed</c> may be incomplete.</summary>
    public int StaysWithoutAmount { get; init; }
}

/// <summary>The Swiss tourist tax collected for a canton in a currency (an empty canton when the property has no municipality).</summary>
public sealed class AgencySwissTouristTaxDto
{
    public string Canton { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public int Documents { get; init; }

    public decimal Collected { get; init; }
}

/// <summary>The commission the agency earned from one owner in a currency, as the invoices of the period say it.</summary>
public sealed class AgencyCommissionDto
{
    public Guid OwnerId { get; init; }

    /// <summary><c>null</c> for an owner whose data was anonymized.</summary>
    public string? OwnerName { get; init; }

    public string Currency { get; init; } = string.Empty;

    /// <summary>The invoices and credit notes issued to the owner in the period.</summary>
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

/// <summary>The invoices of the agency to the owners issued in the period.</summary>
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

/// <summary>How many invoices and credit notes were issued to the owners in a currency.</summary>
public sealed class AgencyOwnerInvoiceCountDto
{
    public string Currency { get; init; } = string.Empty;

    public int Invoices { get; init; }

    public int CreditNotes { get; init; }
}

/// <summary>One VAT rate of a set of documents.</summary>
public sealed class AgencyRateDto
{
    public string Currency { get; init; } = string.Empty;

    /// <summary>The rate in percent; <c>0</c> with a nature code for an operation without VAT; <c>null</c> when a document does not state it.</summary>
    public decimal? RatePercent { get; init; }

    public string? NatureCode { get; init; }

    public AgencyAmountsDto Amounts { get; init; } = new();
}

/// <summary>The payouts to the owners confirmed in the period in a currency.</summary>
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

/// <summary>The wire batches made in the period in a currency, by state.</summary>
public sealed class AgencyWireDto
{
    public string Currency { get; init; } = string.Empty;

    /// <summary><c>generated</c>, <c>downloaded</c> or <c>paid</c>.</summary>
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

/// <summary>The portal commissions of the accounts the agency holds, for one channel and currency.</summary>
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

/// <summary>The margin of the agency in a currency.</summary>
public sealed class AgencyMarginDto
{
    public string Currency { get; init; } = string.Empty;

    /// <summary>The taxable commissions invoiced to the owners.</summary>
    public decimal Commissions { get; init; }

    /// <summary>The taxable recharges invoiced to the owners.</summary>
    public decimal Recharges { get; init; }

    /// <summary>The commissions of the portals whose account the agency holds.</summary>
    public decimal PortalCommissionsBorne { get; init; }

    /// <summary><c>commissions + recharges - portalCommissionsBorne</c>.</summary>
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

/// <summary>The performance of the properties of one owner (weighted by the share it holds), as the report by owner says it.</summary>
public sealed class AgencyOwnerRevenueDto
{
    /// <summary>The owner; <c>null</c> for the properties nobody owns.</summary>
    public Guid? OwnerId { get; init; }

    /// <summary><c>null</c> for an owner whose data was anonymized and for the row of the properties nobody owns.</summary>
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
