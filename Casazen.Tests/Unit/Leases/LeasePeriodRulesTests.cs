using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-OVERLAP: two leases of one property cannot cover the same days. The rule on its own, as the database asks it: a lease that overlaps
/// is in the way, one that follows it is not, a lease is never in the way of itself, nor is one of another property, a rejected one or a
/// draft without its two dates, and a lease ended early frees the days after its last day.
/// </summary>
public class LeasePeriodRulesTests
{
    private static readonly Guid Property = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static LeaseContract Lease(
        DateTime start, DateTime end, LeaseStatus status = LeaseStatus.Registered, Guid? property = null, DateTime? endedOn = null)
    {
        var lease = new LeaseContract { PropertyId = property ?? Property, Status = status, StartDate = start, EndDate = end };
        if (endedOn is { } last)
        {
            lease.TerminatedOn = last;
            lease.CurrentTermEndDate = last;
        }

        return lease;
    }

    /// <summary>Whether <paramref name="existing"/> is in the way of a new lease of the property with the days 1 November 2026 to 31 October 2029.</summary>
    private static bool InTheWay(LeaseContract existing, Guid? exceptLeaseId = null) =>
        LeasePeriodRules.InTheWayOf(Property, exceptLeaseId ?? Guid.NewGuid(), Day(2026, 11, 1), Day(2029, 10, 31)).Compile()(existing);

    [Theory]
    [InlineData(2026, 11, 1, 2029, 10, 31, true)] // the same days
    [InlineData(2025, 1, 1, 2026, 11, 1, true)] // one day in common: its last day is our first
    [InlineData(2029, 10, 31, 2033, 10, 30, true)] // one day in common: its first day is our last
    [InlineData(2027, 1, 1, 2027, 12, 31, true)] // inside
    [InlineData(2020, 1, 1, 2035, 12, 31, true)] // around
    [InlineData(2022, 1, 1, 2026, 10, 31, false)] // ends the day before we start
    [InlineData(2029, 11, 1, 2033, 10, 31, false)] // starts the day after we end
    public void ALease_IsInTheWay_OnlyWhenItSharesAtLeastOneDay(int y1, int m1, int d1, int y2, int m2, int d2, bool inTheWay)
    {
        Assert.Equal(inTheWay, InTheWay(Lease(Day(y1, m1, d1), Day(y2, m2, d2))));
        Assert.Equal(inTheWay, LeasePeriodRules.Overlaps(Day(y1, m1, d1), Day(y2, m2, d2), Day(2026, 11, 1), Day(2029, 10, 31)));
    }

    [Fact]
    public void TheLeaseItself_AnotherProperty_ARejectedLease_AndADraftWithoutBothDates_AreNotInTheWay()
    {
        var same = Lease(Day(2026, 11, 1), Day(2029, 10, 31));

        Assert.True(InTheWay(same));
        Assert.False(InTheWay(same, exceptLeaseId: same.Id)); // never in the way of itself
        Assert.False(InTheWay(Lease(Day(2026, 11, 1), Day(2029, 10, 31), property: Guid.NewGuid())));
        Assert.False(InTheWay(Lease(Day(2026, 11, 1), Day(2029, 10, 31), LeaseStatus.Rejected)));
        Assert.False(InTheWay(Lease(Day(2026, 11, 1), LeaseDraftDefaults.PlaceholderDate, LeaseStatus.Draft)));
        Assert.False(InTheWay(Lease(LeaseDraftDefaults.PlaceholderDate, LeaseDraftDefaults.PlaceholderDate, LeaseStatus.Draft)));
    }

    [Fact]
    public void ADraftWithBothItsDates_CountsLikeAnyOtherLease()
    {
        Assert.True(InTheWay(Lease(Day(2026, 11, 1), Day(2029, 10, 31), LeaseStatus.Draft)));
        Assert.True(InTheWay(Lease(Day(2027, 5, 1), Day(2031, 4, 30), LeaseStatus.AwaitingSignature)));
    }

    [Fact]
    public void ALeaseEndedEarly_FreesTheDaysAfterItsLastDay_NotTheOnesUpToIt()
    {
        // The contract goes on to 2032, but it was ended on 31 October 2026: the new one starts on 1 November.
        Assert.False(InTheWay(Lease(Day(2024, 1, 1), Day(2032, 12, 31), endedOn: Day(2026, 10, 31))));
        // Ended on the first day of the new one: that day is still taken.
        Assert.True(InTheWay(Lease(Day(2024, 1, 1), Day(2032, 12, 31), endedOn: Day(2026, 11, 1))));
        Assert.Equal(Day(2026, 10, 31), LeasePeriodRules.EffectiveEnd(Lease(Day(2024, 1, 1), Day(2032, 12, 31), endedOn: Day(2026, 10, 31))));
        Assert.Equal(Day(2032, 12, 31), LeasePeriodRules.EffectiveEnd(Lease(Day(2024, 1, 1), Day(2032, 12, 31))));
    }

    [Fact]
    public void ALeaseThatRenewedByItself_CoversTheNewPeriod()
    {
        var renewed = Lease(Day(2018, 1, 1), Day(2021, 12, 31));
        renewed.CurrentTermEndDate = Day(2027, 12, 31); // two tacit renewals later

        Assert.True(InTheWay(renewed));
    }

    [Fact]
    public void APeriodWithoutDays_CoversNothing()
    {
        Assert.False(LeasePeriodRules.Overlaps(Day(2027, 1, 10), Day(2027, 1, 5), Day(2026, 11, 1), Day(2029, 10, 31)));
        Assert.False(LeasePeriodRules.HasPeriod(Day(2027, 1, 10), Day(2027, 1, 5)));
        Assert.False(LeasePeriodRules.HasPeriod(LeaseDraftDefaults.PlaceholderDate, Day(2027, 1, 5)));
        Assert.False(LeasePeriodRules.HasPeriod(Day(2027, 1, 5), LeaseDraftDefaults.PlaceholderDate));
        Assert.True(LeasePeriodRules.HasPeriod(Day(2027, 1, 5), Day(2027, 1, 5)));
    }
}
