using Casazen.Core.Entities.Enums;
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

    /// <summary>The longest text of a <c>CreateActivity</c>: one line, what the host wants to be told, never a guest's words.</summary>
    public const int MaxActivityTextLength = 140;

    /// <summary>The furthest deadline of a <c>CreateActivity</c>, in days from the moment the routine runs.</summary>
    public const int MaxActivityDueInDays = 60;

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

    /// <summary>
    /// What a <c>CreateActivity</c> says: its <c>text</c>, 1 to <see cref="MaxActivityTextLength"/> characters once trimmed (the reason of the To do, written
    /// by the host), and its <c>dueInDays</c>, a whole number from 0 to <see cref="MaxActivityDueInDays"/> or none (no deadline). False when the
    /// configuration is not that: the engine then fails the action, it never makes a To do out of "about what was meant".
    /// </summary>
    public static bool TryReadActivity(RoutineAction action, out string text, out int? dueInDays)
    {
        ArgumentNullException.ThrowIfNull(action);

        text = string.Empty;
        dueInDays = null;

        var written = action.GetString("text")?.Trim();
        if (written is not { Length: > 0 and <= MaxActivityTextLength })
            return false;

        if (action.Config.ContainsKey("dueInDays"))
        {
            if (!action.TryGetInt("dueInDays", out var days) || days is < 0 or > MaxActivityDueInDays)
                return false;

            dueInDays = days;
        }

        text = written;
        return true;
    }

    /// <summary>
    /// Whether the configuration of <paramref name="action"/> is one its type accepts: a <c>Wait</c> with a delay <see cref="WaitDelay"/> reads, a
    /// <c>CreateActivity</c> that <see cref="TryReadActivity"/> reads; the other types take any object (their own rules arrive with their implementation).
    /// What the API saves is what the engine can run.
    /// </summary>
    public static bool IsValidConfig(RoutineAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Config.ContainsKey("aiCondition") &&
            (action.Type is not (RoutineActionType.CreateCleaningRequest or RoutineActionType.RequestReview)
             || action.GetString("aiCondition")?.Trim() is not { Length: > 0 and <= 500 }))
            return false;

        return action.Type switch
        {
            RoutineActionType.Wait => WaitDelay(action) is not null,
            RoutineActionType.CreateActivity => TryReadActivity(action, out _, out _),
            _ => true,
        };
    }

    public static bool HasRequiredFields(RoutineAction action) => action.Type switch
    {
        RoutineActionType.MessageToGuest => Guid.TryParse(action.GetString("templateId"), out var id) && id != Guid.Empty,
        RoutineActionType.AddTag => CrmProfileRules.TryNormalizeTags([action.GetString("tag")], out _),
        _ => true,
    };

    /// <summary>A member that is missing counts 0; one that is there must be a whole number of 0 or more.</summary>
    private static bool TryReadWhole(RoutineAction action, string name, out int value)
    {
        value = 0;
        if (!action.Config.ContainsKey(name))
            return true;

        return action.TryGetInt(name, out value) && value >= 0;
    }
}
