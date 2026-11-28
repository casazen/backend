using Casazen.Core.Authorization;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ConditionEvaluator(
    ILogger<ConditionEvaluator> logger) : IConditionEvaluator
{
    public async Task<bool> EvaluateAsync(
        Condition condition,
        object context,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ValidateCondition(condition);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Invalid condition for org {OrgId}: {Errors}",
                scope.OrgId,
                string.Join(", ", errors));
            return false;
        }

        if (condition.Groups.Count == 0)
        {
            return true;
        }

        foreach (var group in condition.Groups)
        {
            var groupResult = await EvaluateGroupAsync(group, context, scope, cancellationToken);
            if (!groupResult)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<int> PreviewCountAsync(
        Condition condition,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ValidateCondition(condition);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Invalid condition for preview count in org {OrgId}: {Errors}",
                orgId,
                string.Join(", ", errors));
            return 0;
        }

        logger.LogInformation(
            "Preview count requested for org {OrgId} - placeholder returning 0",
            orgId);

        await Task.CompletedTask;
        return 0;
    }

    public List<string> ValidateCondition(Condition condition)
    {
        var errors = new List<string>();

        if (condition == null)
        {
            errors.Add("Condition cannot be null");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(condition.Version))
        {
            errors.Add("Condition version is required");
        }

        if (condition.Groups == null)
        {
            errors.Add("Condition groups cannot be null");
            return errors;
        }

        foreach (var group in condition.Groups)
        {
            if (group == null)
            {
                errors.Add("Condition group cannot be null");
                continue;
            }

            if (string.IsNullOrWhiteSpace(group.Operator))
            {
                errors.Add("Group operator is required");
            }
            else if (group.Operator != "AND" && group.Operator != "OR")
            {
                errors.Add($"Invalid group operator: {group.Operator}. Must be AND or OR");
            }

            if (group.Rules == null)
            {
                errors.Add("Group rules cannot be null");
                continue;
            }

            foreach (var rule in group.Rules)
            {
                if (rule == null)
                {
                    errors.Add("Condition rule cannot be null");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.FieldId))
                {
                    errors.Add("Rule field ID is required");
                    continue;
                }

                if (!FieldCatalog.IsValidField(rule.FieldId))
                {
                    errors.Add($"Unknown field: {rule.FieldId}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.Operator))
                {
                    errors.Add($"Operator is required for field {rule.FieldId}");
                }
                else if (!FieldCatalog.IsValidOperator(rule.FieldId, rule.Operator))
                {
                    errors.Add($"Invalid operator '{rule.Operator}' for field {rule.FieldId}");
                }

                if (rule.Value == null && rule.Operator != "is")
                {
                    errors.Add($"Value is required for operator '{rule.Operator}' on field {rule.FieldId}");
                }
            }
        }

        return errors;
    }

    private async Task<bool> EvaluateGroupAsync(
        ConditionGroup group,
        object context,
        HostScope scope,
        CancellationToken cancellationToken)
    {
        if (group.Rules.Count == 0)
        {
            return true;
        }

        var results = new List<bool>();

        foreach (var rule in group.Rules)
        {
            var ruleResult = await EvaluateRuleAsync(rule, context, scope, cancellationToken);
            results.Add(ruleResult);
        }

        if (group.Operator == "OR")
        {
            return results.Any(r => r);
        }

        return results.All(r => r);
    }

    private async Task<bool> EvaluateRuleAsync(
        ConditionRule rule,
        object context,
        HostScope scope,
        CancellationToken cancellationToken)
    {
        if (context is RoutineConditionFacts routineFacts)
        {
            return RoutineConditions.Satisfies(rule, routineFacts);
        }

        if (context is MarketingSegmentFacts facts)
            return MarketingSegmentRules.Matches(rule, facts.Profile, facts.Today, facts.Stage);

        await Task.CompletedTask;
        return false;
    }
}
