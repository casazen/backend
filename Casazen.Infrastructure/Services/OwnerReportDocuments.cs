using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The title and the CSV file of the report by property owner (NF-96), in Italian like the other host reports
/// (<see cref="HostReportDocuments"/>): for a spreadsheet set to Italian (<see cref="CsvWriter"/>). The title says the base of the
/// numbers and the file carries it on its first row, so no figure travels without its label. An owner appears with its name and nothing
/// else: never a fiscal code, an IBAN, an e-mail or a phone (the report does not even read them).
/// </summary>
public static class OwnerReportDocuments
{
    /// <summary>Title of the report by owner: by accrual, the nights of the confirmed stays, each owner for the share it holds.</summary>
    public const string Title = "Prestazioni per proprietario, per competenza";

    /// <summary>What the file shows for an owner whose personal data was anonymized (the API says the same in <c>OwnerAnonymizedLabel</c>).</summary>
    public const string AnonymizedOwnerLabel = "Proprietario anonimizzato";

    /// <summary>What the file shows for the properties nobody owns in the period.</summary>
    public const string UnassignedLabel = "Senza proprietario";

    private const string Total = "Totale";

    /// <summary>
    /// The report as a file: the title with its base and the days on the first row, the header, a row for each owner, the row of the
    /// properties nobody owns (when there is one) and the total. Nights and stays can be fractions: a house shared by two owners is half
    /// of each.
    /// </summary>
    public static byte[] Csv(OwnerPerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = new List<IReadOnlyList<object?>>
        {
            Row($"{Title}, dal {Date(report.Period.From)} al {Date(report.Period.To.AddDays(-1))}"),
            Row(
                "Proprietario",
                "Immobili",
                "Notti occupate",
                "Notti disponibili",
                "Notti chiuse dall'host",
                "Occupazione (%)",
                "Ricavo per competenza (€)",
                "Soggiorni",
                "Notti dei soggiorni",
                "ADR (€)",
                "RevPAR (€)"),
        };
        rows.AddRange(report.Rows.Select(r => OwnerRow(r.IsAnonymized ? AnonymizedOwnerLabel : r.DisplayName ?? string.Empty, r.PropertyCount, r.Figures)));
        if (report.Unassigned.PropertyCount > 0)
            rows.Add(OwnerRow(UnassignedLabel, report.Unassigned.PropertyCount, report.Unassigned.Figures));

        rows.Add(OwnerRow(Total, null, report.Total));
        return CsvWriter.Write(rows);
    }

    /// <summary>The file name of the report: <c>casazen-prestazioni-per-proprietario-competenza-20261001-20261031.csv</c>.</summary>
    public static string FileName(HostDashboardPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return $"casazen-prestazioni-per-proprietario-competenza-{Compact(period.From)}-{Compact(period.To.AddDays(-1))}.csv";
    }

    private static IReadOnlyList<object?> OwnerRow(string label, int? propertyCount, OwnerPerformanceFigures figures) =>
        Row(
            label,
            propertyCount,
            Nights(figures.OccupiedNights),
            Nights(figures.AvailableNights),
            Nights(figures.ClosedNights),
            figures.OccupancyRate is { } rate ? (decimal?)Math.Round(rate * 100m, 2, MidpointRounding.AwayFromZero) : null,
            Math.Round(figures.Revenue, 2, MidpointRounding.AwayFromZero),
            Nights(figures.StayCount),
            Nights(figures.RevenueNights),
            figures.Adr,
            figures.RevPar);

    // Nights and stays can be fractions when a house is shared; two places are enough to read them.
    private static decimal Nights(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string Date(DateTime day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Compact(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
