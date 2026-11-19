using Casazen.Core.Authorization;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The condition of a routine, read for a scheduled message (E05/F07/US01): the same reading as the engine gives it when the routine fires (<see cref="RoutineConditions"/> and
/// <see cref="IConditionEvaluator"/> on the facts of the booking), so a routine «only for stays of at least four nights» or «only for Airbnb» plans, and sends, a message only for those stays.
/// </summary>
internal static class ScheduledMessageConditions
{
    /// <summary>
    /// Whether the booking meets the condition. No condition, or one with no rule, is met; one that is not a condition, names a field the routines do not read or a value they refuse is NOT met (the engine
    /// records such a routine as failed and runs nothing: a message is never planned or sent on a condition nobody can read); a rule on a fact that is not known is not met.
    /// </summary>
    public static async Task<bool> HoldsAsync(
        IConditionEvaluator evaluator,
        Guid orgId,
        Guid routineId,
        string? conditionJson,
        RoutineConditionFacts facts,
        ILogger logger,
        CancellationToken ct)
    {
        var parsed = RoutineConditions.Parse(conditionJson);
        if (!parsed.Valid)
        {
            logger.LogWarning("Routine {RoutineId} of org {OrgId} has a condition that is not valid: its scheduled messages are not planned", routineId, orgId);
            return false;
        }

        if (parsed.Condition is not { } condition)
            return true;

        var errors = evaluator.ValidateCondition(condition)
            .Concat(RoutineConditions.UnsupportedFieldErrors(condition))
            .Concat(RoutineConditions.ValueErrors(condition))
            .ToList();
        if (errors.Count > 0)
        {
            logger.LogWarning("Routine {RoutineId} of org {OrgId} has an invalid condition ({Errors}): its scheduled messages are not planned", routineId, orgId, string.Join("; ", errors));
            return false;
        }

        // No rule at all (the {} of a routine with no condition): nothing to prove, nothing to read.
        if (condition.Groups.All(group => group.Rules.Count == 0))
            return true;

        return await evaluator.EvaluateAsync(condition, facts, new HostScope(orgId), ct);
    }
}
