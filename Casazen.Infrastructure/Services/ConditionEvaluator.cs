using Casazen.Core.Authorization;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Server-side condition evaluation for marketing segments and automation routines (ADR-014).
/// </summary>
public class ConditionEvaluator(
    ILogger<ConditionEvaluator> logger) : IConditionEvaluator
{
    /// <inheritdoc/>
    public async Task<bool> EvaluateAsync(
        Condition condition,
        object context,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(scope);

        // Validate condition before evaluation
        var errors = ValidateCondition(condition);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Invalid condition for org {OrgId}: {Errors}",
                scope.OrgId,
                string.Join(", ", errors));
            return false;
        }

        // Evaluate groups (default to AND if empty)
        if (condition.Groups.Count == 0)
        {
            return true;
        }

        // All groups must evaluate (combined with implicit AND at top level)
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

    /// <inheritdoc/>
    public async Task<int> PreviewCountAsync(
        Condition condition,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(scope);

        // Validate condition
        var errors = ValidateCondition(condition);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Invalid condition for preview count in org {OrgId}: {Errors}",
                orgId,
                string.Join(", ", errors));
            return 0;
        }

        // For MVP: return 0 as placeholder
        // Full implementation will query guests/bookings matching the condition
        // Performance target: <5s for 10k guests (ADR-014-R6)
        logger.LogInformation(
            "Preview count requested for org {OrgId} - placeholder returning 0",
            orgId);

        await Task.CompletedTask;
        return 0;
    }

    /// <inheritdoc/>
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

        // Validate each group
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

            // Validate each rule
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

                // Check field exists in catalog
                if (!FieldCatalog.IsValidField(rule.FieldId))
                {
                    errors.Add($"Unknown field: {rule.FieldId}");
                    continue;
                }

                // Check operator is valid for the field
                if (string.IsNullOrWhiteSpace(rule.Operator))
                {
                    errors.Add($"Operator is required for field {rule.FieldId}");
                }
                else if (!FieldCatalog.IsValidOperator(rule.FieldId, rule.Operator))
                {
                    errors.Add($"Invalid operator '{rule.Operator}' for field {rule.FieldId}");
                }

                // Value is optional for some operators, but should be present for most
                if (rule.Value == null && rule.Operator != "is")
                {
                    errors.Add($"Value is required for operator '{rule.Operator}' on field {rule.FieldId}");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Evaluate a single condition group.
    /// </summary>
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

        // Combine results based on operator
        if (group.Operator == "OR")
        {
            return results.Any(r => r);
        }

        // Default to AND
        return results.All(r => r);
    }

    /// <summary>
    /// Evaluate a single condition rule against context.
    /// </summary>
    private async Task<bool> EvaluateRuleAsync(
        ConditionRule rule,
        object context,
        HostScope scope,
        CancellationToken cancellationToken)
    {
        // For MVP: placeholder implementation
        // Full implementation will extract field values from context and apply operators
        logger.LogDebug(
            "Evaluating rule: {FieldId} {Operator} {Value} for org {OrgId}",
            rule.FieldId,
            rule.Operator,
            rule.Value,
            scope.OrgId);

        await Task.CompletedTask;
        return false;
    }
}
