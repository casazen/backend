using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The titles, the CSV files and the Excel files of the host reports (SR-10, NF-101), in Italian like the fiscal ones (<see cref="FiscalReportDocuments"/>): the CSV
/// for a spreadsheet set to Italian (<see cref="CsvWriter"/>). Every report says in its title which base its numbers are on, and the file carries that title on its
/// first row, so no figure travels without its label. Nothing about a guest: properties, channels, counts and amounts only.
/// </summary>
/// <remarks>
/// Each report is built once as a <see cref="ReportTable"/> (<c>Table</c>): its title row, its header and its typed rows, so <c>Csv</c> and <c>Xlsx</c> carry the
/// same columns and the same numbers, an amount to the cent, a percentage to the cent of a point and a day as a day. The CSV is what it always was, byte for byte.
/// </remarks>
public static class HostReportDocuments
{
    public const string RevenueByPropertyTitle = "Ricavo per immobile, per competenza";

    public const string OccupancyByPropertyTitle = "Occupazione per immobile, notti occupate su notti disponibili";

    public const string BookingsByChannelTitle = "Prenotazioni per canale, per competenza";

    public const string PerformanceTotalTitle = "Prestazioni complessive, per competenza";

    public const string PerformanceByPropertyTitle = "Prestazioni per immobile, per competenza";

    public const string PerformanceByChannelTitle = "Prestazioni per canale, per competenza";

    public const string TrendTotalTitle = "Andamento, per competenza";

    public const string TrendByPropertyTitle = "Andamento per immobile, per competenza";

    public const string TrendByChannelTitle = "Andamento per canale, per competenza";

    public const string RevenueByPropertySheet = "Ricavo per immobile";

    public const string OccupancyByPropertySheet = "Occupazione per immobile";

    public const string BookingsByChannelSheet = "Prenotazioni per canale";

    private const string Total = "Totale";

    public static ReportTable Table(HostRevenueByProperty report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var table = Start(RevenueByPropertyTitle, report.Period, "Immobile", "Ricavo per competenza (€)", "Soggiorni", "Notti");
        foreach (var r in report.Rows)
            table.Add(r.PropertyName, ReportCell.Money(r.Amount), r.StayCount, r.Nights);

        return table.Add(Total, ReportCell.Money(report.Amount), report.StayCount, report.Nights);
    }

    public static byte[] Csv(HostRevenueByProperty report) => CsvWriter.Write(Table(report));

    public static byte[] Xlsx(HostRevenueByProperty report) => XlsxWriter.Write([new ReportSheet(RevenueByPropertySheet, Table(report))]);

    public static ReportTable Table(HostOccupancyByProperty report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var table = Start(
            OccupancyByPropertyTitle, report.Period, "Immobile", "Notti occupate", "Notti disponibili", "Notti chiuse dall'host", "Occupazione (%)");
        foreach (var r in report.Rows)
            table.Add(OccupancyRow(r.PropertyName, r.Occupancy));

        return table.Add(OccupancyRow(Total, report.Total));
    }

    public static byte[] Csv(HostOccupancyByProperty report) => CsvWriter.Write(Table(report));

    public static byte[] Xlsx(HostOccupancyByProperty report) => XlsxWriter.Write([new ReportSheet(OccupancyByPropertySheet, Table(report))]);

    public static ReportTable Table(HostBookingsByChannel report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var table = Start(
            BookingsByChannelTitle, report.Period, "Canale", "Prenotazioni", "Notti", "Ricavo per competenza (€)", "Quota delle prenotazioni (%)");
        foreach (var r in report.Rows)
        {
            table.Add(
                FiscalReportDocuments.ChannelLabel(r.Source.ToString()),
                r.BookingCount,
                r.Nights,
                ReportCell.Money(r.Revenue),
                Share(r.BookingCount, report.BookingCount));
        }

        return table.Add(Total, report.BookingCount, report.Nights, ReportCell.Money(report.Revenue), Share(report.BookingCount, report.BookingCount));
    }

    public static byte[] Csv(HostBookingsByChannel report) => CsvWriter.Write(Table(report));

    public static byte[] Xlsx(HostBookingsByChannel report) => XlsxWriter.Write([new ReportSheet(BookingsByChannelSheet, Table(report))]);

    public static string PerformanceTitle(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => PerformanceByPropertyTitle,
        HostPerformanceGroupBy.Channel => PerformanceByChannelTitle,
        _ => PerformanceTotalTitle,
    };

    public static string TrendTitle(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => TrendByPropertyTitle,
        HostPerformanceGroupBy.Channel => TrendByChannelTitle,
        _ => TrendTotalTitle,
    };

    public static string PerformanceSheet(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => "Prestazioni per immobile",
        HostPerformanceGroupBy.Channel => "Prestazioni per canale",
        _ => "Prestazioni",
    };

    public static string TrendSheet(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => "Andamento per immobile",
        HostPerformanceGroupBy.Channel => "Andamento per canale",
        _ => "Andamento",
    };

    public static string PerformanceFileName(HostPerformanceGroupBy groupBy, bool trend) => (trend ? "andamento" : "prestazioni") + groupBy switch
    {
        HostPerformanceGroupBy.Property => "-per-immobile-competenza",
        HostPerformanceGroupBy.Channel => "-per-canale-competenza",
        _ => "-competenza",
    };

    public static ReportTable Table(HostPerformanceReport report, HostPerformanceGroupBy groupBy)
    {
        ArgumentNullException.ThrowIfNull(report);

        var compared = report.PreviousYearPeriod is not null;
        var header = new List<string>
        {
            groupBy switch
            {
                HostPerformanceGroupBy.Property => "Immobile",
                HostPerformanceGroupBy.Channel => "Canale",
                _ => "Perimetro",
            },
            "Notti occupate",
            "Notti disponibili",
            "Notti chiuse dall'host",
            "Occupazione (%)",
            "Ricavo per competenza (€)",
            "Soggiorni",
            "Notti dei soggiorni",
            "ADR (€)",
            "RevPAR (€)",
        };
        if (compared)
            header.AddRange(ComparisonHeader);

        var table = StartCompared(PerformanceTitle(groupBy), report.Period, report.PreviousYearPeriod, [.. header]);
        foreach (var row in report.Rows)
        {
            var label = groupBy == HostPerformanceGroupBy.Channel ? FiscalReportDocuments.ChannelLabel(row.Key) : row.Label;
            table.Add(PerformanceRow(label, row, compared));
        }

        return table.Add(PerformanceRow(Total, report.Total, compared));
    }

    public static byte[] Csv(HostPerformanceReport report, HostPerformanceGroupBy groupBy) => CsvWriter.Write(Table(report, groupBy));

    public static byte[] Xlsx(HostPerformanceReport report, HostPerformanceGroupBy groupBy) =>
        XlsxWriter.Write([new ReportSheet(PerformanceSheet(groupBy), Table(report, groupBy))]);

    public static ReportTable Table(HostTrendReport report, HostPerformanceGroupBy groupBy)
    {
        ArgumentNullException.ThrowIfNull(report);

        var compared = report.PreviousYearPeriod is not null;
        var header = new List<string>
        {
            groupBy switch
            {
                HostPerformanceGroupBy.Property => "Immobile",
                HostPerformanceGroupBy.Channel => "Canale",
                _ => "Perimetro",
            },
            "Periodo",
            "Dal",
            "Al",
            "Notti occupate",
            "Notti disponibili",
            "Occupazione (%)",
            "Ricavo per competenza (€)",
            "Soggiorni",
            "Notti dei soggiorni",
            "ADR (€)",
            "RevPAR (€)",
        };
        if (compared)
        {
            header.AddRange(
            [
                "Occupazione anno precedente (%)",
                "Ricavo anno precedente (€)",
                "ADR anno precedente (€)",
                "RevPAR anno precedente (€)",
            ]);
        }

        var table = StartCompared(TrendTitle(groupBy), report.Period, report.PreviousYearPeriod, [.. header]);
        foreach (var series in report.Series)
        {
            var label = SeriesLabel(series, groupBy);
            for (var index = 0; index < report.Buckets.Count; index++)
            {
                var bucket = report.Buckets[index];
                var figures = series.Points[index].Figures;
                var cells = new List<ReportCell>
                {
                    label,
                    bucket.Label,
                    ReportCell.Date(DateOnly.FromDateTime(bucket.From)),
                    ReportCell.Date(DateOnly.FromDateTime(bucket.To.AddDays(-1))),
                    ReportCell.Integer(figures.HasNights ? figures.OccupiedNights : null),
                    ReportCell.Integer(figures.HasNights ? figures.AvailableNights : null),
                    ReportCell.Percent(figures.OccupancyRate),
                    ReportCell.Money(figures.Revenue),
                    figures.StayCount,
                    figures.RevenueNights,
                    ReportCell.Money(figures.Adr),
                    ReportCell.Money(figures.RevPar),
                };
                if (compared)
                {
                    var before = index < series.PreviousYear.Count ? series.PreviousYear[index]?.Figures : null;
                    cells.Add(ReportCell.Percent(before?.OccupancyRate));
                    cells.Add(ReportCell.Money(before?.Revenue));
                    cells.Add(ReportCell.Money(before?.Adr));
                    cells.Add(ReportCell.Money(before?.RevPar));
                }

                table.Add(cells);
            }
        }

        return table;
    }

    public static byte[] Csv(HostTrendReport report, HostPerformanceGroupBy groupBy) => CsvWriter.Write(Table(report, groupBy));

    public static byte[] Xlsx(HostTrendReport report, HostPerformanceGroupBy groupBy) =>
        XlsxWriter.Write([new ReportSheet(TrendSheet(groupBy), Table(report, groupBy))]);

    public static string FileName(string report, HostDashboardPeriod period, string extension = "csv")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(report);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"casazen-{report}-{Compact(period.From)}-{Compact(period.To.AddDays(-1))}.{extension}";
    }

    private static ReportTable Start(string title, HostDashboardPeriod period, params string[] header) =>
        new($"{title}, dal {Date(period.From)} al {Date(period.To.AddDays(-1))}", header);

    private static ReportTable StartCompared(
        string title, HostDashboardPeriod period, HostDashboardPeriod? comparedWith, string[] header)
    {
        var days = $"{title}, dal {Date(period.From)} al {Date(period.To.AddDays(-1))}";
        if (comparedWith is not null)
            days += $", confrontato con dal {Date(comparedWith.From)} al {Date(comparedWith.To.AddDays(-1))}";

        return new ReportTable(days, header);
    }

    private static readonly string[] ComparisonHeader =
    [
        "Occupazione anno precedente (%)",
        "Ricavo anno precedente (€)",
        "ADR anno precedente (€)",
        "RevPAR anno precedente (€)",
        "Variazione ricavo (%)",
        "Variazione occupazione (punti)",
        "Variazione ADR (%)",
        "Variazione RevPAR (%)",
    ];

    private static IReadOnlyList<ReportCell> PerformanceRow(string label, HostPerformanceRow row, bool compared)
    {
        var figures = row.Figures;
        var cells = new List<ReportCell>
        {
            label,
            ReportCell.Integer(figures.HasNights ? figures.OccupiedNights : null),
            ReportCell.Integer(figures.HasNights ? figures.AvailableNights : null),
            ReportCell.Integer(figures.HasNights ? figures.ClosedNights : null),
            ReportCell.Percent(figures.OccupancyRate),
            ReportCell.Money(figures.Revenue),
            figures.StayCount,
            figures.RevenueNights,
            ReportCell.Money(figures.Adr),
            ReportCell.Money(figures.RevPar),
        };
        if (!compared)
            return cells;

        var before = row.PreviousYear;
        cells.Add(ReportCell.Percent(before?.OccupancyRate));
        cells.Add(ReportCell.Money(before?.Revenue));
        cells.Add(ReportCell.Money(before?.Adr));
        cells.Add(ReportCell.Money(before?.RevPar));
        cells.Add(ReportCell.Percent(PercentToFraction(HostPerformanceRules.DeltaPercent(figures.Revenue, before?.Revenue))));
        cells.Add(ReportCell.Number(HostPerformanceRules.DeltaPoints(figures.OccupancyRate, before?.OccupancyRate)));
        cells.Add(ReportCell.Percent(PercentToFraction(HostPerformanceRules.DeltaPercent(figures.Adr, before?.Adr))));
        cells.Add(ReportCell.Percent(PercentToFraction(HostPerformanceRules.DeltaPercent(figures.RevPar, before?.RevPar))));
        return cells;
    }

    private static string SeriesLabel(HostTrendSeries series, HostPerformanceGroupBy groupBy)
    {
        if (series.Key == HostPerformanceRules.TotalKey)
            return Total;

        if (series.Key == HostPerformanceRules.OtherSeriesKey)
            return groupBy == HostPerformanceGroupBy.Channel ? "Altri canali" : "Altri immobili";

        return groupBy == HostPerformanceGroupBy.Channel ? FiscalReportDocuments.ChannelLabel(series.Key) : series.Label;
    }

    private static IReadOnlyList<ReportCell> OccupancyRow(string name, HostDashboardOccupancy occupancy) =>
        [
            name,
            occupancy.OccupiedNights,
            occupancy.AvailableNights,
            occupancy.ClosedNights,
            ReportCell.Percent(occupancy.Rate),
        ];

    private static ReportCell Share(int part, int whole) => ReportCell.Percent(whole == 0 ? null : (decimal)part / whole);

    private static decimal? PercentToFraction(decimal? percent) => percent / 100m;

    private static string Date(DateTime day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Compact(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
