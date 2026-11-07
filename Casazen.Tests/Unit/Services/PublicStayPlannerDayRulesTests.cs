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

    // --- A house is not offered for a stay that breaks a rule --------------------------------------------------

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
        // The checkout would refuse the stay, so no total is shown for it; the price per night is still the night's.
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
        // The minimum the card shows is the one of the night the stay arrives on (3), not the house's (2).
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

    // --- The first free date and the nearby dates --------------------------------------------------------------

    [Fact]
    public void Search_TheFirstFreeDate_NeverFallsOnADayARuleRefuses()
    {
        // The 14th and the 15th are closed to arrival; a stay of two nights from the 16th would leave on the 18th, closed to departure.
        var rules = Rules(
            (Arrival, new DayRuleValues(ClosedToArrival: true)),
            (Arrival.AddDays(1), new DayRuleValues(ClosedToArrival: true)),
            (Arrival.AddDays(4), new DayRuleValues(ClosedToDeparture: true)));
        var house = House();

        var card = Single(SearchFor(house, Arrival, 2, rules));

        Assert.Equal(PublicStayReasons.ArrivalClosed, card.Availability.Reason);
        // The 16th (leaves on the 18th, closed) is refused too: the first date is the 17th.
        var firstFree = Assert.IsType<DateOnly>(card.Availability.FirstFreeCheckIn);
        Assert.Equal(Arrival.AddDays(3), firstFree);
        Assert.Null(DayRuleStayChecks.FirstViolation(firstFree, firstFree.AddDays(2), Today, house.MinNights, rules));
    }

    [Fact]
    public void Search_TheFirstFreeDate_IsAStayAsLongAsTheMinimumOfTheNightItStartsOn()
    {
        // The house takes one night, but the nights of the 15th, the 16th and the 17th ask for three. The stay asked is one night from the 15th
        // (too short), and the 17th is taken: a stay of three nights from the 15th, the 16th or the 17th would take it, so the first date is the
        // 18th, which asks for nothing.
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
        // Nothing is free for the 14th and the 15th: the house is taken those nights.
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
        // The 15th is closed to arrival and the 16th would leave on the closed 18th: neither is proposed.
        Assert.DoesNotContain(result.Alternatives, alternative => alternative.CheckIn == Arrival.AddDays(2));
    }

    [Fact]
    public void Search_TheNearbyDates_AreOfferedOnlyToAHouseWhoseRuleMinimumTheStayMeets()
    {
        // The nights around the 14th ask for three nights; the stay asked is two: no nearby date is proposed on the nights that ask for three.
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

    // --- The days of the whole site ----------------------------------------------------------------------------

    [Fact]
    public void SiteDays_ANightClosedToArrival_IsNotAvailableForTheHouse_AndTheMinimumIsTheOneOfThatNight()
    {
        var other = Guid.NewGuid();
        var houses = new[] { House(minNights: 2), House(other, minNights: 1) };
        var rules = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>
        {
            // The first house: closed on the 14th, three nights on the 15th. The second house: five nights on the 15th, nothing else.
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

        // The 14th: the first house is closed, the second one is free with its own minimum of 1.
        Assert.Equal((true, 1), (days[0].AnyAvailable, days[0].MinNights));
        // The 15th: the shortest minimum of that night among the houses is the first one's 3.
        Assert.Equal((true, 3), (days[1].AnyAvailable, days[1].MinNights));
        // The 16th: no rule; the houses' own minimums, the shortest of them is 1.
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

        // Closed to arrival, then outside the advance limits (the arrival is 66 days away, the rule asks for 500): nothing starts there.
        Assert.Equal((false, null, null), (days[0].AnyAvailable, days[0].FromPriceCents, days[0].MinNights));
        Assert.False(days[1].AnyAvailable);
        Assert.True(days[2].AnyAvailable);
    }

    // --- The advance counts on the clock of the house ----------------------------------------------------------

    [Theory]
    [InlineData(10, 59, true)]
    [InlineData(11, 1, false)]
    public void Search_TheAdvanceOfARule_IsCountedOnTheClockOfTheHouse_NotOnTheOneOfRome(int hour, int minute, bool offered)
    {
        // Auckland is 13 hours ahead of UTC in October: 11:00 UTC on the 9th is its midnight. At 10:59 the 10th is a day ahead of it, a minute later it
        // is today (advance 0). In Rome it is the 9th all that time, so the night of the 10th is a day ahead throughout.
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

        // It is the clock the checkout reads: the same function of the zone and the instant.
        var checkout = DayRuleStayChecks.FirstViolation(
            arrival, arrival.AddDays(1), DayRuleRules.TodayOf("Pacific/Auckland", new DateTimeOffset(now)), null, Rules((arrival, rule)));
        Assert.Equal(offered, checkout is null);

        // And the days of the whole site: the night of the 10th starts a stay in Rome all along, in Auckland only while it is a day ahead.
        var days = PublicStayPlanner.SiteDays([auckland], data, arrival, arrival.AddDays(1), 2);
        Assert.Equal(offered, days.Single().AnyAvailable);
        Assert.True(PublicStayPlanner.SiteDays([rome], data, arrival, arrival.AddDays(1), 2).Single().AnyAvailable);
    }

    // --- The price a card shows --------------------------------------------------------------------------------

    [Fact]
    public void Search_TheCardPrice_IsTheOneOfTheQuote_WithThePriceTheHostWroteInItsCalendar()
    {
        // The host wrote 150 for the 14th (a Monday: the rate is 100): the host prices the planner receives already hold it.
        var prices = DayRulePricing.HostPrices(null, Rules((Arrival, new DayRuleValues(Price: 150m))));
        var data = Data(Rules((Arrival, new DayRuleValues(Price: 150m)))) with
        {
            HostPrices = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>> { [HouseId] = prices },
        };

        var card = Single(PublicStayPlanner.Search([House()], data, Criteria(Arrival, 2), GeneratedAt));

        // 150 + 100 for two nights: 125 a night.
        Assert.Equal(12500L, card.Price.PerNightCents);
        Assert.Equal(25000L, card.Price.TotalCents);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------

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
