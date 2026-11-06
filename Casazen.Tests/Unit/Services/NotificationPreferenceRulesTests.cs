using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F03/US01/T02 (NF-65, PO-10, ADR-025-R3): the pure rules of the notification preferences — the defaults of each kind of event, what a save
/// may do, and how the quiet hours split the channels of an alert. No database, no clock.
/// </summary>
public class NotificationPreferenceRulesTests
{
    private const NotificationChannels PushAndCounter = NotificationChannels.MobilePush | NotificationChannels.CounterOnly;

    private static readonly TimeOnly QuietStart = new(22, 0);
    private static readonly TimeOnly QuietEnd = new(8, 0);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ─── Defaults (PO-10) ───

    [Theory]
    [InlineData(NotificationEventType.NewGuestMessage, PushAndCounter)]
    [InlineData(NotificationEventType.UrgentGuestMessage, PushAndCounter)]
    [InlineData(NotificationEventType.AiDraftToApprove, PushAndCounter)]
    [InlineData(NotificationEventType.NewBooking, PushAndCounter)]
    [InlineData(NotificationEventType.LegalDeadline, NotificationChannels.MobilePush | NotificationChannels.CounterOnly | NotificationChannels.Email)]
    [InlineData(NotificationEventType.BookingCancelled, NotificationChannels.CounterOnly)]
    [InlineData(NotificationEventType.SyncError, NotificationChannels.CounterOnly)]
    [InlineData(NotificationEventType.ScheduledMessageIssue, NotificationChannels.CounterOnly)]
    public void DefaultChannelsOf_EachKindOfEvent_IsTheDefaultOfPo10(NotificationEventType type, NotificationChannels expected)
    {
        Assert.Equal(expected, NotificationPreferenceRules.DefaultChannelsOf(type));
    }

    [Fact]
    public void EventTypes_AreTheEightKindsOfEvent_InTheOrderOfThePage()
    {
        Assert.Equal(8, NotificationPreferenceRules.EventTypes.Count);
        Assert.Equal(Enum.GetValues<NotificationEventType>(), NotificationPreferenceRules.EventTypes);
        Assert.All(NotificationPreferenceRules.EventTypes, type => Assert.NotEqual(NotificationChannels.None, NotificationPreferenceRules.DefaultChannelsOf(type)));
    }

    [Fact]
    public void EffectiveChannelsOf_NothingSaved_IsTheDefault_AndWhatTheUserChoseOtherwise()
    {
        Assert.Equal(PushAndCounter, NotificationPreferenceRules.EffectiveChannelsOf(NotificationEventType.NewGuestMessage, null));
        Assert.Equal(
            NotificationChannels.Email,
            NotificationPreferenceRules.EffectiveChannelsOf(NotificationEventType.NewGuestMessage, NotificationChannels.Email));

        // «Nothing at all» is a choice for any kind of event but the legal deadline.
        Assert.Equal(
            NotificationChannels.None,
            NotificationPreferenceRules.EffectiveChannelsOf(NotificationEventType.NewBooking, NotificationChannels.None));
    }

    [Theory]
    [InlineData(NotificationChannels.None)]
    [InlineData(NotificationChannels.BrowserPush)]
    public void EffectiveChannelsOf_ALegalDeadlineThatCouldDeliverNothing_FallsBackToTheDefault(NotificationChannels stored)
    {
        var channels = NotificationPreferenceRules.EffectiveChannelsOf(NotificationEventType.LegalDeadline, stored);

        Assert.Equal(NotificationPreferenceRules.DefaultChannelsOf(NotificationEventType.LegalDeadline), channels);
    }

    // ─── What a save may do ───

    [Fact]
    public void ValidateSave_ALegalDeadlineWithoutAChannel_IsRefused()
    {
        Assert.Equal(
            NotificationPreferenceRules.LegalChannelRequiredCode,
            NotificationPreferenceRules.ValidateSave(NotificationEventType.LegalDeadline, NotificationChannels.None));
        Assert.Null(NotificationPreferenceRules.ValidateSave(NotificationEventType.LegalDeadline, NotificationChannels.CounterOnly));
        Assert.Null(NotificationPreferenceRules.ValidateSave(NotificationEventType.NewGuestMessage, NotificationChannels.None));
    }

    [Theory]
    [InlineData(NotificationEventType.NewGuestMessage)]
    [InlineData(NotificationEventType.LegalDeadline)]
    public void ValidateSave_BrowserPush_IsUnavailable(NotificationEventType type)
    {
        Assert.Equal(
            NotificationPreferenceRules.ChannelUnavailableCode,
            NotificationPreferenceRules.ValidateSave(type, NotificationChannels.BrowserPush | NotificationChannels.Email));
        Assert.False(NotificationPreferenceRules.IsAvailable(NotificationChannels.BrowserPush));
        Assert.True(NotificationPreferenceRules.IsAvailable(NotificationChannels.MobilePush));
    }

    [Fact]
    public void IsDeclared_ABitNoChannelHas_IsNotDeclared()
    {
        Assert.True(NotificationPreferenceRules.IsDeclared(PushAndCounter | NotificationChannels.BrowserPush));
        Assert.False(NotificationPreferenceRules.IsDeclared((NotificationChannels)64));
    }

    // ─── Quiet hours ───

    [Fact]
    public void Decide_OutsideTheQuietHours_EverythingGoesNow()
    {
        var decision = NotificationPreferenceRules.Decide(
            NotificationEventType.LegalDeadline,
            NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly,
            QuietStart,
            QuietEnd,
            true,
            Utc(2026, 11, 6, 11));

        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly, decision.Now);
        Assert.Equal(NotificationChannels.None, decision.Deferred);
        Assert.Null(decision.DueAtUtc);
    }

    [Theory]
    [InlineData(22, 0, 7)]
    [InlineData(23, 59, 7)]
    public void Decide_InTheEveningOfTheQuietHours_PushAndEmailWaitForTheNextMorning_TheCounterDoesNot(int hourInRome, int minute, int dueHourUtc)
    {
        // November: Rome is UTC+1, so 22:00 in Rome is 21:00 UTC and 08:00 the next morning is 07:00 UTC.
        var decision = NotificationPreferenceRules.Decide(
            NotificationEventType.NewGuestMessage,
            PushAndCounter | NotificationChannels.Email,
            QuietStart,
            QuietEnd,
            true,
            Utc(2026, 11, 6, hourInRome - 1, minute));

        Assert.Equal(NotificationChannels.CounterOnly, decision.Now);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, decision.Deferred);
        Assert.Equal(Utc(2026, 11, 7, dueHourUtc), decision.DueAtUtc);
    }

    [Fact]
    public void Decide_AfterMidnightInTheQuietHours_TheyEndTheSameMorning()
    {
        var decision = NotificationPreferenceRules.Decide(
            NotificationEventType.NewGuestMessage,
            PushAndCounter,
            QuietStart,
            QuietEnd,
            true,
            Utc(2026, 11, 7, 2));

        Assert.Equal(NotificationChannels.CounterOnly, decision.Now);
        Assert.Equal(NotificationChannels.MobilePush, decision.Deferred);
        Assert.Equal(Utc(2026, 11, 7, 7), decision.DueAtUtc);
    }

    [Fact]
    public void Decide_TheEndOfTheQuietHoursIsOutsideThem()
    {
        // 08:00 in Rome (07:00 UTC) is the first instant after the quiet hours.
        var decision = NotificationPreferenceRules.Decide(NotificationEventType.NewGuestMessage, PushAndCounter, QuietStart, QuietEnd, true, Utc(2026, 11, 7, 7));

        Assert.Equal(PushAndCounter, decision.Now);
        Assert.Null(decision.DueAtUtc);
    }

    [Fact]
    public void Decide_TheQuietHoursStayOnTheClockOfRome_AlsoOnTheDayTheClockChanges()
    {
        // 28 March 2026, 23:00 in Rome is 22:00 UTC (CET); the next morning's 08:00 is already summer time (UTC+2): 06:00 UTC.
        var decision = NotificationPreferenceRules.Decide(NotificationEventType.NewGuestMessage, PushAndCounter, QuietStart, QuietEnd, true, Utc(2026, 3, 28, 22));

        Assert.Equal(Utc(2026, 3, 29, 6), decision.DueAtUtc);
    }

    [Fact]
    public void Decide_ALegalDeadline_OverridesTheQuietHours_WhileTheFlagIsOn()
    {
        var channels = NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly;
        var night = Utc(2026, 11, 6, 22);

        var overriding = NotificationPreferenceRules.Decide(NotificationEventType.LegalDeadline, channels, QuietStart, QuietEnd, true, night);
        var notOverriding = NotificationPreferenceRules.Decide(NotificationEventType.LegalDeadline, channels, QuietStart, QuietEnd, false, night);

        Assert.Equal(channels, overriding.Now);
        Assert.Equal(NotificationChannels.None, overriding.Deferred);
        Assert.Equal(NotificationChannels.CounterOnly, notOverriding.Now);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, notOverriding.Deferred);
    }

    [Fact]
    public void Decide_AnEventWhoseOnlyChannelIsTheCounter_NeverWaits()
    {
        var decision = NotificationPreferenceRules.Decide(
            NotificationEventType.SyncError,
            NotificationChannels.CounterOnly,
            QuietStart,
            QuietEnd,
            true,
            Utc(2026, 11, 6, 23));

        Assert.Equal(NotificationChannels.CounterOnly, decision.Now);
        Assert.Equal(NotificationChannels.None, decision.Deferred);
        Assert.Null(decision.DueAtUtc);
    }

    [Fact]
    public void Decide_StartEqualToEnd_IsNoQuietHours()
    {
        var decision = NotificationPreferenceRules.Decide(
            NotificationEventType.NewGuestMessage,
            PushAndCounter,
            new TimeOnly(0, 0),
            new TimeOnly(0, 0),
            true,
            Utc(2026, 11, 6, 23));

        Assert.Equal(PushAndCounter, decision.Now);
        Assert.Null(decision.DueAtUtc);
    }

    [Fact]
    public void Decide_BrowserPush_IsDropped()
    {
        var decision = NotificationPreferenceRules.Decide(
            NotificationEventType.NewGuestMessage,
            NotificationChannels.BrowserPush | NotificationChannels.CounterOnly,
            QuietStart,
            QuietEnd,
            true,
            Utc(2026, 11, 6, 11));

        Assert.Equal(NotificationChannels.CounterOnly, decision.Now);
    }

    [Fact]
    public void QuietHoursEndUtc_AMorningWindowThatDoesNotCrossMidnight_EndsToday()
    {
        // 13:00-15:00 in Rome; 14:00 in Rome is 13:00 UTC in November.
        var end = NotificationPreferenceRules.QuietHoursEndUtc(new TimeOnly(13, 0), new TimeOnly(15, 0), Utc(2026, 11, 6, 13));

        Assert.Equal(Utc(2026, 11, 6, 14), end);
        Assert.Null(NotificationPreferenceRules.QuietHoursEndUtc(new TimeOnly(13, 0), new TimeOnly(15, 0), Utc(2026, 11, 6, 15)));
    }
}
