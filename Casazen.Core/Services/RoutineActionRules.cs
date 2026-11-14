using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// What the configuration of each action of a routine must say (E01/F04/US01, NF-21). Pure: the engine reads the configuration of a saved routine with
/// it, and the API (E01/F04/US01/T03) refuses with the same rules what the engine would refuse.
/// </summary>
public static class RoutineActionRules
{
    /// <summary>The longest a Wait lasts, in days (ADR-022-R10): days and hours together.</summary>
    public const int MaxWaitDays = 365;

    /// <summary>
    /// How long a <c>Wait</c> lasts: <c>days</c> and/or <c>hours</c>, whole numbers of 0 or more, together at least one hour and at most
    /// <see cref="MaxWaitDays"/> days. <c>null</c> when the configuration is not that (neither member, a member that is not a whole number, a negative
    /// one, a delay of nothing or of more than a year): the engine then fails the action, it never waits "about" what was meant.
    /// </summary>
    public static TimeSpan? WaitDelay(RoutineAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!TryReadWhole(action, "days", out var days) || !TryReadWhole(action, "hours", out var hours))
            return null;

        var total = ((long)days * 24) + hours;
        return total is >= 1 and <= MaxWaitDays * 24L ? TimeSpan.FromHours(total) : null;
    }

    /// <summary>A member that is missing counts 0; one that is there must be a whole number of 0 or more.</summary>
    private static bool TryReadWhole(RoutineAction action, string name, out int value)
    {
        value = 0;
        if (!action.Config.ContainsKey(name))
            return true;

        return action.TryGetInt(name, out value) && value >= 0;
    }
}
