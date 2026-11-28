using Casazen.Core.Options;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Regulatory;

/// <summary>Where a calendar day stands with respect to the configured CIN deadline (<c>Cin:ExposureDeadline</c>, CO-20).</summary>
public enum CinDeadlinePhase
{
    NotConfigured,

    Upcoming,

    DueToday,

    Passed,
}

/// <summary>
/// The CIN deadline on one day (CO-20, A5-31). <see cref="DaysUntilDeadline"/> is negative once the deadline has passed
/// (it used to stop at 0, so the console said "the deadline is today" for ever) and null without a deadline.
/// </summary>
public sealed record CinDeadlineStatus(DateOnly? Deadline, CinDeadlinePhase Phase, int? DaysUntilDeadline)
{
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
    public const int DueToday = 0;

    public const int Final = -1;

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

public sealed class CinDeadlineCalendar(IOptions<CinOptions> options, TimeProvider timeProvider)
{
    public CinOptions Options => options.Value;

    public CinDeadlineStatus Today() =>
        CinDeadlineStatus.On(options.Value.GetExposureDeadline(), timeProvider.TodayInRomeAsDateOnly());
}
