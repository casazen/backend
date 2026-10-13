using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10: when the customer who booked a supplier from its showcase is reminded: 18:00 in Rome the day before the work, once, and
/// only if the supplier took the request before that time. The hour is the wall clock of Rome, so it follows the daylight saving time.
/// </summary>
public class ServiceRequestReminderRulesTests
{
    private static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Theory]
    // Summer time (UTC+2): 18:00 in Rome is 16:00 UTC.
    [InlineData("2026-10-13T07:00:00Z", "2026-10-12T16:00:00Z")]
    // A work at 00:30 in Rome (22:30 UTC the day before) is on the Rome day 14: reminded on the 13th at 18:00 (16:00 UTC).
    [InlineData("2026-10-13T22:30:00Z", "2026-10-13T16:00:00Z")]
    // Winter time (UTC+1): 18:00 in Rome is 17:00 UTC.
    [InlineData("2026-11-10T09:00:00Z", "2026-11-09T17:00:00Z")]
    // The day the clocks go back (25 October 2026): a work on the 26th is reminded on the 25th at 18:00 in Rome, already in winter time.
    [InlineData("2026-10-26T08:00:00Z", "2026-10-25T17:00:00Z")]
    // The day the clocks go forward (29 March 2026): a work on the 30th is reminded on the 29th at 18:00 in Rome, already in summer time.
    [InlineData("2026-03-30T08:00:00Z", "2026-03-29T16:00:00Z")]
    // A work on the day the clocks go back is reminded on the 24th, in summer time.
    [InlineData("2026-10-25T09:00:00Z", "2026-10-24T16:00:00Z")]
    public void DueAt_Is18InRomeOfTheDayBeforeTheRomeDayOfTheWork(string start, string expectedDue) =>
        Assert.Equal(Utc(expectedDue), ServiceRequestReminderRules.DueAt(Utc(start)));

    [Fact]
    public void DueAt_IsTheSameForEveryHourOfTheDayOfTheWork()
    {
        var morning = ServiceRequestReminderRules.DueAt(Utc("2026-10-13T06:00:00Z"));
        var evening = ServiceRequestReminderRules.DueAt(Utc("2026-10-13T20:00:00Z"));

        Assert.Equal(morning, evening);
    }

    [Fact]
    public void IsDue_FromEighteenTheDayBefore_UntilTheWorkStarts()
    {
        var start = Utc("2026-10-13T07:00:00Z");
        var taken = Utc("2026-10-10T10:00:00Z");

        Assert.False(ServiceRequestReminderRules.IsDue(start, taken, Utc("2026-10-12T15:59:59Z")));
        Assert.True(ServiceRequestReminderRules.IsDue(start, taken, Utc("2026-10-12T16:00:00Z")));
        Assert.True(ServiceRequestReminderRules.IsDue(start, taken, Utc("2026-10-12T20:30:00Z")));
        Assert.True(ServiceRequestReminderRules.IsDue(start, taken, Utc("2026-10-13T06:59:59Z")));
        Assert.False(ServiceRequestReminderRules.IsDue(start, taken, Utc("2026-10-13T07:00:00Z")));
        Assert.False(ServiceRequestReminderRules.IsDue(start, taken, Utc("2026-10-13T09:00:00Z")));
    }

    [Fact]
    public void IsDue_ARequestTakenAfterTheReminderTime_GetsNoReminder_TheAcceptedMailSaidIt()
    {
        var start = Utc("2026-10-13T07:00:00Z");
        var now = Utc("2026-10-12T17:30:00Z");

        Assert.False(ServiceRequestReminderRules.IsDue(start, Utc("2026-10-12T16:00:00Z"), now));
        Assert.False(ServiceRequestReminderRules.IsDue(start, Utc("2026-10-12T17:00:00Z"), now));
        Assert.True(ServiceRequestReminderRules.IsDue(start, Utc("2026-10-12T15:59:59Z"), now));
    }

    [Fact]
    public void IsDue_WithoutAMomentOfTheTake_TheRuleIsTheTimeAlone()
    {
        Assert.True(ServiceRequestReminderRules.IsDue(Utc("2026-10-13T07:00:00Z"), null, Utc("2026-10-12T18:00:00Z")));
    }

    [Fact]
    public void WillBeSent_OnlyWhenTheRequestIsTakenBeforeTheReminderTime()
    {
        var start = Utc("2026-10-13T07:00:00Z");

        Assert.True(ServiceRequestReminderRules.WillBeSent(start, Utc("2026-10-12T15:59:59Z")));
        Assert.False(ServiceRequestReminderRules.WillBeSent(start, Utc("2026-10-12T16:00:00Z")));
        Assert.False(ServiceRequestReminderRules.WillBeSent(start, DateTime.MaxValue));
    }

    [Fact]
    public void ReminderTime_IsSixInTheEvening()
    {
        Assert.Equal(new TimeOnly(18, 0), ServiceRequestReminderRules.ReminderTime);
    }
}
