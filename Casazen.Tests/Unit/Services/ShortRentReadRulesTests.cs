using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03: the rules the read API of the short rent area is built on, with no database: the periods of the Home and the one
/// before each, what counts as money collected and on which day, what part of a text can be a booking code, and the order of the
/// things to do.
/// </summary>
public class ShortRentReadRulesTests
{
    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    // --- The periods ------------------------------------------------------------------------------------

    [Fact]
    public void Next30Days_StartsTodayAndTakesThirtyNights()
    {
        var period = HostDashboardPeriod.ForNext30Days(Day(2026, 10, 9));

        Assert.Equal(HostDashboardPeriodKind.Next30Days, period.Kind);
        Assert.Equal(Day(2026, 10, 9), period.From);
        Assert.Equal(Day(2026, 11, 8), period.To);
        Assert.Equal(30, period.Nights);
    }

    [Fact]
    public void Previous_OfNext30Days_IsTheThirtyDaysBeforeToday()
    {
        var previous = HostDashboardPeriod.ForNext30Days(Day(2026, 10, 9)).Previous();

        Assert.Equal(Day(2026, 9, 9), previous.From);
        Assert.Equal(Day(2026, 10, 9), previous.To);
        Assert.Equal(30, previous.Nights);
    }

    [Fact]
    public void Previous_OfLast30Days_IsTheThirtyDaysBeforeIt()
    {
        var last = HostDashboardPeriod.ForLast30Days(Day(2026, 10, 9));

        var previous = last.Previous();

        // The last 30 days are 10 September to 9 October; the thirty before them end on 9 September (10 September excluded).
        Assert.Equal(Day(2026, 9, 10), last.From);
        Assert.Equal(Day(2026, 8, 11), previous.From);
        Assert.Equal(Day(2026, 9, 10), previous.To);
        Assert.Equal(HostDashboardPeriodKind.Last30Days, previous.Kind);
    }

    [Fact]
    public void Previous_OfAMonth_HasTheSameNumberOfDaysAsTheMonth_NotThePreviousCalendarMonth()
    {
        var october = HostDashboardPeriod.ForMonth(new DateOnly(2026, 10, 15));

        var previous = october.Previous();

        // October has 31 days: the 31 days before it start on 31 August, so the two periods weigh the same.
        Assert.Equal(31, october.Nights);
        Assert.Equal(Day(2026, 8, 31), previous.From);
        Assert.Equal(Day(2026, 10, 1), previous.To);
        Assert.Equal(31, previous.Nights);
    }

    [Theory]
    [InlineData("Next30Days")]
    [InlineData("next30days")]
    [InlineData(" NEXT30DAYS ")]
    public void TryParsePeriod_Next30Days_IsAcceptedWhateverTheCase(string period)
    {
        Assert.True(DashboardController.TryParsePeriod(period, null, out var kind, out var month));

        Assert.Equal(HostDashboardPeriodKind.Next30Days, kind);
        Assert.Null(month);
    }

    [Fact]
    public void TryParsePeriod_Next30DaysWithAMonth_IsRefused()
    {
        Assert.False(DashboardController.TryParsePeriod("Next30Days", "2026-10", out _, out _));
    }

    // --- Money collected ------------------------------------------------------------------------------

    private static Payment PaymentOf(PaymentStatus status, DateTime? processedAt, DateTime createdAt, decimal amount = 100m, decimal refunded = 0m)
    {
        return new Payment
        {
            Status = status,
            ProcessedAt = processedAt,
            CreatedAt = createdAt,
            Amount = amount,
            RefundedAmount = refunded,
        };
    }

    [Theory]
    [InlineData(PaymentStatus.Completed, true)]
    [InlineData(PaymentStatus.PartiallyRefunded, true)]
    [InlineData(PaymentStatus.Refunded, false)]
    [InlineData(PaymentStatus.Failed, false)]
    [InlineData(PaymentStatus.Pending, false)]
    [InlineData(PaymentStatus.Processing, false)]
    [InlineData(PaymentStatus.Canceled, false)]
    public void Collected_OnlyAPaymentThatCameInCounts(PaymentStatus status, bool counts)
    {
        var rule = PaymentCashRules.CollectedBetween(Day(2026, 10, 1), Day(2026, 11, 1)).Compile();
        var payment = PaymentOf(status, new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc), Day(2026, 10, 14));

        Assert.Equal(counts, rule(payment));
    }

    [Fact]
    public void Collected_TheDaysAreRomeDays_TheEdgesBelongToTheDayTheyFallOnInRome()
    {
        // 9 October to 7 November (the end day excluded): summer time until 25 October, winter time after.
        var rule = PaymentCashRules.CollectedBetween(Day(2026, 10, 9), Day(2026, 11, 8)).Compile();
        bool Counts(DateTime instant) => rule(PaymentOf(PaymentStatus.Completed, instant, Day(2026, 10, 1)));

        // 22:00Z on the 8th is midnight of the 9th in Rome (UTC+2).
        Assert.False(Counts(new DateTime(2026, 10, 8, 21, 59, 59, DateTimeKind.Utc)));
        Assert.True(Counts(new DateTime(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc)));
        // The 7th of November ends at 23:00Z (UTC+1).
        Assert.True(Counts(new DateTime(2026, 11, 7, 22, 59, 59, DateTimeKind.Utc)));
        Assert.False(Counts(new DateTime(2026, 11, 7, 23, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Collected_APaymentWithoutAnInstantOfSettlementCountsOnTheDayItWasCreated()
    {
        var rule = PaymentCashRules.CollectedBetween(Day(2026, 10, 1), Day(2026, 11, 1)).Compile();

        Assert.True(rule(PaymentOf(PaymentStatus.Completed, null, Day(2026, 10, 20))));
        Assert.False(rule(PaymentOf(PaymentStatus.Completed, null, Day(2026, 9, 20))));
        // The instant of settlement wins over the creation: created in September, settled in October.
        Assert.True(rule(PaymentOf(PaymentStatus.Completed, Day(2026, 10, 2), Day(2026, 9, 28))));
    }

    [Theory]
    [InlineData(300, 0, 300)]
    [InlineData(200, 50, 150)]
    [InlineData(200, 200, 0)]
    [InlineData(100, 150, 0)]
    public void Collected_IsTheAmountMinusWhatWasRefunded_NeverBelowZero(double amount, double refunded, double expected)
    {
        Assert.Equal((decimal)expected, PaymentCashRules.Collected((decimal)amount, (decimal)refunded));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("12.34", 1234)]
    [InlineData("0.10", 10)]
    [InlineData("1234567.89", 123456789)]
    public void ToCents_IsExact(string euros, long cents)
    {
        Assert.Equal(cents, PaymentCashRules.ToCents(decimal.Parse(euros, System.Globalization.CultureInfo.InvariantCulture)));
    }

    // --- Text that can be a booking code ----------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ab", null)]
    [InlineData("a-b", null)]
    [InlineData("abc", "ABC")]
    [InlineData("abc-de fg", "ABCDEFG")]
    [InlineData("ABCDE-FGHJK", "ABCDEFGHJK")]
    [InlineData("oil", "011")]
    [InlineData("rossi", "R0SS1")]
    [InlineData("ABCDEFGHJKM", null)]
    [InlineData("abcu", null)]
    [InlineData("anna@example.com", null)]
    public void CodeFragment_ReadsWhatAPersonTypesLikeABookingCode(string? text, string? expected)
    {
        Assert.Equal(expected, BookingSearchRules.CodeFragment(text));
    }

    // --- The things to do -------------------------------------------------------------------------------

    [Fact]
    public void Priority_EveryActionHasItsOwnPlace_OneToEight()
    {
        var priorities = Enum.GetValues<HostTodoAction>().Select(HostTodoPriority.Of).Order().ToList();

        Assert.Equal(Enumerable.Range(1, 8), priorities);
    }

    [Fact]
    public void Priority_TheOrderIsTheDocumentedOne()
    {
        HostTodoAction[] mostPressingFirst =
        [
            HostTodoAction.RespondToRequest,
            HostTodoAction.ResolveAlloggiatiFailure,
            HostTodoAction.SendAlloggiati,
            HostTodoAction.CompleteGuestCheckIn,
            HostTodoAction.ReviewFailedPayment,
            HostTodoAction.CheckOut,
            HostTodoAction.ConfirmPropertyReady,
            HostTodoAction.ActivateProperty,
        ];

        Assert.Equal(Enumerable.Range(1, 8), mostPressingFirst.Select(HostTodoPriority.Of));
    }

    [Fact]
    public void From_EveryActionOfTheCockpit_HasAPlaceInTheThingsToDo_UnderTheSameName()
    {
        // A new cockpit action without a mapping fails here, not in front of a host.
        foreach (var action in Enum.GetValues<ComplianceCockpitAction>())
            Assert.Equal(action.ToString(), HostTodoPriority.From(action).ToString());
    }
}
