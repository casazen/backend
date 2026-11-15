using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The files of the tourist tax at year end (E18/F01/US02/T03, NF-117, acceptance steps C9 and D3 of the complete flow), as a CSV or an Excel file with the same tables: the annual declaration data per
/// comune (nights, guests, the stays with a tax and those with none with the reason, the exempt guests by reason, the tax due, remitted and the difference, and the payments with their days and
/// references), the account of the agent by month or quarter (what was collected and remitted, a line for the year and the payments) and the yearly count of the nights of the properties in Ticino for
/// the regional tourist organization (taxed and untaxed nights with the amounts of the documents issued). They carry every field the data has (⚖ T2: the format a comune asks varies) and are made of
/// what <see cref="TouristTaxSummaryService"/> gives: nothing is recomputed here and nothing is filed by CasaZen. The texts are those of the resource files, in the language of the request.
/// </summary>
public static class TouristTaxYearEndExports
{
    /// <summary>The name of the sheet of the count of Ticino: the canton code, in both languages (not a text to translate).</summary>
    public const string TicinoSheetName = "TI";

    /// <summary>The most rows of a table of these exports.</summary>
    public const int MaxRows = 200_000;

    // ---------------------------------------------------------------- the declaration to the comune

    /// <summary>
    /// The three tables of the declaration of the year: the comuni (stays, nights, guests, the stays with a tax, the stays with no amount and why, the tax due, remitted and the difference with the
    /// status of the comune and a total row), the exempt guests by comune and reason (counts, never a guest) and the payments (period, amount, day, reference, receipt).
    /// </summary>
    public static IReadOnlyList<ReportTable> DeclarationTables(TouristTaxDeclaration data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);

        var year = data.Year.ToString("0000", CultureInfo.InvariantCulture);
        var comuni = new ReportTable(
            texts.Get("TtDeclarationTitle", language, year),
            [
                texts.Get("TtColComune", language),
                texts.Get("TtColStays", language),
                texts.Get("TtColNights", language),
                texts.Get("TtColGuests", language),
                texts.Get("TtColTaxedStays", language),
                texts.Get("TtColStaysWithoutAmount", language),
                texts.Get("TtColReasonExempt", language),
                texts.Get("TtColReasonChannel", language),
                texts.Get("TtColReasonNotCalculated", language),
                texts.Get("TtColReasonNoTaxDue", language),
                texts.Get("TtColOwed", language),
                texts.Get("TtColRemitted", language),
                texts.Get("TtColDifference", language),
                texts.Get("TtColStatus", language),
                texts.Get("TtColNote", language),
            ]);
        foreach (var comune in data.Comuni)
        {
            var summary = comune.Summary;
            comuni.Add(
                summary.Comune,
                summary.Stays,
                summary.Nights,
                summary.Guests,
                comune.TaxedStays,
                summary.StaysWithoutAmount,
                StaysOf(comune, TouristTaxStayReasons.Exempt),
                StaysOf(comune, TouristTaxStayReasons.CollectedByChannel),
                StaysOf(comune, TouristTaxStayReasons.NotCalculated),
                StaysOf(comune, TouristTaxStayReasons.NoTaxDue),
                ReportCell.Money(summary.Owed),
                ReportCell.Money(summary.Paid),
                ReportCell.Money(summary.Balance),
                summary.Status is { } status ? StatusText(status, texts, language) : null,
                StaysOf(comune, TouristTaxStayReasons.NotCalculated) > 0 ? texts.Get("TouristTaxOwedIncomplete", language) : null);
        }

        var total = data.Total;
        comuni.Add(
            texts.Get("TtTotal", language),
            total.Stays,
            total.Nights,
            total.Guests,
            data.Comuni.Sum(c => c.TaxedStays),
            total.StaysWithoutAmount,
            data.Comuni.Sum(c => StaysOf(c, TouristTaxStayReasons.Exempt)),
            data.Comuni.Sum(c => StaysOf(c, TouristTaxStayReasons.CollectedByChannel)),
            data.Comuni.Sum(c => StaysOf(c, TouristTaxStayReasons.NotCalculated)),
            data.Comuni.Sum(c => StaysOf(c, TouristTaxStayReasons.NoTaxDue)),
            ReportCell.Money(total.Owed),
            ReportCell.Money(total.Paid),
            ReportCell.Money(total.Balance),
            ReportCell.Empty,
            total.StaysWithoutAmount > 0 ? texts.Get("TouristTaxOwedIncomplete", language) : null);

        var exemptions = new ReportTable(
            texts.Get("TtExemptionsTitle", language, year),
            [
                texts.Get("TtColComune", language),
                texts.Get("TtExemptionColReason", language),
                texts.Get("TtColStays", language),
                texts.Get("TtColGuests", language),
                texts.Get("TtExemptionColGuestNights", language),
            ]);
        foreach (var comune in data.Comuni)
        {
            foreach (var reason in comune.Exemptions)
                exemptions.Add(comune.Summary.Comune, ExemptionText(reason.Reason, texts, language), reason.Stays, reason.Guests, reason.GuestNights);
        }

        return [comuni, exemptions, RemittancesTable(data.Comuni.SelectMany(c => c.Summary.Remittances ?? []).ToList(), year, texts, language)];
    }

    /// <summary>The CSV of the declaration: the comuni, the exemptions and the payments, one under the other (<see cref="CsvWriter"/>).</summary>
    public static byte[] DeclarationCsv(TouristTaxDeclaration data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(DeclarationTables(data, texts, language), MaxRows);

    /// <summary>The Excel file of the declaration: a sheet for the comuni, one for the exemptions and one for the payments.</summary>
    public static byte[] DeclarationXlsx(TouristTaxDeclaration data, IDocumentTexts texts, string language)
    {
        var tables = DeclarationTables(data, texts, language);
        return XlsxWriter.Write(
            [
                new ReportSheet(texts.Get("TtDeclarationSheet", language), tables[0]),
                new ReportSheet(texts.Get("TtExemptionsSheet", language), tables[1]),
                new ReportSheet(texts.Get("TtRemittancesSheet", language), tables[2]),
            ],
            MaxRows);
    }

    /// <summary>The name of the file of the declaration of a year: <c>casazen-imposta-soggiorno-dichiarazione-2026.csv</c>.</summary>
    public static string DeclarationFileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"casazen-imposta-soggiorno-dichiarazione-{year:0000}.{extension}");

    // ---------------------------------------------------------------- the account of the agent

    /// <summary>
    /// The two tables of the account of the agent: for each comune the tax collected and remitted in each month or quarter and then in the year (the rows of a comune together), with the stays, nights and
    /// guests of the period and the difference; and every payment of the year with its period, amount, day, reference and receipt.
    /// </summary>
    public static IReadOnlyList<ReportTable> AgentAccountTables(TouristTaxAgentAccount data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);

        var year = data.Year.ToString("0000", CultureInfo.InvariantCulture);
        var cut = texts.Get(data.By == TouristTaxAccountPeriod.Quarter ? "TtAgentByQuarter" : "TtAgentByMonth", language);
        var account = new ReportTable(
            texts.Get("TtAgentTitle", language, year, cut),
            [
                texts.Get("TtColComune", language),
                texts.Get("AccPkgColPeriod", language),
                texts.Get("TtRemColPeriodFrom", language),
                texts.Get("TtRemColPeriodTo", language),
                texts.Get("TtColStays", language),
                texts.Get("TtColNights", language),
                texts.Get("TtColGuests", language),
                texts.Get("TtAgentColCollected", language),
                texts.Get("TtColStaysWithoutAmount", language),
                texts.Get("TtColRemitted", language),
                texts.Get("TtColDifference", language),
                texts.Get("TtColNote", language),
            ]);
        foreach (var row in data.Rows)
        {
            account.Add(
                row.Comune,
                row.IsYear ? texts.Get("TtAgentYear", language) : row.Period,
                ReportCell.Date(row.From),
                ReportCell.Date(row.To),
                row.Stays,
                row.Nights,
                row.Guests,
                ReportCell.Money(row.Collected),
                row.StaysWithoutAmount,
                ReportCell.Money(row.Remitted),
                ReportCell.Money(row.Balance),
                row.StaysWithoutAmount > 0 ? texts.Get("TouristTaxOwedIncomplete", language) : null);
        }

        return [account, RemittancesTable(data.Remittances, year, texts, language)];
    }

    /// <summary>The CSV of the account of the agent: the account, then the payments.</summary>
    public static byte[] AgentAccountCsv(TouristTaxAgentAccount data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(AgentAccountTables(data, texts, language), MaxRows);

    /// <summary>The Excel file of the account of the agent: a sheet for the account and one for the payments.</summary>
    public static byte[] AgentAccountXlsx(TouristTaxAgentAccount data, IDocumentTexts texts, string language)
    {
        var tables = AgentAccountTables(data, texts, language);
        return XlsxWriter.Write(
            [
                new ReportSheet(texts.Get("TtAgentSheetAccount", language), tables[0]),
                new ReportSheet(texts.Get("TtRemittancesSheet", language), tables[1]),
            ],
            MaxRows);
    }

    /// <summary>The name of the file of the account of the agent: <c>casazen-imposta-soggiorno-conto-agente-2026-mensile.csv</c> (<c>trimestrale</c> by quarter).</summary>
    public static string AgentAccountFileName(int year, TouristTaxAccountPeriod by, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"casazen-imposta-soggiorno-conto-agente-{year:0000}-{(by == TouristTaxAccountPeriod.Quarter ? "trimestrale" : "mensile")}.{extension}");

    // ---------------------------------------------------------------- the yearly count of Ticino

    /// <summary>
    /// The table of the yearly count of the nights of the properties in Ticino: for each property its address and municipality, the stays and the nights of the year, the taxed and untaxed ones by their
    /// documents, those with no document issued yet (told apart, never counted as untaxed), and the tourist tax of the documents in francs.
    /// </summary>
    public static ReportTable TicinoTable(TicinoYearlyCount data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);

        var table = new ReportTable(
            texts.Get("TtTicinoTitle", language, data.Year.ToString("0000", CultureInfo.InvariantCulture), DocumentLanguages.Day(data.DueDate)),
            [
                texts.Get("TtTicinoColProperty", language),
                texts.Get("TtTicinoColAddress", language),
                texts.Get("TtTicinoColTown", language),
                texts.Get("TtTicinoColMunicipality", language),
                texts.Get("TtColStays", language),
                texts.Get("TtColNights", language),
                texts.Get("TtTicinoColTaxedStays", language),
                texts.Get("TtTicinoColTaxedNights", language),
                texts.Get("TtTicinoColUntaxedStays", language),
                texts.Get("TtTicinoColUntaxedNights", language),
                texts.Get("TtTicinoColNoDocumentStays", language),
                texts.Get("TtTicinoColNoDocumentNights", language),
                texts.Get("TtTicinoColCurrency", language),
                texts.Get("TtTicinoColAmount", language),
            ]);
        foreach (var property in data.Properties)
        {
            table.Add(
                property.PropertyName,
                property.Address,
                property.City,
                property.MunicipalityCode,
                property.Stays,
                property.Nights,
                property.TaxedStays,
                property.TaxedNights,
                property.UntaxedStays,
                property.UntaxedNights,
                property.StaysWithoutDocument,
                property.NightsWithoutDocument,
                property.Currency,
                ReportCell.Money(property.TouristTax));
        }

        return table;
    }

    /// <summary>The CSV of the yearly count of Ticino.</summary>
    public static byte[] TicinoCsv(TicinoYearlyCount data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(TicinoTable(data, texts, language), MaxRows);

    /// <summary>The Excel file of the yearly count of Ticino: one sheet, <see cref="TicinoSheetName"/>.</summary>
    public static byte[] TicinoXlsx(TicinoYearlyCount data, IDocumentTexts texts, string language) =>
        XlsxWriter.Write([new ReportSheet(TicinoSheetName, TicinoTable(data, texts, language))], MaxRows);

    /// <summary>The name of the file of the yearly count of Ticino: <c>casazen-imposta-soggiorno-ticino-2026.csv</c>.</summary>
    public static string TicinoFileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"casazen-imposta-soggiorno-ticino-{year:0000}.{extension}");

    // ---------------------------------------------------------------- the words

    /// <summary>The payments of the year as a table: the comune, the period they pay, the amount, the day, the reference, the notes of the host and whether a receipt is stored.</summary>
    private static ReportTable RemittancesTable(IReadOnlyList<TouristTaxRemittanceLine> remittances, string year, IDocumentTexts texts, string language)
    {
        var table = new ReportTable(
            texts.Get("TtRemittancesTitle", language, year),
            [
                texts.Get("TtColComune", language),
                texts.Get("TtRemColPeriodFrom", language),
                texts.Get("TtRemColPeriodTo", language),
                texts.Get("TtRemColAmount", language),
                texts.Get("TtRemColPaidOn", language),
                texts.Get("TtRemColReference", language),
                texts.Get("TtRemColNotes", language),
                texts.Get("TtRemColReceipt", language),
            ]);
        foreach (var remittance in remittances)
        {
            table.Add(
                remittance.Comune,
                ReportCell.Date(remittance.PeriodFrom),
                ReportCell.Date(remittance.PeriodTo),
                ReportCell.Money(remittance.Amount),
                ReportCell.Date(remittance.PaidOn),
                remittance.Reference,
                remittance.Notes,
                remittance.HasReceipt ? texts.Get("TtYes", language) : null);
        }

        return table;
    }

    private static int StaysOf(TouristTaxDeclarationComune comune, string reason) =>
        comune.StaysWithoutAmount.FirstOrDefault(r => r.Reason == reason)?.Stays ?? 0;

    private static string StatusText(TouristTaxSummaryStatus status, IDocumentTexts texts, string language) => status switch
    {
        TouristTaxSummaryStatus.NothingOwed => texts.Get("TouristTaxStatusNothingRecorded", language),
        TouristTaxSummaryStatus.ToPay => texts.Get("TtStatusToPay", language),
        TouristTaxSummaryStatus.Paid => texts.Get("TtStatusPaid", language),
        TouristTaxSummaryStatus.Overpaid => texts.Get("TtStatusOverpaid", language),
        _ => status.ToString(),
    };

    /// <summary>The exemption reason in words: the catalogue of <see cref="TouristTaxExemptionReason"/> and the two reasons that are not a choice of the host (the age, an erased reason); an unknown code as it is.</summary>
    private static string ExemptionText(string reason, IDocumentTexts texts, string language) => reason switch
    {
        TouristTaxExemptionReason.MinorAgeCode => texts.Get("TtExemptionMinorAge", language),
        TouristTaxExemptionReason.ResidentCode => texts.Get("TtExemptionResident", language),
        TouristTaxExemptionReason.HealthCareCode => texts.Get("TtExemptionHealthCare", language),
        TouristTaxExemptionReason.DisabilityCompanionCode => texts.Get("TtExemptionDisabilityCompanion", language),
        TouristTaxExemptionReason.PublicServiceCode => texts.Get("TtExemptionPublicService", language),
        TouristTaxExemptionReason.HostFamilyCode => texts.Get("TtExemptionHostFamily", language),
        TouristTaxExemptionReason.OtherCode => texts.Get("TtExemptionOther", language),
        TouristTaxExemptionKinds.Erased => texts.Get("TtExemptionErased", language),
        TouristTaxExemptionKinds.Restricted => texts.Get("TtExemptionRestricted", language),
        _ => reason,
    };
}
