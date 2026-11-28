using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class ServiceRequestReminderRulesTests
{
    private static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Theory]
    [InlineData("2026-10-13T07:00:00Z", "2026-10-12T16:00:00Z")]
    [InlineData("2026-10-13T22:30:00Z", "2026-10-13T16:00:00Z")]
    [InlineData("2026-11-10T09:00:00Z", "2026-11-09T17:00:00Z")]
    [InlineData("2026-10-26T08:00:00Z", "2026-10-25T17:00:00Z")]
    [InlineData("2026-03-30T08:00:00Z", "2026-03-29T16:00:00Z")]
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
