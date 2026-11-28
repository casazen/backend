using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

public static class TouristTaxYearEndExports
{
    public const string TicinoSheetName = "TI";

    public const int MaxRows = 200_000;

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

    public static byte[] DeclarationCsv(TouristTaxDeclaration data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(DeclarationTables(data, texts, language), MaxRows);

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

    public static string DeclarationFileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"casazen-imposta-soggiorno-dichiarazione-{year:0000}.{extension}");

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

    public static byte[] AgentAccountCsv(TouristTaxAgentAccount data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(AgentAccountTables(data, texts, language), MaxRows);

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

    public static string AgentAccountFileName(int year, TouristTaxAccountPeriod by, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"casazen-imposta-soggiorno-conto-agente-{year:0000}-{(by == TouristTaxAccountPeriod.Quarter ? "trimestrale" : "mensile")}.{extension}");

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
                texts.Get("TtColNote", language),
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
                ReportCell.Money(property.TouristTax),
                NoteOf(property, texts, language));
        }

        return table;
    }

    private static string? NoteOf(TicinoPropertyCount property, IDocumentTexts texts, string language)
    {
        if (property.CantonUnknown)
            return texts.Get("TtTicinoNoteCantonUnknown", language);

        return property.MunicipalityDerived ? texts.Get("TtTicinoNoteMunicipalityDerived", language) : null;
    }

    public static byte[] TicinoCsv(TicinoYearlyCount data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(TicinoTable(data, texts, language), MaxRows);

    public static byte[] TicinoXlsx(TicinoYearlyCount data, IDocumentTexts texts, string language) =>
        XlsxWriter.Write([new ReportSheet(TicinoSheetName, TicinoTable(data, texts, language))], MaxRows);

    public static string TicinoFileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"casazen-imposta-soggiorno-ticino-{year:0000}.{extension}");

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
