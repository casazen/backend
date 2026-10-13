using Casazen.Core.Options;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Regulatory;

/// <summary>Where a calendar day stands with respect to the configured CIN deadline (<c>Cin:ExposureDeadline</c>, CO-20).</summary>
public enum CinDeadlinePhase
{
    /// <summary>No deadline configured: only the obligation is shown, without a date.</summary>
    NotConfigured,

    /// <summary>The deadline is after today.</summary>
    Upcoming,

    /// <summary>The deadline is today.</summary>
    DueToday,

    /// <summary>The deadline is before today: "deadline passed", never "today" again.</summary>
    Passed,
}

/// <summary>
/// The CIN deadline on one day (CO-20, A5-31). <see cref="DaysUntilDeadline"/> is negative once the deadline has passed
/// (it used to stop at 0, so the console said "the deadline is today" for ever) and null without a deadline.
/// </summary>
public sealed record CinDeadlineStatus(DateOnly? Deadline, CinDeadlinePhase Phase, int? DaysUntilDeadline)
{
    /// <summary>The status of <paramref name="deadline"/> on <paramref name="today"/> (Europe/Rome calendar date).</summary>
    public static CinDeadlineStatus On(DateOnly? deadline, DateOnly today)
    {
        if (deadline is not { } date)
            return new CinDeadlineStatus(null, CinDeadlinePhase.NotConfigured, null);

        var days = date.DayNumber - today.DayNumber;
        var phase = days switch
        {
            > 0 => CinDeadlinePhase.Upcoming,
            0 => CinDeadlinePhase.DueToday,
            _ => CinDeadlinePhase.Passed,
        };
        return new CinDeadlineStatus(date, phase, days);
    }

    /// <summary>Lowercase API value of <see cref="Phase"/>: <c>none</c>, <c>upcoming</c>, <c>today</c> or <c>passed</c>.</summary>
    public string PhaseApiValue => Phase switch
    {
        CinDeadlinePhase.Upcoming => "upcoming",
        CinDeadlinePhase.DueToday => "today",
        CinDeadlinePhase.Passed => "passed",
        _ => "none",
    };
}

/// <summary>
/// Stages of the host alert about properties without a valid CIN (CO-20). A stage is the number of days before the
/// deadline it belongs to: one of <c>Cin:AlertDaysBefore</c> (30, 7, 1 by default), <see cref="DueToday"/> on the
/// deadline day and <see cref="Final"/> once it has passed. Without a deadline there is one stage only,
/// <see cref="Final"/>: the reminder of the obligation. Later stages have smaller values, so a stage is due when it is
/// smaller than the last one sent for the same deadline; a changed deadline starts the sequence again.
/// </summary>
public static class CinAlertStages
{
    /// <summary>The deadline day.</summary>
    public const int DueToday = 0;

    /// <summary>After the deadline, or the one reminder of the obligation when no deadline is configured.</summary>
    public const int Final = -1;

    /// <summary>
    /// The stage due on the day of <paramref name="status"/>: the nearest threshold of <paramref name="alertDaysBefore"/>
    /// already reached before the deadline, <see cref="DueToday"/>, <see cref="Final"/>; null while the deadline is
    /// farther than every threshold.
    /// </summary>
    public static int? Due(CinDeadlineStatus status, IReadOnlyCollection<int> alertDaysBefore)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(alertDaysBefore);
        return status.Phase switch
        {
            CinDeadlinePhase.Upcoming => alertDaysBefore
                .Where(threshold => threshold >= status.DaysUntilDeadline)
                .Select(threshold => (int?)threshold)
                .Min(),
            CinDeadlinePhase.DueToday => DueToday,
            _ => Final,
        };
    }
}

/// <summary>
/// Today's <see cref="CinDeadlineStatus"/>: the configured deadline (<see cref="CinOptions.ExposureDeadline"/>) against the
/// calendar date in Europe/Rome of the injected clock (FD-06).
/// </summary>
public sealed class CinDeadlineCalendar(IOptions<CinOptions> options, TimeProvider timeProvider)
{
    public CinOptions Options => options.Value;

    public CinDeadlineStatus Today() =>
        CinDeadlineStatus.On(options.Value.GetExposureDeadline(), timeProvider.TodayInRomeAsDateOnly());
}
