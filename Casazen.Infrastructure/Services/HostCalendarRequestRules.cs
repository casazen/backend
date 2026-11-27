using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The limits of a read of the calendar of the host, the same for the services that make one (<see cref="HostCalendarService"/>,
/// <see cref="PropertyDayRuleService"/>): the days asked for and the properties asked for by id.
/// </summary>
internal static class HostCalendarRequestRules
{
    /// <summary>The range does not end before it starts and is at most <see cref="HostCalendarLimits.MaxWindowDays"/> days long.</summary>
    public static void EnsureWindow(DateOnly from, DateOnly to)
    {
        if (to < from)
            throw new ArgumentException("The last day of the range is before the first.", nameof(to));

        if (to.DayNumber - from.DayNumber + 1 > HostCalendarLimits.MaxWindowDays)
        {
            throw new DomainRuleException(
                HostCalendarErrorCodes.WindowTooLarge, HostCalendarErrorCodes.WindowTooLargeMessageKey, HostCalendarLimits.MaxWindowDays);
        }
    }

    // The properties asked for by id, without repeats; null when none were asked for.
    public static List<Guid>? RequestedIds(IReadOnlyCollection<Guid>? ids)
    {
        if (ids is null || ids.Count == 0)
            return null;

        var distinct = ids.Distinct().ToList();
        if (distinct.Count > HostCalendarLimits.MaxProperties)
        {
            throw new DomainRuleException(
                HostCalendarErrorCodes.TooManyProperties, HostCalendarErrorCodes.TooManyPropertiesMessageKey, HostCalendarLimits.MaxProperties);
        }

        return distinct;
    }
}
