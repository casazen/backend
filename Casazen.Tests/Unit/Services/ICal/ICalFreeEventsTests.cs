using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>Grouping, keys and cap of the free-looking events the supplier is asked about (<see cref="ICalFreeEvents"/>).</summary>
public class ICalFreeEventsTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, SupplierFreeEventDecision> NoAnswers =
        new Dictionary<string, SupplierFreeEventDecision>();

    private static ICalOccurrence Day(string? uid, DateOnly date, bool recurring = false, string? summary = "Evento") =>
        new(uid, summary, date, date.AddDays(1), date,
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-2),
            date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-2),
            IsAllDay: true, IsRecurring: recurring);

    [Fact]
    public void Plan_SeriesOccurrences_AreOneRowWithOneKey()
    {
        var plan = ICalFreeEvents.Plan(
            [Day("series", new DateOnly(2026, 10, 14), true), Day("series", new DateOnly(2026, 10, 21), true), Day("series", new DateOnly(2026, 10, 28), true)],
            NoAnswers, Now);

        var row = Assert.Single(plan.Candidates);
        Assert.True(row.Recurring);
        Assert.Equal(3, row.Occurrences);
        Assert.Equal(3, plan.Blocking.Count);
    }

    [Fact]
    public void Plan_SingleEventWithOtherStart_HasAnotherKey()
    {
        var first = ICalFreeEvents.KeyOf(Day("one", new DateOnly(2026, 10, 14)));
        var moved = ICalFreeEvents.KeyOf(Day("one", new DateOnly(2026, 10, 15)));

        Assert.NotEqual(first.DecisionKey, moved.DecisionKey);
        Assert.Equal(first.UidHash, moved.UidHash);
    }

    [Fact]
    public void KeyOf_EventWithoutUid_IsStableAcrossSyncsAndNeverTheRawSummary()
    {
        var a = ICalFreeEvents.KeyOf(Day(null, new DateOnly(2026, 10, 14), summary: "Visita medica"));
        var b = ICalFreeEvents.KeyOf(Day(null, new DateOnly(2026, 10, 14), summary: "Visita medica"));

        Assert.Equal(a, b);
        Assert.Equal(64, a.DecisionKey.Length);
        Assert.DoesNotContain("Visita", a.DecisionKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SeriesWithoutUid_SharesOneKeyAcrossOccurrences()
    {
        var plan = ICalFreeEvents.Plan(
            [Day(null, new DateOnly(2026, 10, 14), true, "Palestra"), Day(null, new DateOnly(2026, 10, 21), true, "Palestra")],
            NoAnswers, Now);

        Assert.Equal(2, Assert.Single(plan.Candidates).Occurrences);
    }

    [Fact]
    public void Plan_AvailableAnswer_BlocksNothingButKeepsTheRow()
    {
        var occurrence = Day("free", new DateOnly(2026, 10, 14));
        var key = ICalFreeEvents.KeyOf(occurrence).DecisionKey;

        var plan = ICalFreeEvents.Plan([occurrence], new Dictionary<string, SupplierFreeEventDecision> { [key] = SupplierFreeEventDecision.Available }, Now);

        Assert.Empty(plan.Blocking);
        Assert.Single(plan.Candidates);
        Assert.Contains(key, plan.SeenKeys);
    }

    [Fact]
    public void Plan_BusyAnswer_BlocksEvenBeyondTheCap()
    {
        var occurrences = Enumerable.Range(0, 5).Select(i => Day("busy", new DateOnly(2026, 10, 14).AddDays(i), true)).ToList();
        var key = ICalFreeEvents.KeyOf(occurrences[0]).DecisionKey;

        var plan = ICalFreeEvents.Plan(
            occurrences, new Dictionary<string, SupplierFreeEventDecision> { [key] = SupplierFreeEventDecision.Busy }, Now, maxPendingOccurrences: 1);

        Assert.Equal(5, plan.Blocking.Count);
        Assert.Equal(0, plan.OverCap);
    }

    [Fact]
    public void Plan_MoreUnansweredOccurrencesThanTheCap_KeepsTheNearestAndCountsTheRest()
    {
        var occurrences = Enumerable.Range(0, 250)
            .Select(i => Day($"e{i}", new DateOnly(2026, 10, 14).AddDays(i)))
            .ToList();

        var plan = ICalFreeEvents.Plan(occurrences, NoAnswers, Now);

        Assert.Equal(ICalFreeEvents.MaxPendingOccurrences, plan.Candidates.Count);
        Assert.Equal(ICalFreeEvents.MaxPendingOccurrences, plan.Blocking.Count);
        Assert.Equal(50, plan.OverCap);
        Assert.Equal(250, plan.SeenKeys.Count);
        Assert.Equal(new DateOnly(2026, 10, 14), plan.Blocking.Min(o => o.StartDate));
    }

    [Fact]
    public void Plan_BlockingSwitchedOff_UnansweredEventsOccupyNothing()
    {
        var plan = ICalFreeEvents.Plan([Day("free", new DateOnly(2026, 10, 14))], NoAnswers, Now, blockUndecided: false);

        Assert.Empty(plan.Blocking);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void Plan_Summary_IsCutToTheLengthOfALabel()
    {
        var plan = ICalFreeEvents.Plan([Day("free", new DateOnly(2026, 10, 14), summary: new string('x', 200))], NoAnswers, Now);

        Assert.Equal(80, Assert.Single(plan.Candidates).Summary!.Length);
    }
}
