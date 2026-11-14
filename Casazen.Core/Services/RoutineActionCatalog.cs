using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>Whether an action of a routine can run, and why not when it cannot (<see cref="RoutineActionCatalog"/>).</summary>
/// <param name="Type">The action.</param>
/// <param name="Available">It runs and does what it says.</param>
/// <param name="Code">The stable code of its unavailability (<see cref="RoutineCodes.ActionNotAvailable"/>); <c>null</c> when it is available.</param>
public sealed record RoutineActionAvailability(RoutineActionType Type, bool Available, string? Code);

/// <summary>
/// Which actions of a routine really exist (E01/F04/US01/T02, NF-21, AC-NF21-1): the one source the engine, the API and the routines page read. An
/// action that is not available fails with <see cref="RoutineCodes.ActionNotAvailable"/> when a routine runs it, and cannot be part of a routine that is
/// active outside test mode: nothing here pretends. Pure: the answer depends on the type and on the feature flags it is given, nothing else.
/// </summary>
/// <remarks>
/// A task that makes an action real adds its line to <see cref="Runnable"/> (and its case to the switch of the engine) in the same change. The entries are
/// by the <b>name</b> of the member, so a task can list an action before its member is added to <see cref="RoutineActionType"/> (it then has no effect
/// until it exists); an action with no entry is not available.
/// </remarks>
public static class RoutineActionCatalog
{
    /// <summary>
    /// The actions that run, by name: whether each is available with the given flags. <c>Wait</c> (E01/F04/US01/T01) and <c>CreateActivity</c>
    /// (E01/F04/US01/T02) always are. <c>MessageToGuest</c> becomes available while <c>FeatureFlags.ScheduledMessages</c> is on (E05/F07/US01/T03:
    /// until then the message goes nowhere, and the access code with it: it is released only through the variables of the messages) and
    /// <c>SendCrmReminder</c> with E07/F02/US03/T01. Not available, and not listed: <c>NotifyOwner</c>, <c>NotifyStaff</c> and <c>NotifyTeam</c> (what a
    /// notification of a routine is, a To do, a push or an e-mail, is a product decision of the notification preferences of E05),
    /// <c>SendAccessCode</c>, <c>RequestReview</c> (E26/F01/US01/T01) and <c>AddTag</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<IFeatureFlags, bool>> Runnable =
        new Dictionary<string, Func<IFeatureFlags, bool>>(StringComparer.Ordinal)
        {
            [nameof(RoutineActionType.Wait)] = static _ => true,
            [nameof(RoutineActionType.CreateActivity)] = static _ => true,
            ["SendCrmReminder"] = static _ => true,
        };

    /// <summary>Every action of <see cref="RoutineActionType"/>, in the order of the enum, with whether it is available with <paramref name="featureFlags"/>.</summary>
    public static IReadOnlyList<RoutineActionAvailability> All(IFeatureFlags featureFlags)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);

        return [.. Enum.GetValues<RoutineActionType>().Select(type => Of(type, featureFlags))];
    }

    /// <summary>Whether <paramref name="type"/> is available with <paramref name="featureFlags"/>, and the code that says why not.</summary>
    public static RoutineActionAvailability Of(RoutineActionType type, IFeatureFlags featureFlags)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);

        var available = Runnable.TryGetValue(type.ToString(), out var isAvailable) && isAvailable(featureFlags);
        return new RoutineActionAvailability(type, available, available ? null : RoutineCodes.ActionNotAvailable);
    }

    /// <summary><see cref="Of"/> as a yes or no.</summary>
    public static bool IsAvailable(RoutineActionType type, IFeatureFlags featureFlags) => Of(type, featureFlags).Available;
}
