using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The title, the CSV file and the Excel file of the report by property owner (NF-96, NF-101), in Italian like the other host reports
/// (<see cref="HostReportDocuments"/>): the CSV for a spreadsheet set to Italian (<see cref="CsvWriter"/>). The title says the base of the
/// numbers and the file carries it on its first row, so no figure travels without its label. An owner appears with its name and nothing
/// else: never a fiscal code, an IBAN, an e-mail or a phone (the report does not even read them).
/// </summary>
public static class OwnerReportDocuments
{
    /// <summary>Title of the report by owner: by accrual, the nights of the confirmed stays, each owner for the share it holds.</summary>
    public const string Title = "Prestazioni per proprietario, per competenza";

    /// <summary>Name of the sheet of the Excel file.</summary>
    public const string SheetName = "Per proprietario";

    /// <summary>What the file shows for an owner whose personal data was anonymized (the API says the same in <c>OwnerAnonymizedLabel</c>).</summary>
    public const string AnonymizedOwnerLabel = "Proprietario anonimizzato";

    /// <summary>What the file shows for the properties nobody owns in the period.</summary>
    public const string UnassignedLabel = "Senza proprietario";

    private const string Total = "Totale";

    /// <summary>
    /// The report as a table: the title with its base and the days on the first row, the header, a row for each owner, the row of the
    /// properties nobody owns (when there is one) and the total. Nights and stays can be fractions: a house shared by two owners is half
    /// of each.
    /// </summary>
    public static ReportTable Table(OwnerPerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var table = new ReportTable(
            $"{Title}, dal {Date(report.Period.From)} al {Date(report.Period.To.AddDays(-1))}",
            [
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
                "RevPAR (€)",
            ]);
        foreach (var r in report.Rows)
            table.Add(OwnerRow(r.IsAnonymized ? AnonymizedOwnerLabel : r.DisplayName ?? string.Empty, r.PropertyCount, r.Figures));

        if (report.Unassigned.PropertyCount > 0)
            table.Add(OwnerRow(UnassignedLabel, report.Unassigned.PropertyCount, report.Unassigned.Figures));

        return table.Add(OwnerRow(Total, null, report.Total));
    }

    public static byte[] Csv(OwnerPerformanceReport report) => CsvWriter.Write(Table(report));

    public static byte[] Xlsx(OwnerPerformanceReport report) => XlsxWriter.Write([new ReportSheet(SheetName, Table(report))]);

    /// <summary>The file name of the report: <c>casazen-prestazioni-per-proprietario-competenza-20261001-20261031.csv</c>; <paramref name="extension"/> is <c>csv</c> or <c>xlsx</c>.</summary>
    public static string FileName(HostDashboardPeriod period, string extension = "csv")
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"casazen-prestazioni-per-proprietario-competenza-{Compact(period.From)}-{Compact(period.To.AddDays(-1))}.{extension}";
    }

    // Nights and stays can be fractions when a house is shared; two places are enough to read them (the number cell rounds to them).
    private static IReadOnlyList<ReportCell> OwnerRow(string label, int? propertyCount, OwnerPerformanceFigures figures) =>
        [
            label,
            ReportCell.Integer(propertyCount),
            ReportCell.Number(figures.OccupiedNights),
            ReportCell.Number(figures.AvailableNights),
            ReportCell.Number(figures.ClosedNights),
            ReportCell.Percent(figures.OccupancyRate),
            ReportCell.Money(figures.Revenue),
            ReportCell.Number(figures.StayCount),
            ReportCell.Number(figures.RevenueNights),
            ReportCell.Money(figures.Adr),
            ReportCell.Money(figures.RevPar),
        ];

    private static string Date(DateTime day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Compact(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
