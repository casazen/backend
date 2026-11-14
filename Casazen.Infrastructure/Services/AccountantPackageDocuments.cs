using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>A published statement of an owner as the list of the package shows it: what the version stores, never recomputed.</summary>
internal sealed record PackageStatement(
    Guid VersionId,
    Guid OwnerId,
    string? OwnerName,
    int Year,
    int Month,
    int VersionNumber,
    string Currency,
    decimal Revenue,
    decimal ChannelFees,
    decimal Commission,
    decimal Charges,
    decimal Payments,
    decimal ClosingBalance,
    DateTime PublishedAt,
    string? PublishedSha256);

/// <summary>The withholding the payouts of an owner in a month decided: the evidence added up (a reversal is a row with the opposite amounts).</summary>
internal sealed record PackageWithholding(string? OwnerName, int Year, int Month, string? TaxCode, int Payouts, decimal GrossBase, decimal Withheld);

/// <summary>The data of the Certificazione Unica of one owner, from the annual dataset.</summary>
internal sealed record PackageCuRow(string? OwnerName, decimal GrossBase, decimal Withheld, int Evidence);

/// <summary>
/// What the VAT tables of an issuer cover when the caller cannot read all of it: the invoices of the agency to the owners (they belong to no property: only a caller that reaches
/// the whole org reads them, as figures for each rate with no name) and the portal commissions of the owner ledger (owner accounting). A part that is not covered is said in the
/// file, never left out in silence.
/// </summary>
/// <param name="OwnerInvoices">The invoices to the owners are in the tables.</param>
/// <param name="PortalCommissions">The portal commissions of the accounts the agency holds are in the tables.</param>
internal sealed record VatCoverage(bool OwnerInvoices, bool PortalCommissions)
{
    /// <summary>Everything is in.</summary>
    public static VatCoverage Whole { get; } = new(true, true);
}

/// <summary>
/// The tables of the files of the accountant package (E15/F01/US01/T01, NF-101): the registers of the documents, the VAT of each issuer, the statements, the
/// withholding, the Swiss tourist tax and the index. Each is a <see cref="ReportTable"/>, written by <see cref="CsvWriter"/> and by <see cref="XlsxWriter"/>, so the CSV and the Excel
/// file of a register carry the same rows. The words come from the resource files (<see cref="PackageTexts"/>); an amount is a number to the cent in the currency of its
/// own column and a credit note is negative, so a column adds up to what the period is. Nothing here works a figure out: it writes what the documents and the services store.
/// </summary>
internal static class AccountantPackageDocuments
{
    /// <summary>
    /// The two words of the header of the index that read the same in Italian and in English («File», «SHA-256»): not texts to translate, so they are not in the resource files
    /// (the localization test wants a different English text for every key).
    /// </summary>
    public const string IndexFileHeader = "File";

    /// <inheritdoc cref="IndexFileHeader"/>
    public const string IndexHashHeader = "SHA-256";

    /// <summary>The words that tell an issuer: its name, its country and its tax number.</summary>
    public static string IssuerLabel(AgencyIssuer issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        return string.IsNullOrWhiteSpace(issuer.TaxId) ? $"{issuer.Name} ({issuer.CountryCode})" : $"{issuer.Name} ({issuer.CountryCode} {issuer.TaxId})";
    }

    /// <summary>A day as the files write it in their titles: <c>dd/MM/yyyy</c>.</summary>
    public static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>The word of a kind of document (invoice, receipt, credit note): the one of the fiscal documents and their list.</summary>
    public static string KindLabel(FiscalDocumentKind kind, PackageTexts t) => FiscalDocumentPdfs.KindLabel(kind, t.Source, t.Language);

    /// <summary>The name of an owner, or the word for an owner whose data was anonymized.</summary>
    public static string OwnerLabel(string? name, PackageTexts t) => string.IsNullOrWhiteSpace(name) ? t.Get("OwnerAnonymizedLabel") : name;

    // ---------------------------------------------------------------- the registers of the documents

    /// <summary>
    /// The register of the documents of one issuer: one row for each document (its kind, number, day, recipient, amounts, currency and, for the guests, who collected the
    /// money; for an owner invoice the month of its statement), then the VAT of each document for each rate, then the totals for each rate. The amounts of a credit note are negative.
    /// </summary>
    public static IReadOnlyList<ReportTable> Register(
        IReadOnlyList<PackageDocument> documents,
        bool ownerInvoices,
        AgencyIssuer issuer,
        AccountantPackagePeriod period,
        PackageTexts t)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(t);

        var title = ownerInvoices
            ? t.Get(PackageLabels.OwnerRegisterTitle, Day(period.From), Day(period.To))
            : t.Get(PackageLabels.GuestRegisterTitle, IssuerLabel(issuer), Day(period.From), Day(period.To));
        var header = new List<string> { t["FiscalExportColKind"], t["FiscalExportColNumber"], t["FiscalExportColIssueDate"], t["FiscalExportColRecipient"] };
        header.Add(ownerInvoices ? t[PackageLabels.ColPeriod] : t[PackageLabels.ColPaymentChannel]);
        header.AddRange([t["FiscalExportColTaxableBase"], t["FiscalExportColVat"], t["FiscalExportColStampDuty"], t["FiscalExportColTotal"], t["FiscalExportColCurrency"]]);
        var register = new ReportTable(title, header);
        foreach (var d in documents)
        {
            register.Add(
                KindLabel(d.Kind, t),
                d.Number,
                ReportCell.Date(d.IssueDate),
                d.RecipientName,
                ownerInvoices ? PeriodCell(d) : ChannelLabel(d.PaymentChannel, t),
                ReportCell.Money(d.Sign * d.TaxableBase),
                ReportCell.Money(d.Sign * d.VatAmount),
                ReportCell.Money(d.Sign * d.StampDuty),
                ReportCell.Money(d.Sign * d.Total),
                d.Currency);
        }

        var detail = new ReportTable(
            t[PackageLabels.RateDetailTitle],
            [t["FiscalExportColNumber"], t["FiscalExportColIssueDate"], t[PackageLabels.ColRate], t[PackageLabels.ColNature], t["FiscalExportColTaxableBase"], t["FiscalExportColVat"], t["FiscalExportColCurrency"]]);
        foreach (var (document, row) in RateRows(documents))
        {
            detail.Add(
                document.Number,
                ReportCell.Date(document.IssueDate),
                RateCell(row),
                NatureOf(row, t),
                ReportCell.Money(document.Sign * row.Taxable),
                ReportCell.Money(document.Sign * row.Vat),
                document.Currency);
        }

        return [register, detail, RateTotals(documents, t)];
    }

    /// <summary>The totals of a set of documents for each currency, rate and nature code (credit notes taken away): the base the VAT is declared on.</summary>
    public static ReportTable RateTotals(IReadOnlyList<PackageDocument> documents, PackageTexts t)
    {
        var table = new ReportTable(
            t[PackageLabels.RateTotalsTitle],
            [t["FiscalExportColCurrency"], t[PackageLabels.ColRate], t[PackageLabels.ColNature], t["FiscalExportColTaxableBase"], t["FiscalExportColVat"], t["FiscalExportColTotal"], t[PackageLabels.ColDocuments]]);
        foreach (var group in RateRows(documents)
                     .GroupBy(x => (x.Document.Currency, x.Row.RatePercent, Nature: x.Row.RatePercent == 0m ? x.Row.NatureCode : null))
                     .OrderBy(g => g.Key.Currency, StringComparer.Ordinal)
                     .ThenByDescending(g => g.Key.RatePercent ?? -1m)
                     .ThenBy(g => g.Key.Nature, StringComparer.Ordinal))
        {
            var taxable = group.Sum(x => x.Document.Sign * x.Row.Taxable);
            var vat = group.Sum(x => x.Document.Sign * x.Row.Vat);
            table.Add(
                group.Key.Currency,
                RateCell(group.Key.RatePercent),
                group.Key.RatePercent is null ? t[PackageLabels.RateUnknown] : group.Key.Nature,
                ReportCell.Money(taxable),
                ReportCell.Money(vat),
                ReportCell.Money(taxable + vat),
                group.Select(x => x.Document.Id).Distinct().Count());
        }

        return table;
    }

    // ---------------------------------------------------------------- the VAT of an issuer

    /// <summary>
    /// The VAT tables of one issuer (NF-101, D11). A Swiss issuer: the base of the quarterly return (the taxable turnover and the VAT due for each rate, by the quarter of the
    /// day of the issue), what is outside the VAT (the tourist tax, the expenses advanced) and, for information only, the commissions of the portals whose account the agency holds
    /// (⚖ not verified: a possible base of the purchase tax, never added to the VAT due). An Italian issuer: the helper of the sales register, by month, for the ordinary invoices,
    /// the invoices in the flat-rate regime and the receipts. The documents are the ones of the issuer: the guests' and the invoices to the owners (as figures for each rate, with
    /// no name, for a caller that reaches the whole org: the VAT of the issuer is made of everything it issued). What <paramref name="coverage"/> says the caller cannot read is
    /// said in the file, never left out in silence.
    /// </summary>
    public static IReadOnlyList<ReportTable> Vat(
        AgencyIssuer issuer,
        IReadOnlyList<PackageDocument> documents,
        IReadOnlyList<AgencyPortalCommissionBorneRow> portalCommissions,
        PackageTexts t,
        VatCoverage? coverage = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(portalCommissions);
        ArgumentNullException.ThrowIfNull(t);

        var covered = coverage ?? VatCoverage.Whole;
        return issuer.IsSwiss ? SwissVat(issuer, documents, portalCommissions, covered, t) : ItalianVat(issuer, documents, covered, t);
    }

    /// <summary>One amount of a document as the Swiss return counts it: its taxable amount, its stored VAT, its rate (<c>null</c> when the document does not state it) and what it is (<c>null</c> for a document with no lines).</summary>
    private sealed record SwissVatPart(PackageDocument Document, FiscalLineType? LineType, decimal Taxable, decimal Vat, decimal? RatePercent);

    /// <summary>The parts of a document: one for each line, with the stored VAT of the line; a document with no lines (one imported from a channel manager) states its own totals and enters as it states them, never dropped.</summary>
    private static IEnumerable<SwissVatPart> SwissVatPartsOf(PackageDocument document)
    {
        if (document.Lines.Count > 0)
            return document.Lines.Select(l => new SwissVatPart(document, l.LineType, l.TotalPrice, AccountantPackageFacts.VatOfLine(l), l.VatRatePercent));

        return AccountantPackageFacts.VatOf(document).Select(r => new SwissVatPart(document, null, r.Taxable, r.Vat, r.RatePercent));
    }

    private static ReportCell RateOrUnknown(decimal? ratePercent, PackageTexts t) => ratePercent is { } rate ? ReportCell.Percent(rate / 100m) : t[PackageLabels.RateUnknown];

    /// <summary>The table that says what a file does not hold for the caller: its title and the reason.</summary>
    private static ReportTable OwnerInvoicesNote(PackageTexts t)
    {
        var note = new ReportTable(t[PackageLabels.VatOwnerInvoicesOmittedTitle], [t[PackageLabels.IndexColReason]]);
        note.Add(t[PackageLabels.VatOwnerInvoicesOmittedReason]);
        return note;
    }

    private static IReadOnlyList<ReportTable> SwissVat(
        AgencyIssuer issuer, IReadOnlyList<PackageDocument> documents, IReadOnlyList<AgencyPortalCommissionBorneRow> portalCommissions, VatCoverage coverage, PackageTexts t)
    {
        var parts = documents.SelectMany(SwissVatPartsOf).ToList();

        var returnBase = new ReportTable(
            t.Get(PackageLabels.VatSwissTitle, IssuerLabel(issuer)),
            [t[PackageLabels.ColPeriod], t[PackageLabels.ColRate], t[PackageLabels.ColTurnover], t[PackageLabels.ColVatDue], t["FiscalExportColCurrency"], t[PackageLabels.ColDocuments]]);
        foreach (var group in parts
                     .Where(x => x.RatePercent is null or > 0m)
                     .GroupBy(x => (Quarter: AccountantPackageFacts.QuarterOf(x.Document.IssueDate), x.RatePercent, x.Document.Currency))
                     .OrderBy(g => g.Key.Quarter, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
                     .ThenByDescending(g => g.Key.RatePercent ?? -1m))
        {
            returnBase.Add(
                group.Key.Quarter,
                RateOrUnknown(group.Key.RatePercent, t),
                ReportCell.Money(group.Sum(x => x.Document.Sign * x.Taxable)),
                ReportCell.Money(group.Sum(x => x.Document.Sign * x.Vat)),
                group.Key.Currency,
                group.Select(x => x.Document.Id).Distinct().Count());
        }

        var outside = new ReportTable(
            t[PackageLabels.VatOutsideTitle],
            [t[PackageLabels.ColPeriod], t["FiscalExportColKind"], t[PackageLabels.ColAmount], t["FiscalExportColCurrency"]]);
        foreach (var group in parts
                     .Where(x => x.RatePercent == 0m)
                     .GroupBy(x => (Quarter: AccountantPackageFacts.QuarterOf(x.Document.IssueDate), Category: OutsideCategoryOf(x.LineType), x.Document.Currency))
                     .OrderBy(g => g.Key.Quarter, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Category)
                     .ThenBy(g => g.Key.Currency, StringComparer.Ordinal))
        {
            outside.Add(
                group.Key.Quarter,
                OutsideLabel(group.Key.Category, t),
                ReportCell.Money(group.Sum(x => x.Document.Sign * x.Taxable)),
                group.Key.Currency);
        }

        var portals = new ReportTable(
            t[PackageLabels.VatPortalTitle],
            [t[PackageLabels.ColChannel], t[PackageLabels.ColAmount], t["FiscalExportColCurrency"]]);
        foreach (var row in portalCommissions)
            portals.Add(FiscalReportDocuments.ChannelLabel(row.Source.ToString()), ReportCell.Money(row.Amount), row.Currency);

        // The portal commissions come from the owner ledger: a caller who may not read the owner accounting is told, the table is not left empty as if there were none.
        if (!coverage.PortalCommissions)
            portals.Add(t[PackageLabels.OmittedOwnerAccounting]);

        if (coverage.OwnerInvoices)
            return [returnBase, outside, portals];

        return [returnBase, outside, portals, OwnerInvoicesNote(t)];
    }

    private static IReadOnlyList<ReportTable> ItalianVat(AgencyIssuer issuer, IReadOnlyList<PackageDocument> documents, VatCoverage coverage, PackageTexts t)
    {
        var table = new ReportTable(
            t.Get(PackageLabels.VatItalyTitle, IssuerLabel(issuer)),
            [
                t[PackageLabels.ColPeriod],
                t[PackageLabels.ColClass],
                t[PackageLabels.ColRate],
                t[PackageLabels.ColNature],
                t["FiscalExportColCurrency"],
                t["FiscalExportColTaxableBase"],
                t["FiscalExportColVat"],
                t["FiscalExportColTotal"],
                t[PackageLabels.ColDocuments],
            ]);
        foreach (var group in RateRows(documents)
                     .GroupBy(x => (Month: AccountantPackageFacts.MonthOf(x.Document.IssueDate), Class: ClassOf(x.Document, issuer), x.Document.Currency, x.Row.RatePercent, Nature: x.Row.RatePercent == 0m ? x.Row.NatureCode : null))
                     .OrderBy(g => g.Key.Month, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Class)
                     .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
                     .ThenByDescending(g => g.Key.RatePercent ?? -1m)
                     .ThenBy(g => g.Key.Nature, StringComparer.Ordinal))
        {
            var taxable = group.Sum(x => x.Document.Sign * x.Row.Taxable);
            var vat = group.Sum(x => x.Document.Sign * x.Row.Vat);
            table.Add(
                group.Key.Month,
                ClassLabel(group.Key.Class, t),
                RateCell(group.Key.RatePercent),
                group.Key.RatePercent is null ? t[PackageLabels.RateUnknown] : group.Key.Nature,
                group.Key.Currency,
                ReportCell.Money(taxable),
                ReportCell.Money(vat),
                ReportCell.Money(taxable + vat),
                group.Select(x => x.Document.Id).Distinct().Count());
        }

        if (coverage.OwnerInvoices)
            return [table];

        return [table, OwnerInvoicesNote(t)];
    }

    /// <summary>
    /// The class of a document in the sales register: a receipt, an invoice in the flat-rate regime, an ordinary invoice (a credit note follows the document it credits). The regime is the
    /// one the document was issued under (its own issuer snapshot), because the issuer of a file is told by its country, tax number and name and a regime can change inside a period;
    /// a document with no snapshot (issued before it existed) takes the regime of <paramref name="issuer"/>.
    /// </summary>
    internal static SalesClass ClassOf(PackageDocument document, AgencyIssuer issuer)
    {
        var kind = document.Kind == FiscalDocumentKind.CreditNote && document.CreditedKind is { } credited ? credited : document.Kind;
        if (kind == FiscalDocumentKind.Receipt)
            return SalesClass.Receipt;

        var regime = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot)?.Regime ?? issuer.Regime;
        return regime == DocumentRegime.VatFlatRate ? SalesClass.FlatRate : SalesClass.Ordinary;
    }

    /// <summary>The classes of the sales register of an Italian issuer.</summary>
    internal enum SalesClass
    {
        Ordinary = 1,
        FlatRate = 2,
        Receipt = 3,
    }

    private static string ClassLabel(SalesClass salesClass, PackageTexts t) => salesClass switch
    {
        SalesClass.FlatRate => t[PackageLabels.ClassFlatRate],
        SalesClass.Receipt => t[PackageLabels.ClassReceipt],
        _ => t[PackageLabels.ClassOrdinary],
    };

    private enum OutsideCategory
    {
        TouristTax = 1,
        AdvancedExpenses = 2,
        Other = 3,
    }

    private static OutsideCategory OutsideCategoryOf(FiscalLineType? type) => type switch
    {
        FiscalLineType.TouristTax => OutsideCategory.TouristTax,
        FiscalLineType.AdvancedExpense => OutsideCategory.AdvancedExpenses,
        _ => OutsideCategory.Other,
    };

    private static string OutsideLabel(OutsideCategory category, PackageTexts t) => category switch
    {
        OutsideCategory.TouristTax => t[PackageLabels.OutsideTouristTax],
        OutsideCategory.AdvancedExpenses => t[PackageLabels.OutsideExpenses],
        _ => t[PackageLabels.OutsideOther],
    };

    // ---------------------------------------------------------------- statements, withholding, tourist tax

    /// <summary>The list of the published statements: one row for each owner and month (its latest published version), with what the version stores and the hash of its published file.</summary>
    public static ReportTable Statements(IReadOnlyList<PackageStatement> statements, PackageTexts t)
    {
        var table = new ReportTable(
            t[PackageLabels.StatementsTitle],
            [
                t[PackageLabels.ColOwner],
                t[PackageLabels.ColPeriod],
                t[PackageLabels.ColVersion],
                t[PackageLabels.ColPublishedOn],
                t["FiscalExportColCurrency"],
                t[PackageLabels.ColRevenue],
                t[PackageLabels.ColChannelFees],
                t[PackageLabels.ColCommission],
                t[PackageLabels.ColCharges],
                t[PackageLabels.ColPayments],
                t[PackageLabels.ColClosingBalance],
                t[PackageLabels.ColSha],
            ]);
        foreach (var s in statements)
        {
            table.Add(
                OwnerLabel(s.OwnerName, t),
                string.Create(CultureInfo.InvariantCulture, $"{s.Year:0000}-{s.Month:00}"),
                s.VersionNumber,
                ReportCell.Date(RomeCalendar.DateInRome(s.PublishedAt)),
                s.Currency,
                ReportCell.Money(s.Revenue),
                ReportCell.Money(s.ChannelFees),
                ReportCell.Money(s.Commission),
                ReportCell.Money(s.Charges),
                ReportCell.Money(s.Payments),
                ReportCell.Money(s.ClosingBalance),
                s.PublishedSha256);
        }

        return table;
    }

    /// <summary>The withholding of the Italian owners for each month of the period: the base and the amount the payouts decided, with the tax code of the rule.</summary>
    public static ReportTable Withholding(IReadOnlyList<PackageWithholding> rows, PackageTexts t)
    {
        var table = new ReportTable(
            t[PackageLabels.WithholdingTitle],
            [t[PackageLabels.ColOwner], t[PackageLabels.ColPeriod], t[PackageLabels.ColTaxCode], t[PackageLabels.ColPayoutCount], t[PackageLabels.ColGrossBase], t[PackageLabels.ColWithheld]]);
        foreach (var r in rows)
        {
            table.Add(
                OwnerLabel(r.OwnerName, t),
                string.Create(CultureInfo.InvariantCulture, $"{r.Year:0000}-{r.Month:00}"),
                r.TaxCode,
                r.Payouts,
                ReportCell.Money(r.GrossBase),
                ReportCell.Money(r.Withheld));
        }

        return table;
    }

    /// <summary>The stored F24 data of the months of the period: the tax code, the amount, the due date, the payment and the filing state as the record holds them (never the form that was filed).</summary>
    public static ReportTable F24(IReadOnlyList<F24Record> records, PackageTexts t)
    {
        var table = new ReportTable(
            t[PackageLabels.F24Title],
            [t[PackageLabels.ColPeriod], t[PackageLabels.ColTaxCode], t[PackageLabels.ColAmount], t[PackageLabels.ColDueDate], t[PackageLabels.ColPaidOn], t[PackageLabels.ColFilingState], t[PackageLabels.ColRuleVersion]]);
        foreach (var r in records)
        {
            table.Add(
                string.Create(CultureInfo.InvariantCulture, $"{r.ReferenceYear:0000}-{r.ReferenceMonth:00}"),
                r.TaxCode,
                ReportCell.Money(r.TotalAmount),
                ReportCell.Date(RomeCalendar.DateInRome(r.DueDate)),
                ReportCell.Date(r.PaidAt is { } paid ? RomeCalendar.DateInRome(paid) : null),
                t.Get("TaxFilingState" + r.FilingState),
                r.RuleVersion);
        }

        return table;
    }

    /// <summary>The data of the Certificazione Unica of a year for each owner (not the official certificate); with no dataset (a month, a year not generated) the table is only its header.</summary>
    public static ReportTable CertificazioneUnica(int year, IReadOnlyList<PackageCuRow> rows, PackageTexts t)
    {
        var table = new ReportTable(
            t.Get(PackageLabels.CuTitle, year.ToString(CultureInfo.InvariantCulture)),
            [t[PackageLabels.ColOwner], t[PackageLabels.ColGrossBase], t[PackageLabels.ColWithheld], t[PackageLabels.ColEvidence]]);
        foreach (var r in rows)
            table.Add(OwnerLabel(r.OwnerName, t), ReportCell.Money(r.GrossBase), ReportCell.Money(r.Withheld), r.Evidence);

        return table;
    }

    /// <summary>The Swiss tourist tax the documents collected for each canton and currency (credit notes taken away).</summary>
    public static ReportTable SwissTouristTax(IReadOnlyList<AgencySwissTouristTaxRow> rows, PackageTexts t)
    {
        var table = new ReportTable(
            t[PackageLabels.SwissTouristTaxTitle],
            [t[PackageLabels.ColCanton], t["FiscalExportColCurrency"], t[PackageLabels.ColDocuments], t[PackageLabels.ColCollected]]);
        foreach (var r in rows)
            table.Add(string.IsNullOrEmpty(r.Canton) ? t[PackageLabels.CantonUnknown] : r.Canton, r.Currency, r.Documents, ReportCell.Money(r.Collected));

        return table;
    }

    // ---------------------------------------------------------------- the index

    /// <summary>
    /// The index of the package: every file with its description, its rows and its SHA-256, then the folders left out. A file that is not a table (a PDF, an XML) has no rows. The
    /// index does not list itself: its hash would be the hash of the file that holds it.
    /// </summary>
    public static IReadOnlyList<ReportTable> Index(
        AccountantPackagePeriod period, IReadOnlyList<PackageIndexEntry> entries, IReadOnlyList<string> omittedFolders, PackageTexts t)
    {
        var files = new ReportTable(
            t.Get(PackageLabels.IndexTitle, period.Key),
            [IndexFileHeader, t[PackageLabels.IndexColDescription], t[PackageLabels.IndexColRows], IndexHashHeader]);
        foreach (var e in entries)
            files.Add(e.Path, e.Description, e.Rows is { } rows ? ReportCell.Integer(rows) : ReportCell.Empty, e.Sha256);

        var omitted = new ReportTable(t[PackageLabels.IndexOmittedTitle], [t[PackageLabels.IndexColFolder], t[PackageLabels.IndexColReason]]);
        foreach (var folder in omittedFolders)
            omitted.Add(folder + "/", t[PackageLabels.OmittedOwnerAccounting]);

        return [files, omitted];
    }

    // ---------------------------------------------------------------- shared

    private static IEnumerable<(PackageDocument Document, PackageVatRow Row)> RateRows(IEnumerable<PackageDocument> documents) =>
        documents.SelectMany(d => AccountantPackageFacts.VatOf(d).Select(r => (Document: d, Row: r)));

    /// <summary>The rate of a VAT row as a percentage cell; empty for a rate that is not stated.</summary>
    public static ReportCell RateCell(PackageVatRow row) => RateCell(row.RatePercent);

    /// <summary>A rate in percent as a percentage cell (8.1 is 8,10%); empty for a rate that is not stated.</summary>
    public static ReportCell RateCell(decimal? ratePercent) => ratePercent is { } rate ? ReportCell.Percent(rate / 100m) : ReportCell.Empty;

    private static ReportCell NatureOf(PackageVatRow row, PackageTexts t) => row.RatePercent is null ? t[PackageLabels.RateUnknown] : row.NatureCode;

    private static ReportCell PeriodCell(PackageDocument document) =>
        document.PeriodYear is { } year && document.PeriodMonth is { } month ? string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}") : ReportCell.Empty;

    private static string ChannelLabel(FiscalPaymentChannel channel, PackageTexts t) => channel switch
    {
        FiscalPaymentChannel.CasaZen => t[PackageLabels.ChannelCasaZen],
        FiscalPaymentChannel.Portal => t[PackageLabels.ChannelPortal],
        FiscalPaymentChannel.Outside => t[PackageLabels.ChannelOutside],
        _ => t[PackageLabels.ChannelUnknown],
    };
}

/// <summary>One line of the index of the package: the file, what it is, its rows (a table; <c>null</c> for a PDF or an XML) and its SHA-256.</summary>
internal sealed record PackageIndexEntry(string Path, string Description, int? Rows, string Sha256);
