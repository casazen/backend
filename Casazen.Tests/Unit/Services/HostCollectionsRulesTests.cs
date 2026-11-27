using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-97: the rules of the collections report, with no database. The buckets and where a payment falls (the Europe/Rome day), what is a share and what is not,
/// the credits and what makes a balance; the expressions a query runs hold the same definitions as the code the rest of the product uses (never a copy that can
/// drift); and the file the host downloads carries each table under its own title.
/// </summary>
public class HostCollectionsRulesTests
{
    private static HostDashboardPeriod Period(DateOnly first, DateOnly last) => HostReportRules.PeriodOf(first, last);

    private static HostCollectedPayment Collected(DateTime at, decimal amount, PaymentMethod method = PaymentMethod.CreditCard, BookingSource source = BookingSource.Direct, decimal refunded = 0m) =>
        new(at, amount, refunded, method, source);

    // ---------------------------------------------------------------- the buckets

    [Theory]
    [InlineData(1, HostCollectionsBucketUnit.Day)]
    [InlineData(31, HostCollectionsBucketUnit.Day)]
    [InlineData(92, HostCollectionsBucketUnit.Day)]
    [InlineData(93, HostCollectionsBucketUnit.Month)]
    [InlineData(365, HostCollectionsBucketUnit.Month)]
    public void Buckets_AreByDayUpToNinetyTwoDays_AndByMonthBeyond(int days, HostCollectionsBucketUnit expected)
    {
        var first = new DateOnly(2026, 1, 1);
        var period = Period(first, first.AddDays(days - 1));

        Assert.Equal(expected, HostCollectionsRules.UnitFor(period));
        var buckets = HostCollectionsRules.EmptyBucketsOf(period);
        Assert.All(buckets, b => Assert.Equal(expected, b.Unit));
        if (expected == HostCollectionsBucketUnit.Day)
            Assert.Equal(days, buckets.Count);
    }

    [Fact]
    public void Buckets_ByMonth_AreCutAtTheEdgesOfThePeriod_AndCoverItExactly()
    {
        var period = Period(new DateOnly(2026, 1, 20), new DateOnly(2026, 6, 10));

        var buckets = HostCollectionsRules.EmptyBucketsOf(period);

        Assert.Equal(new[] { "2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06" }, buckets.Select(b => b.Label));
        Assert.Equal((new DateOnly(2026, 1, 20), new DateOnly(2026, 1, 31)), (buckets[0].From, buckets[0].To));
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), (buckets[1].From, buckets[1].To));
        Assert.Equal((new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 10)), (buckets[^1].From, buckets[^1].To));
        for (var index = 1; index < buckets.Count; index++)
            Assert.Equal(buckets[index - 1].To.AddDays(1), buckets[index].From);
    }

    [Fact]
    public void Summarize_PutsEachPaymentOnItsRomeDay_NotOnTheUtcOne()
    {
        // 22:30 UTC on 8 October is 00:30 on the 9th in Rome (summer time); on 28 March (winter time, the change is the 29th) 22:30 UTC is still the 28th and 23:30 UTC is the 29th.
        var october = HostCollectionsRules.Summarize(
            Period(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 10)),
            [Collected(new DateTime(2026, 10, 8, 22, 30, 0, DateTimeKind.Utc), 100m), Collected(new DateTime(2026, 10, 8, 21, 30, 0, DateTimeKind.Utc), 7m)]);
        var march = HostCollectionsRules.Summarize(
            Period(new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 29)),
            [Collected(new DateTime(2026, 3, 28, 22, 30, 0, DateTimeKind.Utc), 40m), Collected(new DateTime(2026, 3, 28, 23, 30, 0, DateTimeKind.Utc), 5m)]);

        Assert.Equal(new[] { 7m, 100m, 0m }, october.ByDate.Select(b => b.Amount));
        Assert.Equal(new[] { 40m, 5m }, march.ByDate.Select(b => b.Amount));
        Assert.Equal(new HostDashboardCollected(107m, 2), october.Total);
    }

    [Fact]
    public void Summarize_APaymentOutsideThePeriod_IsAnErrorOfTheCaller_NeverMoneyDroppedInSilence()
    {
        var period = Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => HostCollectionsRules.Summarize(period, [Collected(new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), 10m)]));

        Assert.Contains("2026-10-20", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_SharesAreNullWithoutATotal_AndAreRoundedToFourDecimals()
    {
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var nothing = HostCollectionsRules.Summarize(
            Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3)), [Collected(at, 50m, refunded: 50m)]);
        var some = HostCollectionsRules.Summarize(
            Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3)),
            [Collected(at, 100m), Collected(at, 100m, PaymentMethod.BankTransfer), Collected(at, 100m, PaymentMethod.CashOnArrival)]);

        Assert.Equal(new HostDashboardCollected(0m, 1), nothing.Total);
        Assert.All(nothing.ByMethod, row => Assert.Null(row.Share));
        Assert.All(nothing.ByChannel, row => Assert.Null(row.Share));
        Assert.All(some.ByMethod, row => Assert.Equal(0.3333m, row.Share));
    }

    // ---------------------------------------------------------------- credits and balances

    [Fact]
    public void CreditStatus_IsCreditedFromTheDayItArrives_AndExpectedBefore()
    {
        var today = new DateOnly(2026, 10, 9);

        Assert.Equal(HostCreditStatus.Credited, HostCollectionsRules.CreditStatusOf(today.AddDays(-1), today));
        Assert.Equal(HostCreditStatus.Credited, HostCollectionsRules.CreditStatusOf(today, today));
        Assert.Equal(HostCreditStatus.Expected, HostCollectionsRules.CreditStatusOf(today.AddDays(1), today));
    }

    [Fact]
    public void StripeCredits_WithNothingStored_AreNotAvailable_AndKeepTheCountsThatSayWhy()
    {
        var credits = HostCollectionsRules.StripeCreditsOf([], new DateOnly(2026, 10, 9), 4, 4);

        Assert.False(credits.Available);
        Assert.Empty(credits.Payouts);
        Assert.Equal((4, 4), (credits.CardPaymentCount, credits.PaymentsWithoutFigures));
    }

    [Fact]
    public void ChannelCredits_WithoutAnyReportedNet_AreNotAvailable_AndTheStaysAreOnlyCounted()
    {
        var credits = HostCollectionsRules.ChannelCreditsOf(
            [new HostChannelCreditStay(BookingSource.Airbnb, "EUR", null), new HostChannelCreditStay(BookingSource.Airbnb, null, null)]);

        Assert.False(credits.Available);
        Assert.Empty(credits.Rows);
        Assert.Equal(2, credits.StaysWithoutNet);
    }

    [Fact]
    public void Overdue_IsAStayWhoseCheckOutDayIsBeforeToday()
    {
        var today = new DateOnly(2026, 10, 9);

        Assert.True(HostCollectionsRules.IsOverdue(today.AddDays(-1), today));
        Assert.False(HostCollectionsRules.IsOverdue(today, today));
        Assert.False(HostCollectionsRules.IsOverdue(today.AddDays(3), today));
    }

    [Theory]
    [InlineData(500, 260, 240)]
    [InlineData(500, 500, 0)]
    [InlineData(500, 620, 0)]
    [InlineData(0, 0, 0)]
    public void ToCollect_IsTheTotalMinusWhatWasCollected_NeverBelowZero(decimal total, decimal collected, decimal expected)
    {
        Assert.Equal(expected, HostCollectionsRules.ToCollect(total, collected));
    }

    [Fact]
    public void Totals_AreOneForEachCurrency_NeverTheSumOfTwoCurrencies()
    {
        HostBalanceRow Row(string currency, decimal toCollect) => new(
            Guid.NewGuid(), "ABCDE12345", Guid.NewGuid(), "Casa", BookingSource.Direct, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14), currency, null, null, toCollect, false,
            HostBalanceBasis.Payments);

        var totals = HostCollectionsRules.TotalsOf([Row("EUR", 100.10m), Row("CHF", 40m), Row("EUR", 20.20m)]);

        Assert.Equal(new[] { ("CHF", 40m, 1), ("EUR", 120.30m, 2) }, totals.Select(t => (t.Currency, t.Amount, t.StayCount)).ToArray());
    }

    [Theory]
    [InlineData("2026-10-09", "2024-10-09")]
    [InlineData("2028-02-29", "2026-02-28")]
    public void BalancesSince_IsTwentyFourMonthsBack_LikeTheLongestReport(string today, string expected)
    {
        var since = HostCollectionsRules.BalancesSince(DateOnly.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), since);
        Assert.Equal(HostReportRules.MaxMonths, HostCollectionsRules.BalancesMonths);
    }

    [Fact]
    public void Sums_AddedOneAtATime_AreTheFiguresOfTheWholeList_AndNeedNoList()
    {
        var period = Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3));
        HostCollectedPayment[] payments =
        [
            Collected(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), 100m),
            Collected(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), 60.50m, PaymentMethod.CashOnArrival, BookingSource.Manual),
            Collected(new DateTime(2026, 10, 2, 11, 0, 0, DateTimeKind.Utc), 100m, refunded: 30m),
        ];

        var sums = new HostCollectionsSums(period);
        foreach (var payment in payments)
            sums.Add(payment);
        var figures = sums.Figures();

        // The sums say what the list says, and each cut adds up to the total.
        Assert.Equal(HostCollectionsRules.Summarize(period, payments).Total, figures.Total);
        Assert.Equal(new HostDashboardCollected(230.50m, 3), figures.Total);
        Assert.Equal(new[] { 100m, 130.50m, 0m }, figures.ByDate.Select(b => b.Amount));
        Assert.Equal(new[] { (PaymentMethod.CreditCard, 170m, 2), (PaymentMethod.CashOnArrival, 60.50m, 1) }, figures.ByMethod.Select(m => (m.Method, m.Amount, m.PaymentCount)));
        Assert.Equal(figures.Total.Amount, figures.ByChannel.Sum(c => c.Amount));
    }

    // ---------------------------------------------------------------- one definition, however it is asked

    private static Booking BookingOf(BookingSource source, string? reservation) => new() { Source = source, OctorateReservationId = reservation };

    public static TheoryData<BookingSource, string?, bool> EveryKindOfBooking()
    {
        var data = new TheoryData<BookingSource, string?, bool>();
        foreach (var source in Enum.GetValues<BookingSource>())
        {
            foreach (var reservation in new string?[] { null, string.Empty, "  ", "RES-1" })
            {
                foreach (var pushed in new[] { false, true })
                    data.Add(source, reservation, pushed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKindOfBooking))]
    public void TheMoneyOfABooking_AsAQueryAsksIt_IsTheOneDefinitionOfTheProduct(BookingSource source, string? reservation, bool pushed)
    {
        var booking = BookingOf(source, reservation);
        IQueryable<OctorateBookingSync> mirrors = pushed
            ? new[] { new OctorateBookingSync { BookingId = booking.Id } }.AsQueryable()
            : Array.Empty<OctorateBookingSync>().AsQueryable();

        var channelManagers = HostCollectionsRules.HoldsChannelManagerMoney(mirrors).Compile()(booking);
        var casazens = HostCollectionsRules.HoldsCasaZenMoney(mirrors).Compile()(booking);

        // The expression is BookingAmountSourceRules.HoldsChannelManagerMoney, and CasaZen's money is what is neither a channel's nor the channel manager's.
        Assert.Equal(BookingAmountSourceRules.HoldsChannelManagerMoney(booking, pushed), channelManagers);
        Assert.Equal(!FiscalCopy.IsOtaBookingSource(source) && !BookingAmountSourceRules.HoldsChannelManagerMoney(booking, pushed), casazens);
        Assert.False(channelManagers && casazens);
    }

    [Fact]
    public void TheChannels_AreDerivedFromTheOneDefinition_NotWrittenOutAgain()
    {
        Assert.Equal(
            Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).Order(),
            HostCollectionsRules.ChannelSources.Order());
        Assert.DoesNotContain(BookingSource.Direct, HostCollectionsRules.ChannelSources);
        Assert.DoesNotContain(BookingSource.Manual, HostCollectionsRules.ChannelSources);
        Assert.DoesNotContain(BookingSource.Local, HostCollectionsRules.ChannelSources);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Processing)]
    [InlineData(PaymentStatus.Completed)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.Canceled)]
    public void CollectedPayments_ByStatusAlone_AreTheOnesTheHomeCountsWhateverTheDay(PaymentStatus status)
    {
        var payment = new Payment { Status = status, ProcessedAt = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc) };

        var byStatus = PaymentCashRules.IsCollected().Compile()(payment);
        var byStatusAndDay = PaymentCashRules.CollectedBetweenInstants(DateTime.MinValue, DateTime.MaxValue).Compile()(payment);

        Assert.Equal(byStatusAndDay, byStatus);
    }

    [Theory]
    [InlineData(500, 0)]
    [InlineData(500, 120)]
    [InlineData(500, 500)]
    [InlineData(100, 130)]
    public void CollectedAmount_AsAQuerySumsIt_IsWhatACollectedPaymentBrings(decimal amount, decimal refunded)
    {
        var payment = new Payment { Amount = amount, RefundedAmount = refunded };

        Assert.Equal(PaymentCashRules.Collected(amount, refunded), PaymentCashRules.CollectedAmount().Compile()(payment));
    }

    // ---------------------------------------------------------------- the files

    private static string[] Lines(byte[] file) =>
        Encoding.UTF8.GetString(file).TrimStart('\uFEFF').Split("\r\n").SkipLast(1).ToArray();

    [Fact]
    public void Csv_OfTheCollections_HasTheThreeCutsEachUnderItsOwnTitleRowWithTheDays()
    {
        var period = Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3));
        var figures = HostCollectionsRules.Summarize(
            period,
            [
                Collected(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), 100m),
                Collected(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc), 60.50m, PaymentMethod.CashOnArrival, BookingSource.Manual),
            ]);
        var report = new HostCollectionsReport(
            period, figures.Total, figures.ByDate, figures.ByMethod, figures.ByChannel,
            new HostCredits(new HostStripeCredits(false, [], 0, 0), new HostChannelCredits(false, [], 0)));

        var lines = Lines(HostCollectionsReportDocuments.Csv(report));

        Assert.Equal(
            new[]
            {
                "Incassato per data, per cassa, dal 01/10/2026 al 03/10/2026",
                "Dal;Al;Incassato (€);Pagamenti",
                "01/10/2026;01/10/2026;0,00;0",
                "02/10/2026;02/10/2026;100,00;1",
                "03/10/2026;03/10/2026;60,50;1",
                "Totale;;160,50;2",
                string.Empty,
                "Incassato per metodo di pagamento, per cassa, dal 01/10/2026 al 03/10/2026",
                "Metodo di pagamento;Incassato (€);Pagamenti;Quota (%)",
                "Carta;100,00;1;62,31",
                "Contanti all'arrivo;60,50;1;37,69",
                "Totale;160,50;2;100,00",
                string.Empty,
                "Incassato per canale, per cassa, dal 01/10/2026 al 03/10/2026",
                "Canale;Incassato (€);Pagamenti;Quota (%)",
                "Sito di prenotazione;100,00;1;62,31",
                "Inserita dall'host;60,50;1;37,69",
                "Totale;160,50;2;100,00",
            },
            lines);
        Assert.Equal("casazen-incassato-per-cassa-20261001-20261003.csv", HostReportDocuments.FileName("incassato-per-cassa", period));
    }

    [Fact]
    public void Csv_OfTheBalances_LeavesAFigureTheChannelManagerDidNotGiveEmpty_NeverZero()
    {
        var report = new HostBalancesToCollect(
            new DateOnly(2026, 10, 9),
            [
                new HostBalanceRow(
                    Guid.NewGuid(), "ABCDE12345", Guid.NewGuid(), "Trullo", BookingSource.Direct, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15), "EUR", 500m, 260m, 240m,
                    false, HostBalanceBasis.Payments),
                new HostBalanceRow(
                    Guid.NewGuid(), "FGHJK67890", Guid.NewGuid(), "=Casa Bianca", BookingSource.BookingCom, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 4), "EUR", null, null, 45m,
                    true, HostBalanceBasis.ChannelManager),
            ],
            [new HostBalanceTotal("EUR", 285m, 2)]);

        var lines = Lines(HostCollectionsReportDocuments.Csv(report));

        Assert.Equal(
            new[]
            {
                "Saldi da incassare, al 09/10/2026",
                "Codice prenotazione;Immobile;Canale;Check-in;Check-out;Valuta;Totale;Incassato;Da incassare;Scaduto;Importi indicati da",
                "ABCDE-12345;Trullo;Sito di prenotazione;12/10/2026;15/10/2026;EUR;500,00;260,00;240,00;No;CasaZen",
                // The name of a property that a spreadsheet would run as a formula is written as text.
                "FGHJK-67890;'=Casa Bianca;Booking.com;01/10/2026;04/10/2026;EUR;;;45,00;Sì;Channel manager",
                "Totale;;;;;EUR;;;285,00;;",
            },
            lines);
        Assert.Equal("casazen-saldi-da-incassare-20261009.csv", HostCollectionsReportDocuments.BalancesFileName(report.AsOf));
    }
}
