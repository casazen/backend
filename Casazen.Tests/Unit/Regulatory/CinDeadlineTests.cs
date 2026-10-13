using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// CO-20 (A5-31): the CIN deadline is configuration (<c>Cin:ExposureDeadline</c>, no default: RS-2), its phase moves from
/// "days left" to "today" to "passed" (the old count stopped at 0 and said "today" for ever), and the alert stages come
/// from the configured thresholds.
/// </summary>
public class CinDeadlineTests
{
    private static readonly DateOnly Deadline = new(2026, 3, 1);

    [Theory]
    [InlineData(-10, CinDeadlinePhase.Upcoming, 10, "upcoming")]
    [InlineData(-1, CinDeadlinePhase.Upcoming, 1, "upcoming")]
    [InlineData(0, CinDeadlinePhase.DueToday, 0, "today")]
    [InlineData(1, CinDeadlinePhase.Passed, -1, "passed")]
    [InlineData(200, CinDeadlinePhase.Passed, -200, "passed")]
    public void On_ConfiguredDeadline_ReturnsPhaseAndSignedDays(int dayOffset, CinDeadlinePhase phase, int days, string apiValue)
    {
        var status = CinDeadlineStatus.On(Deadline, Deadline.AddDays(dayOffset));

        Assert.Equal(Deadline, status.Deadline);
        Assert.Equal(phase, status.Phase);
        Assert.Equal(days, status.DaysUntilDeadline);
        Assert.Equal(apiValue, status.PhaseApiValue);
    }

    [Fact]
    public void On_NoDeadline_ReturnsNotConfiguredWithoutDays()
    {
        var status = CinDeadlineStatus.On(null, Deadline);

        Assert.Equal(CinDeadlinePhase.NotConfigured, status.Phase);
        Assert.Null(status.Deadline);
        Assert.Null(status.DaysUntilDeadline);
        Assert.Equal("none", status.PhaseApiValue);
    }

    [Theory]
    [InlineData(-40, null)]
    [InlineData(-31, null)]
    [InlineData(-30, 30)]
    [InlineData(-10, 30)]
    [InlineData(-7, 7)]
    [InlineData(-2, 7)]
    [InlineData(-1, 1)]
    [InlineData(0, CinAlertStages.DueToday)]
    [InlineData(1, CinAlertStages.Final)]
    [InlineData(200, CinAlertStages.Final)]
    public void Due_DefaultThresholds_ReturnsTheStageOfTheDay(int dayOffset, int? expected)
    {
        var status = CinDeadlineStatus.On(Deadline, Deadline.AddDays(dayOffset));

        Assert.Equal(expected, CinAlertStages.Due(status, new CinOptions().GetAlertDaysBefore()));
    }

    [Fact]
    public void Due_NoDeadline_ReturnsTheSingleReminderStage()
    {
        Assert.Equal(CinAlertStages.Final, CinAlertStages.Due(CinDeadlineStatus.On(null, Deadline), [30, 7, 1]));
    }

    [Fact]
    public void GetAlertDaysBefore_Configured_ReturnsDistinctFromFarthest()
    {
        var options = new CinOptions { AlertDaysBefore = [7, 14, 7] };

        Assert.Equal([14, 7], options.GetAlertDaysBefore());
        Assert.Equal([30, 7, 1], new CinOptions().GetAlertDaysBefore());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("2026-03-01", "2026-03-01")]
    [InlineData(" 2026-03-01 ", "2026-03-01")]
    public void GetExposureDeadline_EmptyOrIsoDate_ParsesIt(string? configured, string? expected)
    {
        var options = new CinOptions { ExposureDeadline = configured };

        Assert.Equal(expected is null ? null : DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), options.GetExposureDeadline());
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("01/03/2026")]
    [InlineData("2026-02-30")]
    [InlineData("marzo 2026")]
    public void Validate_DeadlineNotIsoDate_FailsAtStartup(string configured)
    {
        var result = new CinOptionsValidator().Validate(null, new CinOptions { ExposureDeadline = configured });

        Assert.True(result.Failed);
        Assert.Contains("Cin__ExposureDeadline", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(400)]
    public void Validate_ThresholdOutOfRange_FailsAtStartup(int days)
    {
        var result = new CinOptionsValidator().Validate(null, new CinOptions { AlertDaysBefore = [30, days] });

        Assert.True(result.Failed);
        Assert.Contains("Cin__AlertDaysBefore", result.FailureMessage);
    }

    [Fact]
    public void Today_UsesTheRomeCalendarOfTheInjectedClock()
    {
        // 23:30 UTC of 28 February is 00:30 of 1 March in Rome: the deadline day.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 28, 23, 30, 0, TimeSpan.Zero));
        var calendar = new CinDeadlineCalendar(Options.Create(new CinOptions { ExposureDeadline = "2026-03-01" }), clock);

        Assert.Equal(CinDeadlinePhase.DueToday, calendar.Today().Phase);

        clock.Advance(TimeSpan.FromDays(200));
        Assert.Equal(CinDeadlinePhase.Passed, calendar.Today().Phase);
        Assert.Equal(-200, calendar.Today().DaysUntilDeadline);
    }
}
