using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The CSV of the fiscal report of the long-term area (LR-11b), in Italian like the other reports (<see cref="HostReportDocuments"/>) and
/// written by the one writer of the host files (<see cref="CsvWriter"/>). It is an estimate and the file says so on its first row, in the
/// words of the page; the second row gives the day it was made, the third the base of each number. Per lease it carries the property, the
/// tenant by name only (D29), the dates, the months, the accrual, the rent collected according to the register and the cedolare secca; a
/// lease in the ordinary regime has no tax figure and the file says why. No other data of a person: no fiscal code, e-mail or phone.
/// </summary>
public static class LongRentFiscalDocuments
{
    /// <summary>First row of the file: what it is, and that it has to be checked by the accountant.</summary>
    public const string Warning = "Stima da validare con il commercialista";

    private const string Total = "Totale";

    // The base of each number, one row under the title: no figure travels without its label.
    private const string Bases =
        "Competenza: i mesi dell'anno in cui il contratto è in vigore (mesi interi) per il canone mensile, fino alla fine del periodo in corso. "
        + "Incassato: i canoni pagati nell'anno e registrati in CasaZen, a partire dal mese indicato in «Registro dal». "
        + "Cedolare secca: l'aliquota della scheda fiscale del contratto per la competenza. "
        + "Regime ordinario: imposta non stimata, dipende dal reddito complessivo.";

    private const string NotEstimatedOrdinary = "Non stimata: dipende dal reddito complessivo";
    private const string NotEstimatedUnknown = "Non stimata: regime fiscale non registrato";
    private const string CedolareEstimated = "Cedolare secca stimata";

    /// <summary>The file: the warning, the day, the bases, the header, one row per lease and the total.</summary>
    public static byte[] Csv(LongRentFiscalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = new List<IReadOnlyList<object?>>
        {
            Row($"{Warning}: report fiscale degli affitti lunghi {report.Year}"),
            Row($"Generato il {Date(report.GeneratedOn)}"),
            Row(Bases),
            Row(
                "Immobile",
                "Conduttore",
                "Tipo di contratto",
                "Regime fiscale",
                "Dal",
                "Al (fine del periodo in corso)",
                "Mesi nell'anno",
                "Canone mensile (€)",
                "Competenza (€)",
                "Incassato registrato in CasaZen (€)",
                "Registro dal",
                "Aliquota cedolare (%)",
                "Cedolare secca stimata (€)",
                "Stima dell'imposta",
                "Note"),
        };
        rows.AddRange(report.Rows.Select(Line));
        rows.Add(Row(
            Total, null, null, null, null, null, null, null,
            report.Totals.AccrualEur,
            report.Totals.CollectedEur,
            null,
            null,
            report.Totals.CedolareEur,
            null,
            null));
        return CsvWriter.Write(rows);
    }

    /// <summary>The name of the file: the estimate and the year, nothing about a person (<c>casazen-stima-fiscale-affitti-lunghi-2026.csv</c>).</summary>
    public static string FileName(int year) =>
        $"casazen-stima-fiscale-affitti-lunghi-{year.ToString(CultureInfo.InvariantCulture)}.csv";

    private static IReadOnlyList<object?> Line(LongRentFiscalRow row) =>
        Row(
            row.PropertyName,
            Tenant(row),
            ContractType(row.ContractType),
            Regime(row.TaxRegime),
            Date(row.StartDate),
            Date(row.CurrentTermEndDate),
            row.MonthsInYear,
            row.MonthlyRentEur,
            row.AccrualEur,
            row.CollectedEur,
            row.TrackedFrom is { } from ? from.ToString("MM/yyyy", CultureInfo.InvariantCulture) : null,
            row.Tax.Rate is { } rate ? Math.Round(rate * 100m, 2, MidpointRounding.AwayFromZero) : null,
            row.Tax.AmountEur,
            row.Tax.Status switch
            {
                LongRentFiscalTaxStatus.CedolareEstimated => CedolareEstimated,
                LongRentFiscalTaxStatus.OrdinaryNotEstimated => NotEstimatedOrdinary,
                _ => NotEstimatedUnknown,
            },
            string.Join(" ", row.Notes.Select(Note)));

    private static string Tenant(LongRentFiscalRow row) =>
        row.TenantAnonymized ? "Dati anonimizzati" : $"{row.TenantFirstName} {row.TenantLastName}".Trim();

    private static string ContractType(LeaseContractType type) => type switch
    {
        LeaseContractType.Libero => "Canone libero",
        LeaseContractType.Concordato => "Canone concordato",
        _ => "Transitorio",
    };

    private static string Regime(LeaseTaxRegime? regime) => regime switch
    {
        LeaseTaxRegime.CedolareSecca => "Cedolare secca",
        LeaseTaxRegime.Ordinario => "Regime ordinario",
        _ => "Non registrato",
    };

    // The notes the rows can carry (LongRentFiscalRules): what makes the cedolare figure uncertain, in words with no figure in them.
    private static string Note(string code) => code switch
    {
        LeaseTaxAdvisoryNoteCodes.AtaUnverified =>
            "Comune candidato ad alta tensione abitativa, non verificato: se confermato l'aliquota sarebbe ridotta.",
        LeaseTaxAdvisoryNoteCodes.AtaNotListed =>
            "Comune non presente nell'elenco di CasaZen: se fosse ad alta tensione abitativa l'aliquota sarebbe ridotta.",
        LeaseTaxAdvisoryNoteCodes.EmergencyComuniNotChecked =>
            "L'aliquota ridotta può valere anche nei comuni con stato di emergenza: non verificato.",
        LeaseTaxAdvisoryNoteCodes.ConcordatoAttestationRequired =>
            "Le agevolazioni del canone concordato richiedono l'attestazione di conformità per i contratti non assistiti.",
        LeaseTaxAdvisoryNoteCodes.TransitorioReliefsToConfirm =>
            "Contratto transitorio: l'aliquota ridotta è da confermare con il commercialista, la stima usa quella ordinaria.",
        LeaseTaxAdvisoryNoteCodes.TaxRegimeUnknown => "Regime fiscale non registrato per questo contratto.",
        _ => code,
    };

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
