using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>The two lengths of an accountant package: a calendar month or a calendar year.</summary>
public enum AccountantPackagePeriodKind
{
    /// <summary>A month (<c>2026-10</c>).</summary>
    Month = 1,

    /// <summary>A year (<c>2026</c>): the agency's annual summary of commissions comes with it.</summary>
    Year = 2,
}

/// <summary>
/// The period of an accountant package (E15/F01/US01/T01, NF-101): a month (<c>yyyy-MM</c>) or a year (<c>yyyy</c>), at most twelve months, as the first and the
/// last day (Europe/Rome calendar days, both included). Built only by <see cref="TryParse"/>, <see cref="OfMonth"/> and <see cref="OfYear"/>, so the two days
/// always are the ones of the key.
/// </summary>
public sealed record AccountantPackagePeriod
{
    private AccountantPackagePeriod(AccountantPackagePeriodKind kind, DateOnly from, DateOnly to)
    {
        Kind = kind;
        From = from;
        To = to;
    }

    /// <summary>A month or a year.</summary>
    public AccountantPackagePeriodKind Kind { get; }

    /// <summary>The first day of the period.</summary>
    public DateOnly From { get; }

    /// <summary>The last day of the period, included.</summary>
    public DateOnly To { get; }

    /// <summary>The words of the request: <c>2026-10</c> for a month, <c>2026</c> for a year.</summary>
    public string Key => From.ToString(Kind == AccountantPackagePeriodKind.Month ? "yyyy-MM" : "yyyy", CultureInfo.InvariantCulture);

    /// <summary>The name of the ZIP: <c>casazen-commercialista-2026-10.zip</c>.</summary>
    public string FileName => string.Create(CultureInfo.InvariantCulture, $"casazen-commercialista-{Key}.zip");

    /// <summary>The days as the period of the fiscal reports.</summary>
    public FiscalReportPeriod FiscalPeriod => new(From, To);

    /// <summary>The days as what the host reports ask for (no property: the whole reach of the caller).</summary>
    public HostReportQuery ReportQuery => new(From, To);

    /// <summary>The months of the period, in order: the statements, the withholding and the F24 of the owners are counted by month.</summary>
    public IReadOnlyList<(int Year, int Month)> Months
    {
        get
        {
            var months = new List<(int Year, int Month)>();
            for (var day = new DateOnly(From.Year, From.Month, 1); day <= To; day = day.AddMonths(1))
                months.Add((day.Year, day.Month));

            return months;
        }
    }

    /// <summary>True when <paramref name="day"/> is one of the days of the period.</summary>
    public bool Contains(DateOnly day) => day >= From && day <= To;

    /// <summary>True when the statement month is one of the months of the period.</summary>
    public bool ContainsMonth(int year, int month) => (year * 100) + month >= (From.Year * 100) + From.Month && (year * 100) + month <= (To.Year * 100) + To.Month;

    /// <summary>The first year and the last a package may name: the years of the host reports.</summary>
    public const int MinYear = HostDashboardPeriod.MinCustomYear;

    /// <inheritdoc cref="MinYear"/>
    public const int MaxYear = HostDashboardPeriod.MaxCustomYear;

    /// <summary>A month of a year the reports support.</summary>
    public static AccountantPackagePeriod OfMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, MinYear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, MaxYear);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);

        var first = new DateOnly(year, month, 1);
        return new AccountantPackagePeriod(AccountantPackagePeriodKind.Month, first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>A year of the years the reports support.</summary>
    public static AccountantPackagePeriod OfYear(int year)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, MinYear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, MaxYear);

        return new AccountantPackagePeriod(AccountantPackagePeriodKind.Year, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
    }

    /// <summary>
    /// The period the words name: exactly <c>yyyy-MM</c> (a month from 01 to 12) or <c>yyyy</c>, of a year from 2000 to 2100. Anything else (a missing word, a
    /// blank, <c>2026-1</c>, <c>2026-13</c>, a date, a number of months) is not a period: <c>false</c>.
    /// </summary>
    public static bool TryParse(string? text, out AccountantPackagePeriod period)
    {
        period = null!;
        var word = text?.Trim();
        if (string.IsNullOrEmpty(word))
            return false;

        if (word.Length == 4 && word.All(char.IsAsciiDigit))
        {
            var year = int.Parse(word, NumberStyles.None, CultureInfo.InvariantCulture);
            if (year is < MinYear or > MaxYear)
                return false;

            period = OfYear(year);
            return true;
        }

        if (word.Length == 7 && word[4] == '-' && word.Where((c, i) => i != 4).All(char.IsAsciiDigit))
        {
            var year = int.Parse(word[..4], NumberStyles.None, CultureInfo.InvariantCulture);
            var month = int.Parse(word[5..], NumberStyles.None, CultureInfo.InvariantCulture);
            if (year is < MinYear or > MaxYear || month is < 1 or > 12)
                return false;

            period = OfMonth(year, month);
            return true;
        }

        return false;
    }
}

/// <summary>
/// What the caller may read of the package, decided by the web layer from the permissions of the caller (the service never checks roles): the owner accounting
/// (the statements of the owners, the invoices of the agency to them, the withholding and the owner parts of the agency summary) is read with
/// <c>agency.statement.view</c>. Without it those folders are left out and the index lists them as omitted. The service also leaves them out for a scope
/// that does not reach the whole org (<see cref="AllowsOwnerAccounting"/>): an owner's accounts span properties the caller does not reach.
/// </summary>
/// <param name="OwnerAccounting">The caller may read the owner accounting.</param>
public sealed record AccountantPackageAccess(bool OwnerAccounting)
{
    /// <summary>Everything: the agency (the job that sends the package to its accountant runs with this and the whole org).</summary>
    public static AccountantPackageAccess Full { get; } = new(true);

    /// <summary>What <c>payment.read</c> alone opens: the documents of the guests, the VAT, the tourist tax and the collections.</summary>
    public static AccountantPackageAccess WithoutOwnerAccounting { get; } = new(false);

    /// <summary>The owner accounting is read when the caller may and its reach is the whole org (never a part of an owner's accounts).</summary>
    public bool AllowsOwnerAccounting(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return OwnerAccounting && scope.IsOrgWide;
    }
}

/// <summary>What a package wrote: the documents it holds, the files, their size, and the folders it left out for lack of permission (for the line of the log).</summary>
/// <param name="Documents">The fiscal documents, the statements and the F24 it holds (a PDF with its XML counts once).</param>
/// <param name="Files">Every file of the ZIP, the index included.</param>
/// <param name="Bytes">The bytes of the files before they were compressed.</param>
/// <param name="OmittedFolders">The folders left out, as the index names them.</param>
public sealed record AccountantPackageResult(int Documents, int Files, long Bytes, IReadOnlyList<string> OmittedFolders);

/// <summary>
/// The fixed structure of the package (E15/F01/US01/T01): the folders and the files that are always there, the same whatever the period holds (an empty list is
/// a header-only file). The names are Italian, like the files the accountant opens.
/// </summary>
public static class AccountantPackageStructure
{
    /// <summary>The index at the root: file, description, rows and SHA-256 of every other file, and the folders left out.</summary>
    public const string IndexFile = "indice.csv";

    /// <summary>The issued documents of the guests (PDF, XML when there is one) and their register, one for each issuer.</summary>
    public const string GuestDocumentsFolder = "documenti-ospiti";

    /// <summary>The issued invoices of the agency to the owners (PDF, XML when Italian) and their register. Owner accounting.</summary>
    public const string OwnerInvoicesFolder = "fatture-proprietari";

    /// <summary>The published statements of the owners (PDF and CSV). Owner accounting.</summary>
    public const string StatementsFolder = "rendiconti";

    /// <summary>The agency summary (CSV, Excel and PDF).</summary>
    public const string AgencySummaryFolder = "riepilogo-agenzia";

    /// <summary>The VAT for each rate and issuer: the base of the Swiss quarterly return and the register of the sales in Italy.</summary>
    public const string VatFolder = "iva";

    /// <summary>The withholding of the Italian owners, the F24 data and the Certificazione Unica data. Owner accounting.</summary>
    public const string WithholdingFolder = "ritenute";

    /// <summary>The tourist tax: the summary by comune and the Swiss tax by canton.</summary>
    public const string TouristTaxFolder = "tassa-soggiorno";

    /// <summary>The collections, the balances to collect and the costs of the channels.</summary>
    public const string CollectionsFolder = "incassi";

    /// <summary>The folders that hold the owner accounting, in the order of the index.</summary>
    public static IReadOnlyList<string> OwnerAccountingFolders { get; } = [OwnerInvoicesFolder, StatementsFolder, WithholdingFolder];
}

/// <summary>The stable codes of the errors of the accountant package, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class AccountantPackageErrors
{
    /// <summary>400: <c>period</c> is missing or is neither <c>yyyy-MM</c> nor <c>yyyy</c>.</summary>
    public const string InvalidPeriod = "accountant_package_invalid_period";

    /// <summary>422: the package would hold more files than <c>DocumentArchive:MaxExportFiles</c> or more bytes than <c>DocumentArchive:MaxExportBytes</c> allow.</summary>
    public const string TooLarge = "accountant_package_too_large";
}

// ---------------------------------------------------------------- the agency summary

/// <summary>The three amounts of a figure that carries VAT: the taxable amount, the VAT and the total (their sum). Credit notes are negative.</summary>
public sealed record AgencyAmounts(decimal Taxable, decimal Vat, decimal Total)
{
    /// <summary>Nothing.</summary>
    public static AgencyAmounts Zero { get; } = new(0m, 0m, 0m);

    /// <summary>The amounts of a taxable amount and its VAT.</summary>
    public static AgencyAmounts Of(decimal taxable, decimal vat) => new(taxable, vat, taxable + vat);

    /// <summary>The sum of two sets of amounts.</summary>
    public AgencyAmounts Plus(AgencyAmounts other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new AgencyAmounts(Taxable + other.Taxable, Vat + other.Vat, Total + other.Total);
    }

    /// <summary>The same amounts with the opposite sign: what a credit note takes away.</summary>
    public AgencyAmounts Negated() => new(-Taxable, -Vat, -Total);
}

/// <summary>Who issues the documents: the identity the registers and the VAT tables are told apart by (the issuer of the snapshot a document was issued with).</summary>
/// <param name="Key">The stable key of the issuer: country, tax number and name.</param>
/// <param name="Name">The legal name of the issuer.</param>
/// <param name="CountryCode"><c>IT</c> or <c>CH</c>.</param>
/// <param name="TaxId">The Swiss VAT number (UID), the Italian VAT number or the fiscal code; <c>null</c> when the issuer has none.</param>
/// <param name="Regime">The regime of the issuer; <c>null</c> when it did not say.</param>
public sealed record AgencyIssuer(string Key, string Name, string CountryCode, string? TaxId, DocumentRegime? Regime)
{
    /// <summary>True for an issuer in Switzerland.</summary>
    public bool IsSwiss => string.Equals(CountryCode, "CH", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The commission of the agency to one owner in a currency, as its invoices of the period say it: taxable, VAT and total, with the recharges and the expenses advanced.</summary>
/// <param name="OwnerId">The owner.</param>
/// <param name="OwnerName">The name the agency keeps for the owner; <c>null</c> for an owner whose data was anonymized.</param>
/// <param name="Currency">The currency of the invoices.</param>
/// <param name="Documents">The invoices and credit notes of the owner issued in the period.</param>
/// <param name="Commission">The lines of the commission, credit notes taken away.</param>
/// <param name="Recharges">The lines of the recharges of costs (with VAT), credit notes taken away.</param>
/// <param name="AdvancedExpenses">The expenses advanced in the name of the owner (outside the VAT), credit notes taken away.</param>
/// <param name="InvoicedTotal">What the invoices of the period add up to, credit notes taken away.</param>
public sealed record AgencyCommissionRow(
    Guid OwnerId,
    string? OwnerName,
    string Currency,
    int Documents,
    AgencyAmounts Commission,
    AgencyAmounts Recharges,
    decimal AdvancedExpenses,
    decimal InvoicedTotal);

/// <summary>One VAT rate of a set of documents: its taxable amount and its VAT (credit notes negative). A rate that is not stated is <c>null</c>.</summary>
/// <param name="Currency">The currency of the documents.</param>
/// <param name="RatePercent">The rate in percent; <c>0</c> with a nature code for an operation without VAT; <c>null</c> for a document that does not say it.</param>
/// <param name="NatureCode">The nature code of an operation without VAT.</param>
/// <param name="Amounts">The taxable amount, the VAT and their total.</param>
public sealed record AgencyRateRow(string Currency, decimal? RatePercent, string? NatureCode, AgencyAmounts Amounts);

/// <summary>How many invoices and credit notes the agency issued to its owners in a currency.</summary>
public sealed record AgencyOwnerInvoiceCount(string Currency, int Invoices, int CreditNotes);

/// <summary>The invoices of the agency to the owners issued in the period: how many, and the totals for each VAT rate.</summary>
/// <param name="Counts">The invoices and the credit notes, for each currency.</param>
/// <param name="Rates">The totals for each currency and rate, the highest rate first; credit notes taken away.</param>
public sealed record AgencyOwnerInvoices(IReadOnlyList<AgencyOwnerInvoiceCount> Counts, IReadOnlyList<AgencyRateRow> Rates);

/// <summary>The documents of the guests an issuer made in the period, for one kind and currency: how many and what they add up to (a credit note is negative).</summary>
public sealed record AgencyGuestDocumentRow(AgencyIssuer Issuer, FiscalDocumentKind Kind, string Currency, int Documents, AgencyAmounts Amounts);

/// <summary>What one channel costs in the period, as the channel costs report says it (<see cref="IChannelCostsReportService"/>): never recomputed here.</summary>
/// <param name="Source">The channel; <c>null</c> for the total row.</param>
/// <param name="Kind">What the cost is; <c>null</c> for the total row.</param>
/// <param name="Stays">The confirmed stays with a night in the period.</param>
/// <param name="Gross">The revenue of those stays.</param>
/// <param name="Cost">The commission or the card fees the channel manager and the provider reported; <c>null</c> when nobody did (never zero).</param>
/// <param name="Net">What is left of the stays whose cost is known; <c>null</c> when none is.</param>
/// <param name="CostRate">The cost over the gross of the stays that report it, 0 to 1; <c>null</c> when none does.</param>
public sealed record AgencyPortalCostRow(
    BookingSource? Source,
    ChannelCostKind? Kind,
    int Stays,
    decimal Gross,
    decimal? Cost,
    decimal? Net,
    decimal? CostRate);

/// <summary>The payouts to the owners confirmed in the period (paid on a day of it, not reversed) in a currency.</summary>
public sealed record AgencyPayoutRow(string Currency, int Payouts, decimal Gross, decimal Withholding, decimal Net);

/// <summary>The wire batches made in the period in a currency, by state: how many batches and transfers, and the total they pay.</summary>
public sealed record AgencyWireRow(string Currency, WireBatchStatus Status, int Batches, int Transfers, decimal Total);

/// <summary>The Italian tourist tax of the period as the tourist tax summary says it (<see cref="ITouristTaxSummaryService"/>), in euros.</summary>
/// <param name="Owed">What the stays recorded as due (the tax collected from the guests).</param>
/// <param name="Paid">What was remitted to the comuni; <c>null</c> when the caller reaches only some of the properties (the payments belong to the org).</param>
/// <param name="ToRemit">What is still to remit (owed less paid); <c>null</c> as <paramref name="Paid"/>.</param>
/// <param name="StaysWithoutAmount">The stays with no amount recorded: the owed amount may be incomplete.</param>
public sealed record AgencyItalianTouristTax(decimal Owed, decimal? Paid, decimal? ToRemit, int StaysWithoutAmount);

/// <summary>The Swiss tourist tax the documents of the period collected for a canton, in a currency (credit notes taken away). What was remitted is not recorded in CasaZen.</summary>
public sealed record AgencySwissTouristTaxRow(string Canton, string Currency, int Documents, decimal Collected);

/// <summary>The tourist tax of the period: Italy by the tourist tax summary, Switzerland by canton from the documents.</summary>
public sealed record AgencyTouristTax(AgencyItalianTouristTax Italy, IReadOnlyList<AgencySwissTouristTaxRow> Switzerland);

/// <summary>
/// The margin of the agency in a currency: the commissions and the recharges it invoiced to the owners (taxable, credit notes taken away) less the commissions of
/// the portals whose account the agency holds (the costs CasaZen can tell the agency bears, from the owner ledger of the statement months of the period).
/// </summary>
public sealed record AgencyMarginRow(string Currency, decimal Commissions, decimal Recharges, decimal PortalCommissionsBorne, decimal Margin);

/// <summary>The portal commissions of the listings whose account the agency holds (V2), by channel and currency, from the owner ledger (statement months of the period): for information, never added to the VAT due.</summary>
public sealed record AgencyPortalCommissionBorneRow(BookingSource Source, string Currency, decimal Amount);

/// <summary>The stays of the period as the Home counts them (<see cref="IHostDashboardService"/>): occupancy and revenue by accrual, tourist tax excluded.</summary>
public sealed record AgencyStaysFigures(int OccupiedNights, int AvailableNights, decimal? OccupancyRate, decimal Revenue, int Stays, int RevenueNights, decimal? Adr, decimal? RevPar);

/// <summary>The money of the period as the collections report says it: collected by cash net of refunds, and what is still to collect for each currency.</summary>
public sealed record AgencyCollectionsFigures(decimal Collected, int Payments, IReadOnlyList<HostBalanceTotal> ToCollect);

/// <summary>The performance of the properties of one owner (or of the properties nobody owns), as the report by owner says it.</summary>
public sealed record AgencyOwnerRevenueRow(Guid? OwnerId, string? OwnerName, bool Anonymized, int Properties, OwnerPerformanceFigures Figures);

/// <summary>
/// The agency summary of a period (E15/F01/US01/T01, acceptance step C7 and D3 of the pilot): the one set of figures the ZIP and the screen show, so they never
/// disagree. Every figure comes from the service or the table that already computes it (the Home, the collections, the channel costs, the tourist tax, the
/// report by owner, the owner ledger and the fiscal documents) and none is worked out a second time. The documents count on the day they were issued, the owner
/// ledger by the statement month, the others as their own report says it.
/// </summary>
/// <param name="Period">The month or the year.</param>
/// <param name="GeneratedOn">The day it was read (Europe/Rome).</param>
/// <param name="OwnerAccountingIncluded">False when the caller may not read the owner accounting: the owner figures are empty and the screen says why.</param>
/// <param name="Stays">Occupancy and revenue.</param>
/// <param name="Collections">What was collected and what is still to collect.</param>
/// <param name="GuestDocuments">The documents issued to the guests, for each issuer, kind and currency.</param>
/// <param name="PortalCosts">The costs of the channels (the rows of the report and its total last).</param>
/// <param name="TouristTax">The tourist tax collected and to remit.</param>
/// <param name="Commissions">The commission earned from each owner (owner accounting).</param>
/// <param name="OwnerInvoices">The invoices issued to the owners (owner accounting).</param>
/// <param name="Payouts">The payouts to the owners (owner accounting).</param>
/// <param name="Wires">The wire batches (owner accounting).</param>
/// <param name="PortalCommissionsBorne">The portal commissions of the accounts the agency holds, by channel (owner accounting).</param>
/// <param name="Margin">The margin for each currency (owner accounting).</param>
/// <param name="Owners">The report by owner (owner accounting).</param>
public sealed record AgencySummary(
    AccountantPackagePeriod Period,
    DateOnly GeneratedOn,
    bool OwnerAccountingIncluded,
    AgencyStaysFigures Stays,
    AgencyCollectionsFigures Collections,
    IReadOnlyList<AgencyGuestDocumentRow> GuestDocuments,
    IReadOnlyList<AgencyPortalCostRow> PortalCosts,
    AgencyTouristTax TouristTax,
    IReadOnlyList<AgencyCommissionRow> Commissions,
    AgencyOwnerInvoices OwnerInvoices,
    IReadOnlyList<AgencyPayoutRow> Payouts,
    IReadOnlyList<AgencyWireRow> Wires,
    IReadOnlyList<AgencyPortalCommissionBorneRow> PortalCommissionsBorne,
    IReadOnlyList<AgencyMarginRow> Margin,
    IReadOnlyList<AgencyOwnerRevenueRow> Owners);
