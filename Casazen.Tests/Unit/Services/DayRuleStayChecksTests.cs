using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US01/T03 (NF-36): the one pure check of a stay against the rules of its nights, the one the quote, the checkout and the search of the
/// booking site share. The order of the rules, the minimum of the property as the fallback, the advance read on the calendar of the property.
/// </summary>
public class DayRuleStayChecksTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly DateOnly Arrival = new(2026, 12, 14);

    private static Dictionary<DateOnly, DayRuleValues> Rules(params (DateOnly Night, DayRuleValues Values)[] rules) =>
        rules.ToDictionary(rule => rule.Night, rule => rule.Values);

    private static DayRuleViolation? Check(int nights, int? propertyMin, Dictionary<DateOnly, DayRuleValues> rules, DateOnly? today = null) =>
        DayRuleStayChecks.FirstViolation(Arrival, Arrival.AddDays(nights), today ?? Today, propertyMin, rules);

    [Fact]
    public void NoRulesAtAll_AStayIsJudgedOnTheMinimumOfThePropertyAlone()
    {
        Assert.Null(Check(1, null, new Dictionary<DateOnly, DayRuleValues>()));
        Assert.Null(Check(3, 3, DayRuleStayChecks.NoRules.ToDictionary(pair => pair.Key, pair => pair.Value)));
        Assert.Equal(
            new DayRuleViolation(DayRuleViolationKind.MinNights, Arrival, 4),
            Check(3, 4, new Dictionary<DateOnly, DayRuleValues>()));
    }

    [Fact]
    public void AClosedArrival_IsReadOnTheArrivalNight_AndAClosedDepartureOnTheDayOfTheDeparture()
    {
        var closedArrival = Rules((Arrival, new DayRuleValues(ClosedToArrival: true)));
        var closedDeparture = Rules((Arrival.AddDays(2), new DayRuleValues(ClosedToDeparture: true)));

        Assert.Equal(new DayRuleViolation(DayRuleViolationKind.ArrivalClosed, Arrival), Check(2, null, closedArrival));
        Assert.Equal(new DayRuleViolation(DayRuleViolationKind.DepartureClosed, Arrival.AddDays(2)), Check(2, null, closedDeparture));
        // A night closed to arrival is not closed to departure, and the other way round; a stay that passes through them is fine.
        Assert.Null(Check(2, null, Rules((Arrival.AddDays(2), new DayRuleValues(ClosedToArrival: true)))));
        Assert.Null(Check(2, null, Rules((Arrival, new DayRuleValues(ClosedToDeparture: true)))));
        Assert.Null(Check(3, null, Rules((Arrival.AddDays(1), new DayRuleValues(ClosedToArrival: true, ClosedToDeparture: true)))));
    }

    [Fact]
    public void TheMinimumStayOfTheArrivalNight_ReplacesTheOneOfTheProperty()
    {
        var shorter = Rules((Arrival, new DayRuleValues(MinNights: 1)));
        var longer = Rules((Arrival, new DayRuleValues(MinNights: 5)));

        // The night says one: the minimum of 3 of the property no longer holds. The night says five: it wins over the property's 2.
        Assert.Null(Check(1, 3, shorter));
        Assert.Equal(new DayRuleViolation(DayRuleViolationKind.MinNights, Arrival, 5), Check(4, 2, longer));
        Assert.Null(Check(5, 2, longer));
        // The rule of another night does not count.
        Assert.Equal(
            new DayRuleViolation(DayRuleViolationKind.MinNights, Arrival, 3),
            Check(2, 3, Rules((Arrival.AddDays(1), new DayRuleValues(MinNights: 1)))));
    }

    [Fact]
    public void TheMaximumStayOfTheArrivalNight_RefusesALongerStay()
    {
        var rules = Rules((Arrival, new DayRuleValues(MaxNights: 4)));

        Assert.Null(Check(4, null, rules));
        Assert.Equal(new DayRuleViolation(DayRuleViolationKind.MaxNights, Arrival, 4), Check(5, null, rules));
    }

    [Fact]
    public void TheAdvance_IsCountedFromTodayOfTheProperty_ToTheArrival()
    {
        var days = Arrival.DayNumber - Today.DayNumber;
        var rules = Rules((Arrival, new DayRuleValues(MinAdvanceDays: days, MaxAdvanceDays: days)));

        // Exactly at both limits is allowed; one day sooner is too short, one day later is too long (today is the property's, not the server's).
        Assert.Null(Check(1, null, rules));
        Assert.Equal(new DayRuleViolation(DayRuleViolationKind.AdvanceTooShort, Arrival, days), Check(1, null, rules, Today.AddDays(1)));
        Assert.Equal(new DayRuleViolation(DayRuleViolationKind.AdvanceTooLong, Arrival, days), Check(1, null, rules, Today.AddDays(-1)));
        // An arrival today has an advance of 0.
        Assert.Equal(
            new DayRuleViolation(DayRuleViolationKind.AdvanceTooShort, Arrival, 1),
            DayRuleStayChecks.FirstViolation(Arrival, Arrival.AddDays(1), Arrival, null, Rules((Arrival, new DayRuleValues(MinAdvanceDays: 1)))));
        Assert.Null(DayRuleStayChecks.FirstViolation(Arrival, Arrival.AddDays(1), Arrival, null, Rules((Arrival, new DayRuleValues(MinAdvanceDays: 0)))));
    }

    [Fact]
    public void TheFirstViolationWins_InTheOrderOfTheBrief()
    {
        // Everything is broken at once: a closed arrival, a closed departure, too short, too long and an advance out of range.
        var everything = Rules(
            (Arrival, new DayRuleValues(ClosedToArrival: true, MinNights: 5, MaxNights: 2, MinAdvanceDays: 500, MaxAdvanceDays: 0)),
            (Arrival.AddDays(3), new DayRuleValues(ClosedToDeparture: true)));

        Assert.Equal(DayRuleViolationKind.ArrivalClosed, Check(3, null, everything)!.Kind);
        Assert.Equal(DayRuleViolationKind.DepartureClosed, Check(3, null, Rules(
            (Arrival, new DayRuleValues(MinNights: 5, MaxNights: 2, MinAdvanceDays: 500)),
            (Arrival.AddDays(3), new DayRuleValues(ClosedToDeparture: true))))!.Kind);
        Assert.Equal(DayRuleViolationKind.MinNights, Check(3, null, Rules((Arrival, new DayRuleValues(MinNights: 5, MaxNights: 2, MinAdvanceDays: 500))))!.Kind);
        Assert.Equal(DayRuleViolationKind.MaxNights, Check(3, null, Rules((Arrival, new DayRuleValues(MaxNights: 2, MinAdvanceDays: 500))))!.Kind);
        Assert.Equal(DayRuleViolationKind.AdvanceTooShort, Check(3, null, Rules((Arrival, new DayRuleValues(MinAdvanceDays: 500, MaxAdvanceDays: 0))))!.Kind);
        Assert.Equal(DayRuleViolationKind.AdvanceTooLong, Check(3, null, Rules((Arrival, new DayRuleValues(MaxAdvanceDays: 10))))!.Kind);
    }

    [Theory]
    [InlineData(DayRuleViolationKind.ArrivalClosed, null, "direct_booking_arrival_closed", "DirectBookingArrivalClosed")]
    [InlineData(DayRuleViolationKind.DepartureClosed, null, "direct_booking_departure_closed", "DirectBookingDepartureClosed")]
    [InlineData(DayRuleViolationKind.MinNights, 3, "direct_booking_min_nights_not_met", "DirectBookingMinNightsNotMet")]
    [InlineData(DayRuleViolationKind.MaxNights, 4, "direct_booking_max_nights_exceeded", "DirectBookingMaxNightsExceeded")]
    [InlineData(DayRuleViolationKind.AdvanceTooShort, 2, "direct_booking_advance_too_short", "DirectBookingAdvanceTooShort")]
    [InlineData(DayRuleViolationKind.AdvanceTooLong, 90, "direct_booking_advance_too_long", "DirectBookingAdvanceTooLong")]
    public void EachViolation_IsA422WithItsStableCode_ItsSentenceInBothLanguages_AndTheDateWrittenAsAnIsoDate(
        DayRuleViolationKind kind,
        int? limit,
        string code,
        string key)
    {
        var exception = DayRuleStayChecks.ToException(new DayRuleViolation(kind, new DateOnly(2026, 3, 29), limit));

        Assert.Equal((code, key), (exception.Code, exception.MessageKey));
        Assert.Equal(limit is null ? Array.Empty<object>() : [limit.Value], exception.MessageArgs);
        // The date is written the way the API writes every date, whatever the culture of the server.
        Assert.Equal("2026-03-29", exception.Extensions["date"]);
        Assert.Equal(limit is { } number ? (object?)number : null, exception.Extensions.GetValueOrDefault("limit"));
        foreach (var file in new[] { "SharedResources.resx", "SharedResources.en.resx" })
            Assert.False(string.IsNullOrWhiteSpace(DayRulesTests.ReadResource(file, key)), $"{file} has no sentence for {key}");
    }

    [Fact]
    public void TheDate_IsWrittenInvariantly_UnderAnyCulture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
            var exception = DayRuleStayChecks.ToException(new DayRuleViolation(DayRuleViolationKind.ArrivalClosed, new DateOnly(2026, 12, 24)));

            Assert.Equal("2026-12-24", exception.Extensions["date"]);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
