using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The titles and the CSV files of the host reports (SR-10), in Italian like the fiscal ones (<see cref="FiscalReportDocuments"/>): for a
/// spreadsheet set to Italian (<see cref="CsvWriter"/>). Every report says in its title which base its numbers are on, and the file
/// carries that title on its first row, so no figure travels without its label. Nothing about a guest: properties, channels, counts and
/// amounts only.
/// </summary>
public static class HostReportDocuments
{
    /// <summary>Title of the revenue by property: by accrual, the nights of the confirmed stays, as the Home.</summary>
    public const string RevenueByPropertyTitle = "Ricavo per immobile, per competenza";

    /// <summary>Title of the occupancy by property.</summary>
    public const string OccupancyByPropertyTitle = "Occupazione per immobile, notti occupate su notti disponibili";

    /// <summary>Title of the bookings by channel: by accrual, the confirmed stays with a night in the period.</summary>
    public const string BookingsByChannelTitle = "Prenotazioni per canale, per competenza";

    /// <summary>Title of the performance of the whole scope (NF-96): occupancy, revenue, ADR and RevPAR, by accrual.</summary>
    public const string PerformanceTotalTitle = "Prestazioni complessive, per competenza";

    /// <summary>Title of the performance per property.</summary>
    public const string PerformanceByPropertyTitle = "Prestazioni per immobile, per competenza";

    /// <summary>Title of the performance per channel.</summary>
    public const string PerformanceByChannelTitle = "Prestazioni per canale, per competenza";

    /// <summary>Title of the trend of the whole scope: the same figures for each week or month.</summary>
    public const string TrendTotalTitle = "Andamento, per competenza";

    /// <summary>Title of the trend per property.</summary>
    public const string TrendByPropertyTitle = "Andamento per immobile, per competenza";

    /// <summary>Title of the trend per channel.</summary>
    public const string TrendByChannelTitle = "Andamento per canale, per competenza";

    private const string Total = "Totale";

    // ---------------------------------------------------------------- files

    public static byte[] Csv(HostRevenueByProperty report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = Start(RevenueByPropertyTitle, report.Period, "Immobile", "Ricavo per competenza (€)", "Soggiorni", "Notti");
        rows.AddRange(report.Rows.Select(r => Row(r.PropertyName, r.Amount, r.StayCount, r.Nights)));
        rows.Add(Row(Total, report.Amount, report.StayCount, report.Nights));
        return CsvWriter.Write(rows);
    }

    public static byte[] Csv(HostOccupancyByProperty report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = Start(
            OccupancyByPropertyTitle, report.Period, "Immobile", "Notti occupate", "Notti disponibili", "Notti chiuse dall'host", "Occupazione (%)");
        rows.AddRange(report.Rows.Select(r => OccupancyRow(r.PropertyName, r.Occupancy)));
        rows.Add(OccupancyRow(Total, report.Total));
        return CsvWriter.Write(rows);
    }

    public static byte[] Csv(HostBookingsByChannel report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = Start(
            BookingsByChannelTitle, report.Period, "Canale", "Prenotazioni", "Notti", "Ricavo per competenza (€)", "Quota delle prenotazioni (%)");
        rows.AddRange(report.Rows.Select(r => Row(
            FiscalReportDocuments.ChannelLabel(r.Source.ToString()),
            r.BookingCount,
            r.Nights,
            r.Revenue,
            Share(r.BookingCount, report.BookingCount))));
        rows.Add(Row(Total, report.BookingCount, report.Nights, report.Revenue, Share(report.BookingCount, report.BookingCount)));
        return CsvWriter.Write(rows);
    }

    /// <summary>The title of a performance report, with its base: what the file carries on its first row and the JSON as <c>title</c>.</summary>
    public static string PerformanceTitle(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => PerformanceByPropertyTitle,
        HostPerformanceGroupBy.Channel => PerformanceByChannelTitle,
        _ => PerformanceTotalTitle,
    };

    /// <summary>The title of a trend, with its base.</summary>
    public static string TrendTitle(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => TrendByPropertyTitle,
        HostPerformanceGroupBy.Channel => TrendByChannelTitle,
        _ => TrendTotalTitle,
    };

    /// <summary>The file name of a performance report or of a trend: <c>prestazioni-per-immobile-competenza</c>, <c>andamento-competenza</c>...</summary>
    public static string PerformanceFileName(HostPerformanceGroupBy groupBy, bool trend) => (trend ? "andamento" : "prestazioni") + groupBy switch
    {
        HostPerformanceGroupBy.Property => "-per-immobile-competenza",
        HostPerformanceGroupBy.Channel => "-per-canale-competenza",
        _ => "-competenza",
    };

    /// <summary>
    /// The performance report as a file: the title with its base and the days on the first row, the header, a row for each property or
    /// channel and the total. The columns of the year before and of the differences are there only when the report compared; a
    /// channel has no nights (they belong to a property), so those cells are empty.
    /// </summary>
    public static byte[] Csv(HostPerformanceReport report, HostPerformanceGroupBy groupBy)
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

        var rows = StartCompared(PerformanceTitle(groupBy), report.Period, report.PreviousYearPeriod, [.. header]);
        foreach (var row in report.Rows)
        {
            var label = groupBy == HostPerformanceGroupBy.Channel ? FiscalReportDocuments.ChannelLabel(row.Key) : row.Label;
            rows.Add(PerformanceRow(label, row, compared));
        }

        rows.Add(PerformanceRow(Total, report.Total, compared));
        return CsvWriter.Write(rows);
    }

    /// <summary>
    /// The trend as a file: the title with its base and the days on the first row, the header, and a row for each bucket of each line
    /// (the whole scope, a property or a channel), with the same days one year earlier next to it when the trend compared.
    /// </summary>
    public static byte[] Csv(HostTrendReport report, HostPerformanceGroupBy groupBy)
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

        var rows = StartCompared(TrendTitle(groupBy), report.Period, report.PreviousYearPeriod, [.. header]);
        foreach (var series in report.Series)
        {
            var label = SeriesLabel(series, groupBy);
            for (var index = 0; index < report.Buckets.Count; index++)
            {
                var bucket = report.Buckets[index];
                var figures = series.Points[index].Figures;
                var cells = new List<object?>
                {
                    label,
                    bucket.Label,
                    Date(bucket.From),
                    Date(bucket.To.AddDays(-1)),
                    figures.HasNights ? (int?)figures.OccupiedNights : null,
                    figures.HasNights ? (int?)figures.AvailableNights : null,
                    Percent(figures.OccupancyRate),
                    figures.Revenue,
                    figures.StayCount,
                    figures.RevenueNights,
                    figures.Adr,
                    figures.RevPar,
                };
                if (compared)
                {
                    var before = index < series.PreviousYear.Count ? series.PreviousYear[index]?.Figures : null;
                    cells.Add(Percent(before?.OccupancyRate));
                    cells.Add(before?.Revenue);
                    cells.Add(before?.Adr);
                    cells.Add(before?.RevPar);
                }

                rows.Add(cells);
            }
        }

        return CsvWriter.Write(rows);
    }

    /// <summary>The file name of a report: the report, its base where it has one, and the days (<c>casazen-ricavo-per-immobile-competenza-20261001-20261031.csv</c>).</summary>
    public static string FileName(string report, HostDashboardPeriod period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(report);
        ArgumentNullException.ThrowIfNull(period);

        return $"casazen-{report}-{Compact(period.From)}-{Compact(period.To.AddDays(-1))}.csv";
    }

    // ---------------------------------------------------------------- shared

    // The title with the days, then the header: the first rows of every file.
    private static List<IReadOnlyList<object?>> Start(string title, HostDashboardPeriod period, params string[] header) =>
    [
        Row($"{title}, dal {Date(period.From)} al {Date(period.To.AddDays(-1))}"),
        Row(header),
    ];

    // The same, for a report that also gives the same days one year earlier: the first row says which days they are.
    private static List<IReadOnlyList<object?>> StartCompared(
        string title, HostDashboardPeriod period, HostDashboardPeriod? comparedWith, string[] header)
    {
        var days = $"{title}, dal {Date(period.From)} al {Date(period.To.AddDays(-1))}";
        if (comparedWith is not null)
            days += $", confrontato con dal {Date(comparedWith.From)} al {Date(comparedWith.To.AddDays(-1))}";

        return [Row(days), Row(header)];
    }

    // The columns of the year before and of the differences a compared performance report adds.
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

    // One row of a performance report. The nights are empty for a channel (they belong to a property); the year before is empty when
    // there was nothing to compare with, and so are the differences.
    private static IReadOnlyList<object?> PerformanceRow(string label, HostPerformanceRow row, bool compared)
    {
        var figures = row.Figures;
        var cells = new List<object?>
        {
            label,
            figures.HasNights ? (int?)figures.OccupiedNights : null,
            figures.HasNights ? (int?)figures.AvailableNights : null,
            figures.HasNights ? (int?)figures.ClosedNights : null,
            Percent(figures.OccupancyRate),
            figures.Revenue,
            figures.StayCount,
            figures.RevenueNights,
            figures.Adr,
            figures.RevPar,
        };
        if (!compared)
            return cells;

        var before = row.PreviousYear;
        cells.Add(Percent(before?.OccupancyRate));
        cells.Add(before?.Revenue);
        cells.Add(before?.Adr);
        cells.Add(before?.RevPar);
        cells.Add(HostPerformanceRules.DeltaPercent(figures.Revenue, before?.Revenue));
        cells.Add(HostPerformanceRules.DeltaPoints(figures.OccupancyRate, before?.OccupancyRate));
        cells.Add(HostPerformanceRules.DeltaPercent(figures.Adr, before?.Adr));
        cells.Add(HostPerformanceRules.DeltaPercent(figures.RevPar, before?.RevPar));
        return cells;
    }

    // The name of a line of a trend as the file shows it: the whole scope, a property, a channel, or what did not fit in the lines.
    private static string SeriesLabel(HostTrendSeries series, HostPerformanceGroupBy groupBy)
    {
        if (series.Key == HostPerformanceRules.TotalKey)
            return Total;

        if (series.Key == HostPerformanceRules.OtherSeriesKey)
            return groupBy == HostPerformanceGroupBy.Channel ? "Altri canali" : "Altri immobili";

        return groupBy == HostPerformanceGroupBy.Channel ? FiscalReportDocuments.ChannelLabel(series.Key) : series.Label;
    }

    private static IReadOnlyList<object?> OccupancyRow(string name, HostDashboardOccupancy occupancy) =>
        Row(
            name,
            occupancy.OccupiedNights,
            occupancy.AvailableNights,
            occupancy.ClosedNights,
            Percent(occupancy.Rate));

    // A rate (0 to 1) as a percentage to the cent of a point; empty without one.
    private static decimal? Percent(decimal? rate) =>
        rate is { } value ? Math.Round(value * 100m, 2, MidpointRounding.AwayFromZero) : null;

    // The share of the whole a part is, as a percentage; empty without a whole.
    private static decimal? Share(int part, int whole) => whole == 0 ? null : Percent((decimal)part / whole);

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string Date(DateTime day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Compact(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
