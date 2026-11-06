using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// From the days of the Octorate calendar to the blocks of CasaZen (E02/F02/US01/T03): which nights are closed, which of them nothing else takes
/// already, how they group into blocks, and the key a block keeps from one run to the next.
/// </summary>
public class OctorateClosedNightsTests
{
    private const string Room = "956528";

    private static readonly DateOnly From = new(2026, 11, 6);
    private static readonly DateOnly To = From.AddDays(365);
    private static readonly IReadOnlySet<DateOnly> NothingElse = new HashSet<DateOnly>();

    [Fact]
    public void ConsecutiveNights_MakeOneRun_AndAGapStartsAnother()
    {
        var nights = new[] { D(10), D(11), D(12), D(14), D(20), D(21) };

        var runs = OctorateClosedNights.Runs(nights);

        // A run goes from its first night to the day after its last: the check-out day stays free for a new check-in.
        Assert.Equal(
            [new NightRun(D(10), D(13)), new NightRun(D(14), D(15)), new NightRun(D(20), D(22))],
            runs);
    }

    [Fact]
    public void NoNight_MakesNoRun_AndASingleNightMakesOneOfOneNight()
    {
        Assert.Empty(OctorateClosedNights.Runs([]));
        Assert.Equal([new NightRun(D(10), D(11))], OctorateClosedNights.Runs([D(10)]));
    }

    [Fact]
    public void ARunAcrossTheEndOfAMonthAndOfAYear_IsOneRun()
    {
        var runs = OctorateClosedNights.Runs([new DateOnly(2026, 12, 30), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 1)]);

        Assert.Equal([new NightRun(new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 2))], runs);
    }

    [Fact]
    public void TheClosedNights_AreTheStoppedOnesAndTheSoldOutOnesOfDaysOnSale_InOrderAndOnceEach()
    {
        var days = new[]
        {
            Day(14, availability: 0),
            Day(10, availability: 0),
            Day(10, availability: 0),
            Day(11, availability: 1),
            Day(12, availability: 1, stop: true),
            Day(13, availability: 0, price: 0m),
        };

        var nights = OctorateClosedNights.Of(days, From, To, NothingElse);

        // 13 was never opened (no unit, no price): it closes nothing. 11 is open.
        Assert.Equal([D(10), D(12), D(14)], nights);
    }

    [Fact]
    public void TheNightsOutsideTheWindow_AreNotRead_BothEndsIncluded()
    {
        var days = new[]
        {
            new OctorateCalendarDay(Room, From.AddDays(-1), 0, 100m, null, null, null, null, null),
            new OctorateCalendarDay(Room, From, 0, 100m, null, null, null, null, null),
            new OctorateCalendarDay(Room, To, 0, 100m, null, null, null, null, null),
            new OctorateCalendarDay(Room, To.AddDays(1), 0, 100m, null, null, null, null, null),
        };

        Assert.Equal([From, To], OctorateClosedNights.Of(days, From, To, NothingElse));
    }

    [Fact]
    public void ANightSomethingElseTakes_IsNotClosedASecondTime()
    {
        var days = Enumerable.Range(10, 6).Select(d => Day(d, availability: 0)).ToList();
        IReadOnlySet<DateOnly> taken = new HashSet<DateOnly> { D(11), D(12), D(15) };

        var nights = OctorateClosedNights.Of(days, From, To, taken);

        Assert.Equal([D(10), D(13), D(14)], nights);
        Assert.Equal([new NightRun(D(10), D(11)), new NightRun(D(13), D(15))], OctorateClosedNights.Runs(nights));
    }

    [Fact]
    public void TheNightsOfAStayOrABlock_AreFromItsFirstDayToTheDayBeforeItsLast_InsideTheWindow()
    {
        var stay = OctorateClosedNights.NightsIn(Utc(D(20)), Utc(D(23)), From, To).ToList();
        var startsBeforeTheWindow = OctorateClosedNights.NightsIn(Utc(From.AddDays(-2)), Utc(From.AddDays(2)), From, To).ToList();
        var endsAfterTheWindow = OctorateClosedNights.NightsIn(Utc(To.AddDays(-1)), Utc(To.AddDays(3)), From, To).ToList();
        var takesNoNight = OctorateClosedNights.NightsIn(Utc(D(20)), Utc(D(20)), From, To).ToList();

        Assert.Equal([D(20), D(21), D(22)], stay);
        Assert.Equal([From, From.AddDays(1)], startsBeforeTheWindow);
        Assert.Equal([To.AddDays(-1), To], endsAfterTheWindow);
        Assert.Empty(takesNoNight);
    }

    [Fact]
    public void TheKeyOfABlock_IsTheDateOfItsFirstNight_SoTheSameClosureKeepsItsRow()
    {
        Assert.Equal("octorate:2026-11-09", OctorateClosedNights.UidOf(new DateOnly(2026, 11, 9)));
        Assert.StartsWith(OctorateClosedNights.UidPrefix, OctorateClosedNights.UidOf(From));
    }

    [Fact]
    public void TheDaysOfTheRoom_AreTheOnesOfThatProduct_OrTheOnlyUnitOfTheAnswer()
    {
        var own = Day(10, availability: 0);
        var other = Day(10, availability: 0, product: "956530");

        // The room asked for is in the answer: only its days.
        Assert.Equal([own], OctorateClosedNights.DaysOf([own, other], Room));

        // One unit only, named by another id than the one asked for: the call asked for one room, so it is that room.
        Assert.Equal([other], OctorateClosedNights.DaysOf([other], Room));

        // Several units, none of them the room: nobody's days.
        Assert.Empty(OctorateClosedNights.DaysOf([other, Day(10, availability: 0, product: "956531")], Room));
        Assert.Empty(OctorateClosedNights.DaysOf([], Room));
    }

    private static DateOnly D(int day) => new(2026, 11, day);

    private static DateTime Utc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static OctorateCalendarDay Day(int day, int availability, decimal price = 100m, bool stop = false, string product = Room) =>
        new(product, D(day), availability, price, stop, null, null, null, null);
}
