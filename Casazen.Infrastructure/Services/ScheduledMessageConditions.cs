using Casazen.Core.Authorization;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

internal static class ScheduledMessageConditions
{
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

        if (condition.Groups.All(group => group.Rules.Count == 0))
            return true;

        return await evaluator.EvaluateAsync(condition, facts, new HostScope(orgId), ct);
    }
}
