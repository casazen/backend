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
