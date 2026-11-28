using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US01/T04 (NF-36): the search of the booking site decides with the rules of the nights, by the same check as the checkout. A house is
/// not offered for a stay that arrives on a closed night, leaves on a closed day, is shorter or longer than the limits of its arrival date or breaks
/// an advance limit, and says why; the first free date and the nearby dates never fall on a day a rule refuses; the days of the whole site skip a
/// night closed to arrival. The planner is pure, so these run without a database.
/// </summary>
public class PublicStayPlannerDayRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly DateOnly Arrival = new(2026, 12, 14);
    private static readonly DateTime GeneratedAt = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid HouseId = Guid.NewGuid();

    [Theory]
    [InlineData("arrival", 2, PublicStayReasons.ArrivalClosed)]
    [InlineData("departure", 2, PublicStayReasons.DepartureClosed)]
    [InlineData("min", 2, PublicStayReasons.MinNights)]
    [InlineData("max", 5, PublicStayReasons.MaxNights)]
    [InlineData("advance-short", 2, PublicStayReasons.Advance)]
    [InlineData("advance-long", 2, PublicStayReasons.Advance)]
    public void Search_AStayThatBreaksARuleOfItsNights_IsNotFree_WithItsStableReason_AndNoTotal(string rule, int nights, string reason)
    {
        var rules = rule switch
        {
            "arrival" => Rules((Arrival, new DayRuleValues(ClosedToArrival: true))),
            "departure" => Rules((Arrival.AddDays(2), new DayRuleValues(ClosedToDeparture: true))),
            "min" => Rules((Arrival, new DayRuleValues(MinNights: 3))),
            "max" => Rules((Arrival, new DayRuleValues(MaxNights: 4))),
            "advance-short" => Rules((Arrival, new DayRuleValues(MinAdvanceDays: 100))),
            _ => Rules((Arrival, new DayRuleValues(MaxAdvanceDays: 30))),
        };

        var card = Single(SearchFor(House(), Arrival, nights, rules));

        Assert.False(card.Availability.Available);
        Assert.Equal(reason, card.Availability.Reason);
        Assert.Null(card.Price.TotalCents);
        Assert.Null(card.Price.IncludesTouristTax);
        Assert.Equal(10000L, card.Price.PerNightCents);
    }

    [Fact]
    public void Search_AStayThatRespectsEveryRule_IsFree_WithTheMinimumOfItsArrivalNight()
    {
        var rules = Rules(
            (Arrival, new DayRuleValues(MinNights: 3, MaxNights: 6, MinAdvanceDays: 10, MaxAdvanceDays: 90)),
            (Arrival.AddDays(1), new DayRuleValues(ClosedToArrival: true)),
            (Arrival.AddDays(4), new DayRuleValues(ClosedToArrival: true)));

        var card = Single(SearchFor(House(minNights: 2), Arrival, 4, rules));

        Assert.True(card.Availability.Available);
        Assert.Null(card.Availability.Reason);
        Assert.Equal(3, card.Availability.MinNights);
        Assert.Equal(Arrival, card.Availability.FirstFreeCheckIn);
        Assert.Equal(40000L, card.Price.TotalCents);
    }

    [Fact]
    public void Search_WithoutRules_TheHouseIsJudgedOnItsOwnMinimumAlone_AsItAlwaysWas()
    {
        var withoutData = Single(PublicStayPlanner.Search(
            [House(minNights: 3)], Data(null), Criteria(Arrival, 2), GeneratedAt));
        var withEmptyData = Single(SearchFor(House(minNights: 3), Arrival, 2, Rules()));

        foreach (var card in new[] { withoutData, withEmptyData })
        {
            Assert.False(card.Availability.Available);
            Assert.Equal(PublicStayReasons.MinNights, card.Availability.Reason);
            Assert.Equal(3, card.Availability.MinNights);
            Assert.Null(card.Price.TotalCents);
        }
    }

    [Fact]
    public void Search_TheNightsTakenCome_BeforeTheRules()
    {
        var rules = Rules((Arrival, new DayRuleValues(ClosedToArrival: true)));
        var data = Data(rules) with { Taken = new Dictionary<Guid, IReadOnlySet<DateOnly>> { [HouseId] = new HashSet<DateOnly> { Arrival.AddDays(1) } } };

        var card = Single(PublicStayPlanner.Search([House()], data, Criteria(Arrival, 2), GeneratedAt));

        Assert.Equal(PublicStayReasons.Busy, card.Availability.Reason);
    }

    [Fact]
    public void Search_TheFirstFreeDate_NeverFallsOnADayARuleRefuses()
    {
        var rules = Rules(
            (Arrival, new DayRuleValues(ClosedToArrival: true)),
            (Arrival.AddDays(1), new DayRuleValues(ClosedToArrival: true)),
            (Arrival.AddDays(4), new DayRuleValues(ClosedToDeparture: true)));
        var house = House();

        var card = Single(SearchFor(house, Arrival, 2, rules));

        Assert.Equal(PublicStayReasons.ArrivalClosed, card.Availability.Reason);
        var firstFree = Assert.IsType<DateOnly>(card.Availability.FirstFreeCheckIn);
        Assert.Equal(Arrival.AddDays(3), firstFree);
        Assert.Null(DayRuleStayChecks.FirstViolation(firstFree, firstFree.AddDays(2), Today, house.MinNights, rules));
    }

    [Fact]
    public void Search_TheFirstFreeDate_IsAStayAsLongAsTheMinimumOfTheNightItStartsOn()
    {
        var rules = Rules(
            (Arrival.AddDays(1), new DayRuleValues(MinNights: 3)),
            (Arrival.AddDays(2), new DayRuleValues(MinNights: 3)),
            (Arrival.AddDays(3), new DayRuleValues(MinNights: 3)));
        var data = Data(rules) with
        {
            Taken = new Dictionary<Guid, IReadOnlySet<DateOnly>> { [HouseId] = new HashSet<DateOnly> { Arrival.AddDays(3) } },
        };

        var card = Single(PublicStayPlanner.Search([House()], data, Criteria(Arrival.AddDays(1), 1), GeneratedAt));

        Assert.Equal(PublicStayReasons.MinNights, card.Availability.Reason);
        Assert.Equal(3, card.Availability.MinNights);
        Assert.Equal(Arrival.AddDays(4), card.Availability.FirstFreeCheckIn);
    }

    [Fact]
    public void Search_TheNearbyDates_NeverFallOnADayARuleRefuses()
    {
        var house = House();
        var closedArrival = Arrival.AddDays(1);
        var closedDeparture = Arrival.AddDays(4);
        var rules = Rules(
            (closedArrival, new DayRuleValues(ClosedToArrival: true)),
            (closedDeparture, new DayRuleValues(ClosedToDeparture: true)));
        var data = Data(rules) with
        {
            Taken = new Dictionary<Guid, IReadOnlySet<DateOnly>> { [HouseId] = new HashSet<DateOnly> { Arrival, Arrival.AddDays(1) } },
        };

        var result = PublicStayPlanner.Search([house], data, Criteria(Arrival, 2), GeneratedAt);

        Assert.Equal(0, result.AvailableCount);
        Assert.NotEmpty(result.Alternatives);
        Assert.All(result.Alternatives, alternative =>
        {
            Assert.NotEqual(closedArrival, alternative.CheckIn);
            Assert.NotEqual(closedDeparture, alternative.CheckOut);
            Assert.Null(DayRuleStayChecks.FirstViolation(alternative.CheckIn, alternative.CheckOut, Today, house.MinNights, rules));
            Assert.Equal(1, alternative.AvailableCount);
        });
        Assert.DoesNotContain(result.Alternatives, alternative => alternative.CheckIn == Arrival.AddDays(2));
    }

    [Fact]
    public void Search_TheNearbyDates_AreOfferedOnlyToAHouseWhoseRuleMinimumTheStayMeets()
    {
        var rules = Rules(
            (Arrival.AddDays(-3), new DayRuleValues(MinNights: 3)),
            (Arrival.AddDays(-2), new DayRuleValues(MinNights: 3)),
            (Arrival.AddDays(-1), new DayRuleValues(MinNights: 3)),
            (Arrival.AddDays(1), new DayRuleValues(MinNights: 3)),
            (Arrival.AddDays(2), new DayRuleValues(MinNights: 3)));
        var data = Data(rules) with
        {
            Taken = new Dictionary<Guid, IReadOnlySet<DateOnly>> { [HouseId] = new HashSet<DateOnly> { Arrival } },
        };

        var result = PublicStayPlanner.Search([House()], data, Criteria(Arrival, 2), GeneratedAt);

        Assert.All(result.Alternatives, alternative => Assert.DoesNotContain(
            alternative.CheckIn,
            new[] { Arrival.AddDays(-3), Arrival.AddDays(-2), Arrival.AddDays(-1), Arrival.AddDays(1), Arrival.AddDays(2) }));
    }

    [Fact]
    public void SiteDays_ANightClosedToArrival_IsNotAvailableForTheHouse_AndTheMinimumIsTheOneOfThatNight()
    {
        var other = Guid.NewGuid();
        var houses = new[] { House(minNights: 2), House(other, minNights: 1) };
        var rules = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>
        {
            [HouseId] = Rules((Arrival, new DayRuleValues(ClosedToArrival: true)), (Arrival.AddDays(1), new DayRuleValues(MinNights: 3))),
            [other] = Rules((Arrival.AddDays(1), new DayRuleValues(MinNights: 5))),
        };
        var data = new PublicStayData(
            Today,
            true,
            new Dictionary<Guid, IReadOnlySet<DateOnly>>(),
            new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>(),
            [],
            new Dictionary<Guid, PublicRatingSummary>(),
            rules);

        var days = PublicStayPlanner.SiteDays(houses, data, Arrival, Arrival.AddDays(3), 2);

        Assert.Equal((true, 1), (days[0].AnyAvailable, days[0].MinNights));
        Assert.Equal((true, 3), (days[1].AnyAvailable, days[1].MinNights));
        Assert.Equal((true, 1), (days[2].AnyAvailable, days[2].MinNights));
    }

    [Fact]
    public void SiteDays_WhenEveryHouseIsClosedToArrival_TheNightHasNoStartingStay()
    {
        var rules = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>
        {
            [HouseId] = Rules((Arrival, new DayRuleValues(ClosedToArrival: true)), (Arrival.AddDays(1), new DayRuleValues(MinAdvanceDays: 500))),
        };
        var data = Data(Rules()) with { DayRules = rules };

        var days = PublicStayPlanner.SiteDays([House()], data, Arrival, Arrival.AddDays(3), 2);

        Assert.Equal((false, null, null), (days[0].AnyAvailable, days[0].FromPriceCents, days[0].MinNights));
        Assert.False(days[1].AnyAvailable);
        Assert.True(days[2].AnyAvailable);
    }

    [Theory]
    [InlineData(10, 59, true)]
    [InlineData(11, 1, false)]
    public void Search_TheAdvanceOfARule_IsCountedOnTheClockOfTheHouse_NotOnTheOneOfRome(int hour, int minute, bool offered)
    {
        var now = new DateTime(2026, 10, 9, hour, minute, 0, DateTimeKind.Utc);
        var arrival = new DateOnly(2026, 10, 10);
        var rome = House(Guid.NewGuid());
        var auckland = House(Guid.NewGuid(), timezone: "Pacific/Auckland");
        var rule = new DayRuleValues(MinAdvanceDays: 1);
        var data = new PublicStayData(
            Today,
            true,
            new Dictionary<Guid, IReadOnlySet<DateOnly>>(),
            new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>(),
            [],
            new Dictionary<Guid, PublicRatingSummary>(),
            new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>
            {
                [rome.Id] = Rules((arrival, rule)),
                [auckland.Id] = Rules((arrival, rule)),
            },
            now);

        var cards = PublicStayPlanner.Search([rome, auckland], data, Criteria(arrival, 1), now).Items.ToDictionary(card => card.Id);

        Assert.True(cards[rome.Id].Availability.Available);
        Assert.Equal(offered, cards[auckland.Id].Availability.Available);
        if (!offered)
            Assert.Equal(PublicStayReasons.Advance, cards[auckland.Id].Availability.Reason);

        var checkout = DayRuleStayChecks.FirstViolation(
            arrival, arrival.AddDays(1), DayRuleRules.TodayOf("Pacific/Auckland", new DateTimeOffset(now)), null, Rules((arrival, rule)));
        Assert.Equal(offered, checkout is null);

        var days = PublicStayPlanner.SiteDays([auckland], data, arrival, arrival.AddDays(1), 2);
        Assert.Equal(offered, days.Single().AnyAvailable);
        Assert.True(PublicStayPlanner.SiteDays([rome], data, arrival, arrival.AddDays(1), 2).Single().AnyAvailable);
    }

    [Fact]
    public void Search_TheCardPrice_IsTheOneOfTheQuote_WithThePriceTheHostWroteInItsCalendar()
    {
        var prices = DayRulePricing.HostPrices(null, Rules((Arrival, new DayRuleValues(Price: 150m))));
        var data = Data(Rules((Arrival, new DayRuleValues(Price: 150m)))) with
        {
            HostPrices = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>> { [HouseId] = prices },
        };

        var card = Single(PublicStayPlanner.Search([House()], data, Criteria(Arrival, 2), GeneratedAt));

        Assert.Equal(12500L, card.Price.PerNightCents);
        Assert.Equal(25000L, card.Price.TotalCents);
    }

    private static IReadOnlyDictionary<DateOnly, DayRuleValues> Rules(params (DateOnly Night, DayRuleValues Values)[] rules) =>
        rules.ToDictionary(rule => rule.Night, rule => rule.Values);

    private static PublicStayHouse House(Guid? id = null, int? minNights = null, string? timezone = null) => new(
        id ?? HouseId,
        "casa-regole",
        "Casa Regole",
        "Como",
        null,
        45.8m,
        9.08m,
        [],
        2,
        1,
        4,
        60,
        100m,
        0m,
        0m,
        minNights,
        null,
        null,
        null,
        [],
        Timezone: timezone ?? PublicStayHouse.DefaultTimezone);

    private static PublicStayData Data(IReadOnlyDictionary<DateOnly, DayRuleValues>? rules) => new(
        Today,
        true,
        new Dictionary<Guid, IReadOnlySet<DateOnly>>(),
        new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>(),
        [],
        new Dictionary<Guid, PublicRatingSummary>(),
        rules is null ? null : new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>> { [HouseId] = rules });

    private static PublicStaysCriteria Criteria(DateOnly checkIn, int nights) => new(
        checkIn,
        checkIn.AddDays(nights),
        2,
        0,
        false,
        null,
        null,
        null,
        null,
        null,
        null,
        [],
        [],
        [],
        PublicSiteSearch.Recommended,
        1,
        null,
        []);

    private static PublicStaysResult SearchFor(PublicStayHouse house, DateOnly checkIn, int nights, IReadOnlyDictionary<DateOnly, DayRuleValues> rules) =>
        PublicStayPlanner.Search([house], Data(rules), Criteria(checkIn, nights), GeneratedAt);

    private static PublicStayCard Single(PublicStaysResult result) => Assert.Single(result.Items);

}
