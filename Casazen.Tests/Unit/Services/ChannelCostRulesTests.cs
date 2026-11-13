using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-98: the rules of the channel costs report, with no database. What kind of cost each channel has, how the two flags of a portal become a mode (only a flag that is
/// true counts, and two that disagree claim nothing), the order of the rows and the one that is the most expensive (never at zero), the rounding of a stay cut across
/// two periods, and the file the host downloads.
/// </summary>
public class ChannelCostRulesTests
{
    private static readonly HostDashboardPeriod October = HostReportRules.PeriodOf(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
    private static readonly IReadOnlyDictionary<BookingSource, ChannelCommissionMode> NoModes = new Dictionary<BookingSource, ChannelCommissionMode>();

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static ChannelCostStay Stay(BookingSource source, int checkIn, int checkOut, decimal price, decimal? cost, decimal? net = null) =>
        new(source, price, Day(10, checkIn), Day(10, checkOut), cost, net);

    [Theory]
    [InlineData(true, null, ChannelCommissionMode.DeductedFromPayout)]
    [InlineData(true, false, ChannelCommissionMode.DeductedFromPayout)]
    [InlineData(false, true, ChannelCommissionMode.InvoicedSeparately)]
    [InlineData(true, true, ChannelCommissionMode.Unknown)]
    [InlineData(false, false, ChannelCommissionMode.Unknown)]
    [InlineData(false, null, ChannelCommissionMode.Unknown)]
    public void Mode_OfAPortal_IsOnlyWhatATrueFlagSays_AndNothingWhenTheyDisagreeOrAreSilent(bool includedInPayments, bool? invoiced, ChannelCommissionMode expected)
    {
        Assert.Equal(expected, ChannelCostRules.ModeOf(includedInPayments, invoiced));
    }

    [Fact]
    public void Kind_OfACost_IsTheChannelsCommission_ThePaymentFeesOfTheBookingSite_OrNothing()
    {
        foreach (var source in Enum.GetValues<BookingSource>())
        {
            var expected = FiscalCopy.IsOtaBookingSource(source)
                ? ChannelCostKind.ChannelCommission
                : source == BookingSource.Direct ? ChannelCostKind.PaymentFee : ChannelCostKind.NoCost;
            Assert.Equal(expected, ChannelCostRules.KindOf(source));
        }

        Assert.Equal(ChannelCostKind.ChannelCommission, ChannelCostRules.KindOf(BookingSource.OtherChannel));
        Assert.Equal(ChannelCostKind.NoCost, ChannelCostRules.KindOf(BookingSource.Manual));
        Assert.Equal(ChannelCostKind.NoCost, ChannelCostRules.KindOf(BookingSource.Local));
    }

    [Fact]
    public void Report_WithoutStays_IsEmpty_WithNothingToShowAsACost()
    {
        var report = ChannelCostRules.Report(October, [], NoModes);

        Assert.Empty(report.Rows);
        Assert.Equal((0, 0m, 0), (report.Total.StayCount, report.Total.Gross, report.Total.StaysWithoutCommission));
        Assert.Null(report.Total.Commission);
        Assert.Null(report.Total.Net);
        Assert.Null(report.Total.CostRate);
    }

    [Fact]
    public void Report_NobodyIsTheMostExpensive_WhenEveryRateIsZeroOrUnknown()
    {
        var report = ChannelCostRules.Report(
            October,
            [
                Stay(BookingSource.Manual, 1, 3, 200m, 0m),
                Stay(BookingSource.Local, 5, 7, 100m, 0m),
                Stay(BookingSource.Expedia, 10, 12, 100m, null),
            ],
            NoModes);

        Assert.DoesNotContain(report.Rows, r => r.MostExpensive);
        // The ones with a rate come first (zero is a rate), then the one without; the larger gross first among equals.
        Assert.Equal(new[] { BookingSource.Manual, BookingSource.Local, BookingSource.Expedia }, report.Rows.Select(r => r.Source!.Value));
    }

    [Fact]
    public void Report_TheRowsAreByRate_TheLargerGrossBreaksATie_AndOnlyTheFirstIsTheMostExpensive()
    {
        var report = ChannelCostRules.Report(
            October,
            [
                Stay(BookingSource.Airbnb, 1, 3, 100m, 10m),
                Stay(BookingSource.BookingCom, 5, 7, 300m, 30m),
                Stay(BookingSource.Expedia, 9, 11, 200m, 40m),
            ],
            NoModes);

        Assert.Equal(new[] { BookingSource.Expedia, BookingSource.BookingCom, BookingSource.Airbnb }, report.Rows.Select(r => r.Source!.Value));
        Assert.Equal(new[] { true, false, false }, report.Rows.Select(r => r.MostExpensive));
        Assert.Equal(new[] { 0.2m, 0.1m, 0.1m }, report.Rows.Select(r => r.CostRate!.Value));
    }

    [Fact]
    public void Report_AStayCutAcrossTwoPeriods_IsRoundedOnTheTotal_NotOnEachShare()
    {
        // 31 October to 3 November: three nights, one of them in October. A hundred over three nights is 33,333...; ten of commission is 3,333...
        var stay = new ChannelCostStay(BookingSource.Airbnb, 100m, Day(10, 31), Day(11, 3), 10m, null);

        var report = ChannelCostRules.Report(October, [stay], NoModes);

        var row = Assert.Single(report.Rows);
        Assert.Equal((33.33m, 3.33m, 30.00m, 0.1m), (row.Gross, row.Commission!.Value, row.Net!.Value, row.CostRate!.Value));
        Assert.Equal(row.Gross, report.Total.Gross);
    }

    [Fact]
    public void Report_TheModeIsOnlyForAChannel_AndUnknownWhenTheSettingsDoNotSayIt()
    {
        var report = ChannelCostRules.Report(
            October,
            [
                Stay(BookingSource.Airbnb, 1, 3, 100m, 10m),
                Stay(BookingSource.Expedia, 5, 7, 100m, 9m),
                Stay(BookingSource.Direct, 9, 11, 100m, 0m),
                Stay(BookingSource.Manual, 13, 15, 100m, 0m),
            ],
            new Dictionary<BookingSource, ChannelCommissionMode> { [BookingSource.Airbnb] = ChannelCommissionMode.DeductedFromPayout });

        Assert.Equal(ChannelCommissionMode.DeductedFromPayout, report.Rows.Single(r => r.Source == BookingSource.Airbnb).CommissionMode);
        Assert.Equal(ChannelCommissionMode.Unknown, report.Rows.Single(r => r.Source == BookingSource.Expedia).CommissionMode);
        Assert.Null(report.Rows.Single(r => r.Source == BookingSource.Direct).CommissionMode);
        Assert.Null(report.Rows.Single(r => r.Source == BookingSource.Manual).CommissionMode);
        Assert.Null(report.Total.CommissionMode);
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public void Csv_HasTheTitleWithTheDays_AChannelPerRow_AnEmptyCellWhereNothingWasReported_AndTheTotal()
    {
        var report = ChannelCostRules.Report(
            October,
            [
                Stay(BookingSource.BookingCom, 5, 7, 200m, 20m),
                Stay(BookingSource.BookingCom, 10, 12, 200m, null),
                Stay(BookingSource.Expedia, 13, 16, 300m, null),
                Stay(BookingSource.Direct, 20, 22, 100m, 0m),
            ],
            new Dictionary<BookingSource, ChannelCommissionMode> { [BookingSource.BookingCom] = ChannelCommissionMode.InvoicedSeparately });

        var text = Encoding.UTF8.GetString(ChannelCostsReportDocuments.Csv(report)).TrimStart('\uFEFF');
        var lines = text.Split("\r\n").SkipLast(1).ToArray();

        Assert.Equal(
            new[]
            {
                "Costi dei canali, per competenza, dal 01/10/2026 al 31/10/2026",
                "Canale;Tipo di costo;Soggiorni;Lordo per competenza (€);Costo (€);Netto (€);Costo sul lordo (%);Come si paga la commissione;Soggiorni senza costo indicato;Il più costoso",
                "Booking.com;Commissione del canale;2;400,00;20,00;180,00;10,00;Fatturata a parte;1;Sì",
                "Sito di prenotazione;Commissioni sui pagamenti con carta;1;100,00;0,00;100,00;0,00;;0;",
                "Expedia;Commissione del canale;1;300,00;;;;Non nota;1;",
                "Totale;;4;800,00;20,00;280,00;6,67;;2;",
            },
            lines);
        Assert.Equal("casazen-costi-dei-canali-competenza-20261001-20261031.csv", ChannelCostsReportDocuments.FileName(October));
    }
}
