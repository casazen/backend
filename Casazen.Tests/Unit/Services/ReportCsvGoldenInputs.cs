using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F03/US05/T01 (NF-101, brief AC3): the fixed inputs of the golden CSV files of the list reports, and the CSV each report writes from them. The inputs are made
/// to stress the file: a name a spreadsheet would run as a formula, a separator and quotes in a name, amounts on the half cent, a negative one, a zero, a rate on
/// the half of the cent of a point, an empty figure, two currencies, a missing comparison. Nothing here depends on the clock, the culture or a random value (the ids
/// are never written), so the same call always writes the same bytes. The expected files of <see cref="ReportCsvGoldenTests"/> were written by the documents of
/// <c>origin/develop</c> before they built a <c>ReportTable</c>, from this very file.
/// </summary>
internal static class ReportCsvGoldenInputs
{
    /// <summary>Every list report of the export, under the name its golden file is kept by.</summary>
    public static readonly string[] Reports =
    [
        "revenue-by-property",
        "occupancy-by-property",
        "bookings-by-channel",
        "performance-total",
        "performance-by-property-compared",
        "performance-by-channel",
        "trend",
        "trend-compared",
        "trend-by-property-compared",
        "collections",
        "balances-to-collect",
        "channel-costs",
        "by-owner",
        "tourist-tax",
        "tourist-tax-owed-only",
    ];

    private static readonly TouristTaxCsvTexts Texts = new("Nessun importo registrato", "Importo incompleto");
    private static readonly HostDashboardPeriod October = HostReportRules.PeriodOf(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
    private static readonly HostDashboardPeriod LastOctober = HostReportRules.PeriodOf(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31));

    /// <summary>The CSV the report writes from the fixed inputs: the very bytes of the file.</summary>
    public static byte[] Csv(string report) => report switch
    {
        "revenue-by-property" => HostReportDocuments.Csv(Revenue()),
        "occupancy-by-property" => HostReportDocuments.Csv(Occupancy()),
        "bookings-by-channel" => HostReportDocuments.Csv(Channels()),
        "performance-total" => HostReportDocuments.Csv(Performance(false, HostPerformanceGroupBy.None), HostPerformanceGroupBy.None),
        "performance-by-property-compared" => HostReportDocuments.Csv(Performance(true, HostPerformanceGroupBy.Property), HostPerformanceGroupBy.Property),
        "performance-by-channel" => HostReportDocuments.Csv(Performance(false, HostPerformanceGroupBy.Channel), HostPerformanceGroupBy.Channel),
        "trend" => HostReportDocuments.Csv(Trend(false, HostPerformanceGroupBy.None), HostPerformanceGroupBy.None),
        "trend-compared" => HostReportDocuments.Csv(Trend(true, HostPerformanceGroupBy.None), HostPerformanceGroupBy.None),
        "trend-by-property-compared" => HostReportDocuments.Csv(Trend(true, HostPerformanceGroupBy.Property), HostPerformanceGroupBy.Property),
        "collections" => HostCollectionsReportDocuments.Csv(Collections()),
        "balances-to-collect" => HostCollectionsReportDocuments.Csv(Balances()),
        "channel-costs" => ChannelCostsReportDocuments.Csv(ChannelCosts()),
        "by-owner" => OwnerReportDocuments.Csv(Owners()),
        "tourist-tax" => TouristTaxSummaryDocuments.Csv(TouristTax(false), Texts),
        "tourist-tax-owed-only" => TouristTaxSummaryDocuments.Csv(TouristTax(true), Texts),
        _ => throw new ArgumentOutOfRangeException(nameof(report), report, null),
    };

    private static HostPerformanceFigures Figures(int occupied, int available, int closed, decimal revenue, int stays, int nights, bool hasNights = true) =>
        new(occupied, available, closed, revenue, stays, nights) { HasNights = hasNights };

    private static HostRevenueByProperty Revenue() => new(
        October,
        [
            new HostRevenueByPropertyRow(Guid.NewGuid(), "Casa Bianca", 1234.56m, 3, 12),
            new HostRevenueByPropertyRow(Guid.NewGuid(), "=SUM(A1) Villa", 0.07m, 1, 1),
            new HostRevenueByPropertyRow(Guid.NewGuid(), "Trullo; il \"Nuovo\"", 2.675m, 2, 5),
            new HostRevenueByPropertyRow(Guid.NewGuid(), "Rimborsata", -0.005m, 0, 0),
            new HostRevenueByPropertyRow(Guid.NewGuid(), "Vuota", 0m, 0, 0),
        ],
        1237.305m,
        6,
        18);

    private static HostOccupancyByProperty Occupancy() => new(
        October,
        [
            new HostOccupancyByPropertyRow(Guid.NewGuid(), "Casa Bianca", new HostDashboardOccupancy(10, 31, 2)),
            new HostOccupancyByPropertyRow(Guid.NewGuid(), "Trullo", new HostDashboardOccupancy(0, 0, 0)),
            new HostOccupancyByPropertyRow(Guid.NewGuid(), "Lunga attesa", new HostDashboardOccupancy(1, 20000, 0)),
            new HostOccupancyByPropertyRow(Guid.NewGuid(), "+Chiusa", new HostDashboardOccupancy(0, 7, 24)),
        ],
        new HostDashboardOccupancy(11, 20038, 26));

    private static HostBookingsByChannel Channels() => new(
        October,
        [
            new HostBookingsByChannelRow(BookingSource.Direct, 1, 3, 100.005m),
            new HostBookingsByChannelRow(BookingSource.Airbnb, 2, 4, 99.99m),
            new HostBookingsByChannelRow(BookingSource.BookingCom, 4, 20, 1500.5m),
        ],
        7,
        27,
        1700.495m);

    private static HostPerformanceReport Performance(bool compared, HostPerformanceGroupBy groupBy)
    {
        var channel = groupBy == HostPerformanceGroupBy.Channel;
        var now = Figures(10, 31, 2, 1234.56m, 3, 12, hasNights: !channel);
        var before = Figures(8, 31, 0, 1000m, 2, 8, hasNights: !channel);
        var odd = Figures(1, 20000, 3, 2.675m, 3, 7, hasNights: !channel);
        var oddBefore = Figures(2, 20000, 0, 0.5m, 1, 3, hasNights: !channel);
        var noComparison = Figures(0, 0, 0, 0m, 0, 0, hasNights: !channel);
        return new HostPerformanceReport(
            October,
            compared ? LastOctober : null,
            groupBy == HostPerformanceGroupBy.None
                ? []
                :
                [
                    new HostPerformanceRow(channel ? "Direct" : "casa", channel ? null : Guid.NewGuid(), channel ? "Direct" : "Casa Bianca", now, compared ? before : null),
                    new HostPerformanceRow(channel ? "Airbnb" : "villa", channel ? null : Guid.NewGuid(), channel ? "Airbnb" : "=Villa", odd, compared ? oddBefore : null),
                    new HostPerformanceRow(channel ? "BookingCom" : "trullo", channel ? null : Guid.NewGuid(), channel ? "BookingCom" : "Trullo", noComparison, compared ? noComparison : null),
                ],
            new HostPerformanceRow(HostPerformanceRules.TotalKey, null, "Totale", now, compared ? before : null));
    }

    private static HostTrendReport Trend(bool compared, HostPerformanceGroupBy groupBy)
    {
        var weekOne = new HostTrendBucket(
            "2026-W40",
            new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            compared ? new DateTime(2025, 9, 29, 0, 0, 0, DateTimeKind.Utc) : null,
            compared ? new DateTime(2025, 10, 6, 0, 0, 0, DateTimeKind.Utc) : null);
        var weekTwo = new HostTrendBucket(
            "2026-W41",
            new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            compared ? new DateTime(2025, 10, 6, 0, 0, 0, DateTimeKind.Utc) : null,
            compared ? new DateTime(2025, 10, 13, 0, 0, 0, DateTimeKind.Utc) : null);
        var total = Series(HostPerformanceRules.TotalKey, "Totale", compared, 312.5m);
        List<HostTrendSeries> series = groupBy == HostPerformanceGroupBy.None
            ? [total]
            : [Series("casa", "Casa Bianca", compared, 200.125m), Series(HostPerformanceRules.OtherSeriesKey, "Altri", compared, 112.375m)];
        return new HostTrendReport(October, compared ? LastOctober : null, [weekOne, weekTwo], series);
    }

    private static HostTrendSeries Series(string key, string label, bool compared, decimal revenue) => new(
        key,
        label,
        [
            new HostTrendPoint("2026-W40", Figures(4, 7, 0, revenue, 1, 4)),
            new HostTrendPoint("2026-W41", Figures(0, 7, 0, 0m, 0, 0)),
        ],
        compared
            ? [new HostTrendPoint("2025-W40", Figures(3, 7, 0, 200m, 1, 3)), null]
            : []);

    private static HostCollectionsReport Collections() => new(
        October,
        new HostDashboardCollected(1500.505m, 4),
        [
            new HostCollectionsBucket(HostCollectionsBucketUnit.Day, "2026-10-01", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), 1000.25m, 3),
            new HostCollectionsBucket(HostCollectionsBucketUnit.Day, "2026-10-02", new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 2), 500.255m, 1),
        ],
        [
            new HostCollectionsMethodRow(PaymentMethod.CreditCard, 1000.25m, 3, 0.6666m),
            new HostCollectionsMethodRow(PaymentMethod.CashOnArrival, 500.255m, 1, 0.33335m),
        ],
        [new HostCollectionsChannelRow(BookingSource.Direct, 1500.505m, 4, 1m)],
        new HostCredits(new HostStripeCredits(false, [], 0, 0), new HostChannelCredits(false, [], 0)));

    private static HostBalancesToCollect Balances() => new(
        new DateOnly(2026, 10, 9),
        [
            new HostBalanceRow(Guid.NewGuid(), "ABCDEFGHJK", Guid.NewGuid(), "Casa Bianca", BookingSource.Direct, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), "EUR", 500m, 200.5m, 299.5m, true, HostBalanceBasis.Payments),
            new HostBalanceRow(Guid.NewGuid(), "KLMNPQRSTV", Guid.NewGuid(), "Chalet Lugano", BookingSource.Airbnb, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25), "CHF", null, null, 120m, false, HostBalanceBasis.ChannelManager),
            new HostBalanceRow(Guid.NewGuid(), "WXYZ234567", Guid.NewGuid(), "=Casa", BookingSource.BookingCom, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 4), "EUR", 80.005m, 0m, 80.005m, false, HostBalanceBasis.Payments),
        ],
        [new HostBalanceTotal("EUR", 379.505m, 2), new HostBalanceTotal("CHF", 120m, 1)]);

    private static ChannelCostsReport ChannelCosts() => new(
        October,
        [
            new ChannelCostRow(BookingSource.Airbnb, ChannelCostKind.ChannelCommission, 3, 1500.5m, 225.08m, 1275.42m, 0.15m, ChannelCommissionMode.DeductedFromPayout, 0, true),
            new ChannelCostRow(BookingSource.Direct, ChannelCostKind.NoCost, 1, 99.99m, null, null, null, null, 1, false),
            new ChannelCostRow(BookingSource.BookingCom, ChannelCostKind.PaymentFee, 2, 300.005m, 12.345m, 287.66m, 0.04115m, ChannelCommissionMode.InvoicedSeparately, 0, false),
        ],
        new ChannelCostRow(null, null, 6, 1900.495m, 237.425m, 1663.07m, 0.12495m, null, 1, false));

    private static OwnerPerformanceReport Owners() => new(
        October,
        [
            new OwnerPerformanceRow(Guid.NewGuid(), "=Mario Rossi", false, 2, new OwnerPerformanceFigures(1234.565m, 10.5m, 31m, 0m, 1.5m, 10.5m)),
            new OwnerPerformanceRow(null, null, true, 1, new OwnerPerformanceFigures(100m, 3m, 31m, 2m, 1m, 3m)),
        ],
        new OwnerPerformanceRow(null, null, false, 1, new OwnerPerformanceFigures(0m, 0m, 0m, 0m, 0m, 0m)),
        new OwnerPerformanceFigures(1334.57m, 13.5m, 62m, 2m, 2.5m, 13.5m));

    private static TouristTaxSummary TouristTax(bool paymentsHidden)
    {
        var period = new FiscalReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var remittance = new TouristTaxRemittanceLine(
            Guid.NewGuid(), "Roma", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 100m, new DateOnly(2026, 4, 10), "Bonifico 123", "=nota del commercialista", true);
        var late = new TouristTaxRemittanceLine(
            Guid.NewGuid(), "Roma", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), 0.005m, new DateOnly(2026, 7, 10), null, "Nota; con \"virgolette\"", false);
        return new TouristTaxSummary(
            period,
            [
                new ComuneTaxSummary("Roma", 10, 25, 40, 123.45m, 1, paymentsHidden ? null : 100m, paymentsHidden ? null : 23.45m, paymentsHidden ? null : TouristTaxSummaryStatus.ToPay, paymentsHidden ? null : [remittance, late]),
                new ComuneTaxSummary("Firenze", 2, 4, 5, 10m, 0, paymentsHidden ? null : 20m, paymentsHidden ? null : -10m, paymentsHidden ? null : TouristTaxSummaryStatus.Overpaid, paymentsHidden ? null : []),
                new ComuneTaxSummary("Lecco", 2, 5, 4, 0m, 2, paymentsHidden ? null : 0m, paymentsHidden ? null : 0m, paymentsHidden ? null : TouristTaxSummaryStatus.NothingOwed, paymentsHidden ? null : []),
            ],
            new TouristTaxSummaryTotals(14, 34, 49, 133.45m, 3, paymentsHidden ? null : 120m, paymentsHidden ? null : 13.45m),
            paymentsHidden);
    }
}
