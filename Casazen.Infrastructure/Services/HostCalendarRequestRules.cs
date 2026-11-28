using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

internal static class HostCalendarRequestRules
{
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
