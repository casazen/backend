using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Casazen.Core.BankStatements;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F03/US05/T01 (REP-06, NF-101), the proof of the money rule: every list report downloads as CSV and as Excel with the same title row, the same columns
/// and the same numbers. Each workbook is read back (sheet by sheet, with the format of each cell) and set against the CSV of the same report: every amount
/// equals its CSV value to the cent, a percentage is the same number as a fraction, a day is the same day, a count is the same count, a text the same text (the
/// leading apostrophe of the CSV aside), and no number is ever a text. Amounts of different currencies are never added together, and a caller who reaches only
/// some of the properties gets no payment of the tourist tax in either file.
/// </summary>
public class ReportExportsProofTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Documents = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly TouristTaxCsvTexts Texts = new("Nessun importo registrato", "Importo incompleto");

    private static readonly HostDashboardPeriod October = HostReportRules.PeriodOf(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
    private static readonly HostDashboardPeriod LastOctober = HostReportRules.PeriodOf(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31));

    private sealed record XCell(string Kind, string Text);

    // ---------------------------------------------------------------- the reports, with numbers that stress the files

    private static HostPerformanceFigures Figures(int occupied, int available, int closed, decimal revenue, int stays, int nights, bool hasNights = true) =>
        new(occupied, available, closed, revenue, stays, nights) { HasNights = hasNights };

    private static HostRevenueByProperty Revenue() => new(
        October,
        [
            new HostRevenueByPropertyRow(Guid.NewGuid(), "Casa Bianca", 1234.56m, 3, 12),
            new HostRevenueByPropertyRow(Guid.NewGuid(), "=SUM(A1) Villa", 0.07m, 1, 1),
            new HostRevenueByPropertyRow(Guid.NewGuid(), "Trullo", 0m, 0, 0),
        ],
        1234.63m,
        4,
        13);

    private static HostOccupancyByProperty Occupancy() => new(
        October,
        [
            new HostOccupancyByPropertyRow(Guid.NewGuid(), "Casa Bianca", new HostDashboardOccupancy(10, 31, 2)),
            new HostOccupancyByPropertyRow(Guid.NewGuid(), "Trullo", new HostDashboardOccupancy(0, 0, 0)),
        ],
        new HostDashboardOccupancy(10, 31, 2));

    private static HostBookingsByChannel Channels() => new(
        October,
        [
            new HostBookingsByChannelRow(BookingSource.Direct, 3, 12, 1500.5m),
            new HostBookingsByChannelRow(BookingSource.Airbnb, 1, 2, 99.99m),
        ],
        4,
        14,
        1600.49m);

    private static HostPerformanceReport Performance(bool compared, HostPerformanceGroupBy groupBy)
    {
        var channel = groupBy == HostPerformanceGroupBy.Channel;
        var now = Figures(10, 31, 2, 1234.56m, 3, 12, hasNights: !channel);
        var before = Figures(8, 31, 0, 1000m, 2, 8, hasNights: !channel);
        var noComparison = Figures(0, 0, 0, 0m, 0, 0, hasNights: !channel);
        return new HostPerformanceReport(
            October,
            compared ? LastOctober : null,
            groupBy == HostPerformanceGroupBy.None
                ? []
                :
                [
                    new HostPerformanceRow(channel ? "Direct" : "casa", channel ? null : Guid.NewGuid(), channel ? "Direct" : "Casa Bianca", now, compared ? before : null),
                    new HostPerformanceRow(channel ? "Airbnb" : "trullo", channel ? null : Guid.NewGuid(), channel ? "Airbnb" : "Trullo", noComparison, compared ? noComparison : null),
                ],
            new HostPerformanceRow(HostPerformanceRules.TotalKey, null, "Totale", now, compared ? before : null));
    }

    private static HostTrendReport Trend(bool compared)
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
        var series = new HostTrendSeries(
            HostPerformanceRules.TotalKey,
            "Totale",
            [
                new HostTrendPoint("2026-W40", Figures(4, 7, 0, 312.5m, 1, 4)),
                new HostTrendPoint("2026-W41", Figures(0, 7, 0, 0m, 0, 0)),
            ],
            compared
                ? [new HostTrendPoint("2025-W40", Figures(3, 7, 0, 200m, 1, 3)), null]
                : []);
        return new HostTrendReport(October, compared ? LastOctober : null, [weekOne, weekTwo], [series]);
    }

    private static HostCollectionsReport Collections() => new(
        October,
        new HostDashboardCollected(1500.5m, 4),
        [
            new HostCollectionsBucket(HostCollectionsBucketUnit.Day, "2026-10-01", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), 1000.25m, 3),
            new HostCollectionsBucket(HostCollectionsBucketUnit.Day, "2026-10-02", new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 2), 500.25m, 1),
        ],
        [
            new HostCollectionsMethodRow(PaymentMethod.CreditCard, 1000.25m, 3, 0.6666m),
            new HostCollectionsMethodRow(PaymentMethod.CashOnArrival, 500.25m, 1, 0.3334m),
        ],
        [new HostCollectionsChannelRow(BookingSource.Direct, 1500.5m, 4, 1m)],
        new HostCredits(new HostStripeCredits(false, [], 0, 0), new HostChannelCredits(false, [], 0)));

    // Two currencies on purpose: the totals are one for each, and nothing adds them.
    private static HostBalancesToCollect Balances() => new(
        new DateOnly(2026, 10, 9),
        [
            new HostBalanceRow(Guid.NewGuid(), "ABCDEFGHJK", Guid.NewGuid(), "Casa Bianca", BookingSource.Direct, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), "EUR", 500m, 200.5m, 299.5m, true, HostBalanceBasis.Payments),
            new HostBalanceRow(Guid.NewGuid(), "KLMNPQRSTV", Guid.NewGuid(), "Chalet Lugano", BookingSource.Airbnb, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25), "CHF", null, null, 120m, false, HostBalanceBasis.ChannelManager),
        ],
        [new HostBalanceTotal("EUR", 299.5m, 1), new HostBalanceTotal("CHF", 120m, 1)]);

    private static ChannelCostsReport ChannelCosts() => new(
        October,
        [
            new ChannelCostRow(BookingSource.Airbnb, ChannelCostKind.ChannelCommission, 3, 1500.5m, 225.08m, 1275.42m, 0.15m, ChannelCommissionMode.DeductedFromPayout, 0, true),
            new ChannelCostRow(BookingSource.Direct, ChannelCostKind.NoCost, 1, 99.99m, null, null, null, null, 1, false),
        ],
        new ChannelCostRow(null, null, 4, 1600.49m, 225.08m, 1375.41m, 0.1406m, null, 1, false));

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
        return new TouristTaxSummary(
            period,
            [
                new ComuneTaxSummary("Roma", 10, 25, 40, 123.45m, 1, paymentsHidden ? null : 100m, paymentsHidden ? null : 23.45m, paymentsHidden ? null : TouristTaxSummaryStatus.ToPay, paymentsHidden ? null : [remittance]),
                new ComuneTaxSummary("Firenze", 2, 4, 5, 10m, 0, paymentsHidden ? null : 20m, paymentsHidden ? null : -10m, paymentsHidden ? null : TouristTaxSummaryStatus.Overpaid, paymentsHidden ? null : []),
            ],
            new TouristTaxSummaryTotals(12, 29, 45, 133.45m, 1, paymentsHidden ? null : 120m, paymentsHidden ? null : 13.45m),
            paymentsHidden);
    }

    private static (byte[] Csv, byte[] Xlsx) Files(string report) => report switch
    {
        "revenue-by-property" => (HostReportDocuments.Csv(Revenue()), HostReportDocuments.Xlsx(Revenue())),
        "occupancy-by-property" => (HostReportDocuments.Csv(Occupancy()), HostReportDocuments.Xlsx(Occupancy())),
        "bookings-by-channel" => (HostReportDocuments.Csv(Channels()), HostReportDocuments.Xlsx(Channels())),
        "performance-total" => (HostReportDocuments.Csv(Performance(false, HostPerformanceGroupBy.None), HostPerformanceGroupBy.None), HostReportDocuments.Xlsx(Performance(false, HostPerformanceGroupBy.None), HostPerformanceGroupBy.None)),
        "performance-by-property-compared" => (HostReportDocuments.Csv(Performance(true, HostPerformanceGroupBy.Property), HostPerformanceGroupBy.Property), HostReportDocuments.Xlsx(Performance(true, HostPerformanceGroupBy.Property), HostPerformanceGroupBy.Property)),
        "performance-by-channel" => (HostReportDocuments.Csv(Performance(false, HostPerformanceGroupBy.Channel), HostPerformanceGroupBy.Channel), HostReportDocuments.Xlsx(Performance(false, HostPerformanceGroupBy.Channel), HostPerformanceGroupBy.Channel)),
        "trend" => (HostReportDocuments.Csv(Trend(false), HostPerformanceGroupBy.None), HostReportDocuments.Xlsx(Trend(false), HostPerformanceGroupBy.None)),
        "trend-compared" => (HostReportDocuments.Csv(Trend(true), HostPerformanceGroupBy.None), HostReportDocuments.Xlsx(Trend(true), HostPerformanceGroupBy.None)),
        "collections" => (HostCollectionsReportDocuments.Csv(Collections()), HostCollectionsReportDocuments.Xlsx(Collections())),
        "balances-to-collect" => (HostCollectionsReportDocuments.Csv(Balances()), HostCollectionsReportDocuments.Xlsx(Balances())),
        "channel-costs" => (ChannelCostsReportDocuments.Csv(ChannelCosts()), ChannelCostsReportDocuments.Xlsx(ChannelCosts())),
        "by-owner" => (OwnerReportDocuments.Csv(Owners()), OwnerReportDocuments.Xlsx(Owners())),
        "tourist-tax" => (TouristTaxSummaryDocuments.Csv(TouristTax(false), Texts), TouristTaxSummaryDocuments.Xlsx(TouristTax(false), Texts)),
        "tourist-tax-owed-only" => (TouristTaxSummaryDocuments.Csv(TouristTax(true), Texts), TouristTaxSummaryDocuments.Xlsx(TouristTax(true), Texts)),
        _ => throw new ArgumentOutOfRangeException(nameof(report), report, null),
    };

    // ---------------------------------------------------------------- reading the two files back

    /// <summary>The sheets of a workbook as typed cells: the kind of a number cell is the one its format says (a count, an amount, a percentage, a day).</summary>
    private static List<(string Name, List<List<XCell?>> Rows)> ReadSheets(byte[] workbook)
    {
        Assert.True(XlsxTableReader.LooksLikeWorkbook(workbook));
        using var zip = new ZipArchive(new MemoryStream(workbook, writable: false), ZipArchiveMode.Read);

        XDocument Load(string path)
        {
            using var stream = zip.GetEntry(path)!.Open();
            return XDocument.Load(stream);
        }

        var styles = Load("xl/styles.xml");
        var formats = styles.Descendants(Main + "numFmt").ToDictionary(f => (string)f.Attribute("numFmtId")!, f => (string)f.Attribute("formatCode")!);
        var cellFormats = styles.Descendants(Main + "cellXfs").Single().Elements(Main + "xf").Select(xf => (string)xf.Attribute("numFmtId")!).ToList();
        var targets = Load("xl/_rels/workbook.xml.rels").Descendants(Rels + "Relationship").ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        var sheets = new List<(string, List<List<XCell?>>)>();
        foreach (var sheet in Load("xl/workbook.xml").Descendants(Main + "sheet"))
        {
            var rows = new List<List<XCell?>>();
            foreach (var row in Load("xl/" + targets[(string)sheet.Attribute(Documents + "id")!]).Descendants(Main + "row"))
            {
                var cells = new List<XCell?>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = XlsxTableReader.ColumnIndex((string)cell.Attribute("r")!)!.Value;
                    while (cells.Count <= column)
                        cells.Add(null);

                    if ((string?)cell.Attribute("t") == "inlineStr")
                    {
                        cells[column] = new XCell("text", string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)));
                        continue;
                    }

                    var style = (string?)cell.Attribute("s");
                    var formatId = style is null ? "0" : cellFormats[int.Parse(style, CultureInfo.InvariantCulture)];
                    var code = formatId == "0" ? "General" : formats[formatId];
                    var kind = code switch
                    {
                        "General" => "integer",
                        "#,##0.00" => "money",
                        "0.00%" => "percent",
                        "dd/mm/yyyy" => "date",
                        _ => "other:" + code,
                    };
                    cells[column] = new XCell(kind, cell.Element(Main + "v")!.Value);
                }

                rows.Add(cells);
            }

            sheets.Add(((string)sheet.Attribute("name")!, rows));
        }

        return sheets;
    }

    /// <summary>The tables of a CSV: the rows of each, split into cells; an empty line separates two tables.</summary>
    private static List<List<string[]>> CsvTables(byte[] csv)
    {
        var lines = Encoding.UTF8.GetString(csv).TrimStart('﻿').Split("\r\n");
        var tables = new List<List<string[]>> { new() };
        foreach (var line in lines.Take(lines.Length - 1))
        {
            if (line.Length == 0)
                tables.Add([]);
            else
                tables[^1].Add(line.Split(';'));
        }

        return tables;
    }

    // ---------------------------------------------------------------- the proof

    /// <summary>
    /// The money rule of the exports: for every report, the Excel file has the same sheets as the CSV has tables, the same title row, the same header and the
    /// same cells; an amount is the same decimal to the cent (two places in both files), a percentage the same number (a fraction in the Excel file, the
    /// percentage in the CSV), a day the same day, a count the same count, a text the same text; and a CSV cell that is a number is a number cell in Excel.
    /// </summary>
    [Theory]
    [InlineData("revenue-by-property")]
    [InlineData("occupancy-by-property")]
    [InlineData("bookings-by-channel")]
    [InlineData("performance-total")]
    [InlineData("performance-by-property-compared")]
    [InlineData("performance-by-channel")]
    [InlineData("trend")]
    [InlineData("trend-compared")]
    [InlineData("collections")]
    [InlineData("balances-to-collect")]
    [InlineData("channel-costs")]
    [InlineData("by-owner")]
    [InlineData("tourist-tax")]
    [InlineData("tourist-tax-owed-only")]
    public void ReportExports_ExcelAndCsvCarryTheSameNumbers_Test(string report)
    {
        var (csv, xlsx) = Files(report);

        var tables = CsvTables(csv);
        var sheets = ReadSheets(xlsx);

        Assert.Equal(tables.Count, sheets.Count);
        for (var t = 0; t < tables.Count; t++)
        {
            var where = $"{report}, table {t} ({sheets[t].Name})";
            var csvRows = tables[t];
            var xlsxRows = sheets[t].Rows;
            Assert.True(csvRows.Count >= 2, where);
            Assert.Equal(csvRows.Count, xlsxRows.Count);

            // The title row and the header are the same text.
            Assert.Equal(csvRows[0][0], Assert.Single(xlsxRows[0])!.Text);
            Assert.Equal(csvRows[1], xlsxRows[1].Select(cell => cell!.Text));

            for (var r = 2; r < csvRows.Count; r++)
            {
                var csvCells = csvRows[r];
                var xlsxCells = xlsxRows[r];
                for (var c = 0; c < Math.Max(csvCells.Length, xlsxCells.Count); c++)
                {
                    var csvText = c < csvCells.Length ? csvCells[c] : string.Empty;
                    var xlsxCell = c < xlsxCells.Count ? xlsxCells[c] : null;
                    AssertSameCell($"{where}, row {r}, column {c}", csvText, xlsxCell);
                }
            }
        }
    }

    private static void AssertSameCell(string where, string csvText, XCell? cell)
    {
        if (cell is null)
        {
            Assert.True(csvText.Length == 0, $"{where}: the CSV has «{csvText}» where the Excel file has nothing");
            return;
        }

        switch (cell.Kind)
        {
            case "text":
                var expected = CsvWriter.StartsLikeAFormula(cell.Text) ? "'" + cell.Text : cell.Text;
                Assert.True(expected == csvText, $"{where}: «{expected}» is not «{csvText}»");
                Assert.False(Regex.IsMatch(csvText, @"^-?\d+(,\d+)?$"), $"{where}: the number {csvText} is a text in the Excel file");
                break;
            case "integer":
                Assert.Matches(@"^-?\d+$", csvText);
                Assert.Matches(@"^-?\d+$", cell.Text);
                Assert.True(decimal.Parse(csvText, CultureInfo.InvariantCulture) == decimal.Parse(cell.Text, CultureInfo.InvariantCulture), where);
                break;
            case "money":
                Assert.Matches(@"^-?\d+,\d{2}$", csvText);
                Assert.Matches(@"^-?\d+\.\d{2}$", cell.Text);
                Assert.True(decimal.Parse(csvText, Italian) == decimal.Parse(cell.Text, CultureInfo.InvariantCulture), $"{where}: {csvText} is not {cell.Text} to the cent");
                break;
            case "percent":
                Assert.Matches(@"^-?\d+,\d{2}$", csvText);
                Assert.Matches(@"^-?\d+(\.\d{1,4})?$", cell.Text);
                Assert.True(decimal.Parse(csvText, Italian) == decimal.Parse(cell.Text, CultureInfo.InvariantCulture) * 100m, $"{where}: {csvText} is not {cell.Text} as a percentage");
                break;
            case "date":
                var day = DateOnly.FromDayNumber(new DateOnly(1899, 12, 30).DayNumber + int.Parse(cell.Text, CultureInfo.InvariantCulture));
                Assert.Equal(day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), csvText);
                break;
            default:
                Assert.Fail($"{where}: a cell of kind {cell.Kind}");
                break;
        }
    }

    // ---------------------------------------------------------------- what the proof leans on

    // Every cell of every sheet as «kind:text», one line for each row.
    private static List<string> Flatten(byte[] workbook) =>
        ReadSheets(workbook).SelectMany(sheet => sheet.Rows).Select(row => string.Join('|', row.Select(cell => cell is null ? string.Empty : cell.Kind + ":" + cell.Text))).ToList();

    /// <summary>The numbers of a file are never text, whatever the culture of the server that wrote it.</summary>
    [Fact]
    public void ReportExports_AreTheSame_WhateverTheCultureOfTheServer()
    {
        var expected = Files("collections");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        try
        {
            var other = Files("collections");

            Assert.Equal(expected.Csv, other.Csv);
            Assert.Equal(Flatten(expected.Xlsx), Flatten(other.Xlsx));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Euro and Swiss francs stay in their own rows, each with its own total: no cell of either file is the sum of the two currencies (299.50 + 120.00), and the
    /// total rows carry the currency they are in.
    /// </summary>
    [Fact]
    public void Balances_AmountsOfDifferentCurrenciesAreNeverAddedTogether_InTheCsvOrTheExcelFile()
    {
        var (csv, xlsx) = Files("balances-to-collect");

        var rows = ReadSheets(xlsx).Single().Rows;
        var totals = rows.Where(row => row[0]?.Text == "Totale").ToList();

        Assert.Equal(2, totals.Count);
        Assert.Equal(["EUR", "CHF"], totals.Select(row => row[5]!.Text));
        Assert.Equal(["299.50", "120.00"], totals.Select(row => row[8]!.Text));
        var everyCell = rows.SelectMany(row => row).Where(cell => cell is not null).Select(cell => cell!.Text).ToList();
        Assert.DoesNotContain("419.50", everyCell);
        Assert.DoesNotContain("419,50", Encoding.UTF8.GetString(csv), StringComparison.Ordinal);
        Assert.Contains("Totale;;;;;EUR;;;299,50;;", Encoding.UTF8.GetString(csv), StringComparison.Ordinal);
        Assert.Contains("Totale;;;;;CHF;;;120,00;;", Encoding.UTF8.GetString(csv), StringComparison.Ordinal);
    }

    /// <summary>
    /// A payment of the tourist tax belongs to the org and to no property: for a caller who reaches only some of the properties there is no payment, no paid
    /// amount, no balance and no status in the CSV or in the Excel file, and the title says the payments are not shown.
    /// </summary>
    [Fact]
    public void TouristTax_ACallerWhoReachesSomeOfThePropertiesGetsNoPayment_InTheCsvOrTheExcelFile()
    {
        var (csv, xlsx) = Files("tourist-tax-owed-only");

        var sheet = Assert.Single(ReadSheets(xlsx));
        Assert.Equal(TouristTaxSummaryDocuments.ComuniSheet, sheet.Name);
        Assert.Contains("versamenti non mostrati", sheet.Rows[0][0]!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sheet.Rows[1], cell => cell!.Text.Contains("versata", StringComparison.OrdinalIgnoreCase) || cell.Text is "Saldo (€)" or "Stato");

        var text = Encoding.UTF8.GetString(csv);
        Assert.DoesNotContain("Versamenti registrati", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bonifico 123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Imposta versata", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReadSheets(xlsx).SelectMany(s => s.Rows).SelectMany(row => row), cell => cell?.Text.Contains("Bonifico 123", StringComparison.Ordinal) == true);

        // The same summary with its payments has the second sheet, with the payment of Roma.
        var shown = ReadSheets(TouristTaxSummaryDocuments.Xlsx(TouristTax(false), Texts));
        Assert.Equal([TouristTaxSummaryDocuments.ComuniSheet, TouristTaxSummaryDocuments.RemittancesSheet], shown.Select(s => s.Name));
        Assert.Equal("Bonifico 123", shown[1].Rows[2][5]!.Text);
        Assert.Equal("100.00", shown[1].Rows[2][3]!.Text);
    }

    /// <summary>The names of the guests, the owners and the payment notes are typed by people: a text that starts like a formula is written as text in both files.</summary>
    [Fact]
    public void ReportExports_ATextThatStartsLikeAFormula_IsNeverAFormula_InTheCsvOrTheExcelFile()
    {
        var (csv, xlsx) = Files("revenue-by-property");

        Assert.Contains("'=SUM(A1) Villa;0,07;1;1", Encoding.UTF8.GetString(csv), StringComparison.Ordinal);
        using var zip = new ZipArchive(new MemoryStream(xlsx, writable: false), ZipArchiveMode.Read);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(stream);
        Assert.Empty(sheet.Descendants(Main + "f"));
        var villa = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "A4");
        Assert.Equal("inlineStr", (string?)villa.Attribute("t"));
        Assert.Equal("=SUM(A1) Villa", villa.Descendants(Main + "t").Single().Value);
    }
}
