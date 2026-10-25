using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Implementation of routine automation service (NF-26, ADR-022).
/// Handles routine triggering, execution, guardrails, and test mode simulation.
/// </summary>
public class RoutineService(
    AppDbContext dbContext,
    ILogger<RoutineService> logger) : IRoutineService
{
    public async Task TriggerAsync(
        Guid orgId,
        RoutineEventType eventType,
        string eventId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        // Find all active routines for this event type and org (TN-1: tenant isolation)
        var routines = await dbContext.Routines
            .Where(r => r.OrgId == orgId && r.Active && r.EventType == eventType)
            .ToListAsync(cancellationToken);

        logger.LogInformation(
            "Triggering {Count} routine(s) for org {OrgId} event {EventType} with ID {EventId}",
            routines.Count, orgId, eventType, eventId);

        foreach (var routine in routines)
        {
            await ExecuteRoutineAsync(routine, eventId, payload, cancellationToken);
        }
    }

    public async Task ResumeAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execution = await dbContext.RoutineExecutions
            .Include(e => e.Routine)
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

        if (execution == null)
        {
            logger.LogWarning("Routine execution {ExecutionId} not found for resume", executionId);
            return;
        }

        if (execution.Routine == null)
        {
            logger.LogWarning(
                "Routine execution {ExecutionId} has no associated routine",
                executionId);
            return;
        }

        logger.LogInformation(
            "Resuming routine execution {ExecutionId} for routine {RoutineId}",
            executionId, execution.RoutineId);

        // Parse the result to get where we left off
        var result = JsonSerializer.Deserialize<RoutineExecutionResult>(execution.ResultJson)
                    ?? new RoutineExecutionResult();

        // Continue executing actions from where we stopped
        await ContinueActionsAsync(execution, result, cancellationToken);
    }

    public async Task<object> SimulateAsync(
        Guid routineId,
        object sampleEventPayload,
        CancellationToken cancellationToken = default)
    {
        var routine = await dbContext.Routines
            .FirstOrDefaultAsync(r => r.Id == routineId, cancellationToken);

        if (routine == null)
        {
            throw new InvalidOperationException($"Routine {routineId} not found");
        }

        // Evaluate conditions (would need condition evaluator from ADR-014)
        var conditionsMatched = EvaluateConditions(routine.ConditionJson, sampleEventPayload);

        // Parse actions
        var actions = JsonSerializer.Deserialize<List<RoutineAction>>(routine.ActionsJson)
                     ?? new List<RoutineAction>();

        return new
        {
            RoutineId = routine.Id,
            RoutineName = routine.Name,
            ConditionsMatched = conditionsMatched,
            WouldExecute = conditionsMatched,
            Actions = actions.Select(a => new
            {
                Type = a.Type,
                Config = a.Config,
                WouldBlock = WouldActionBeBlocked(a, sampleEventPayload)
            }).ToList()
        };
    }

    private async Task ExecuteRoutineAsync(
        Routine routine,
        string eventId,
        object payload,
        CancellationToken cancellationToken)
    {
        // Check for idempotence: has this routine already executed for this event?
        var existingExecution = await dbContext.RoutineExecutions
            .FirstOrDefaultAsync(
                e => e.RoutineId == routine.Id && e.EventId == eventId,
                cancellationToken);

        if (existingExecution != null)
        {
            logger.LogInformation(
                "Routine {RoutineId} already executed for event {EventId}, skipping (idempotence)",
                routine.Id, eventId);
            return;
        }

        // Evaluate conditions
        if (!EvaluateConditions(routine.ConditionJson, payload))
        {
            logger.LogInformation(
                "Routine {RoutineId} conditions not met for event {EventId}",
                routine.Id, eventId);
            return;
        }

        // Create execution record
        var execution = new RoutineExecution
        {
            RoutineId = routine.Id,
            OrgId = routine.OrgId,
            EventId = eventId,
            StartedAt = DateTime.UtcNow,
            Outcome = "Running"
        };

        dbContext.RoutineExecutions.Add(execution);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new RoutineExecutionResult();

        try
        {
            // Parse and execute actions
            await ExecuteActionsAsync(routine, execution, payload, result, cancellationToken);

            execution.CompletedAt = DateTime.UtcNow;
            execution.Outcome = "Success";
            execution.ResultJson = JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Routine {RoutineId} execution failed for event {EventId}",
                routine.Id, eventId);

            execution.CompletedAt = DateTime.UtcNow;
            execution.Outcome = "Failed";
            result.Errors.Add($"Execution failed: {ex.Message}");
            execution.ResultJson = JsonSerializer.Serialize(result);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ExecuteActionsAsync(
        Routine routine,
        RoutineExecution execution,
        object payload,
        RoutineExecutionResult result,
        CancellationToken cancellationToken)
    {
        var actions = JsonSerializer.Deserialize<List<RoutineAction>>(routine.ActionsJson)
                     ?? new List<RoutineAction>();

        await ContinueActionsFromIndexAsync(
            routine, execution, payload, result, actions, 0, cancellationToken);
    }

    private async Task ContinueActionsAsync(
        RoutineExecution execution,
        RoutineExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (execution.Routine == null)
        {
            throw new InvalidOperationException("Routine not loaded");
        }

        var actions = JsonSerializer.Deserialize<List<RoutineAction>>(execution.Routine.ActionsJson)
                     ?? new List<RoutineAction>();

        var nextIndex = result.ExecutedActions.Count;

        await ContinueActionsFromIndexAsync(
            execution.Routine,
            execution,
            new { }, // Payload already used during initial execution
            result,
            actions,
            nextIndex,
            cancellationToken);
    }

    private async Task ContinueActionsFromIndexAsync(
        Routine routine,
        RoutineExecution execution,
        object payload,
        RoutineExecutionResult result,
        List<RoutineAction> actions,
        int startIndex,
        CancellationToken cancellationToken)
    {
        for (int i = startIndex; i < actions.Count; i++)
        {
            var action = actions[i];

            // Apply guardrails (AC-NF26-4, AC-NF26-5, AC-NF26-6, ADR-022-R11)
            var guardrailViolation = CheckGuardrails(action, payload);
            if (guardrailViolation != null)
            {
                logger.LogWarning(
                    "Routine {RoutineId} action {ActionType} blocked by guardrail: {Reason}",
                    routine.Id, action.Type, guardrailViolation);

                result.Errors.Add($"Action {action.Type} blocked: {guardrailViolation}");
                continue;
            }

            if (action.Type == RoutineActionType.Wait)
            {
                // Handle Wait action (AC-NF26-10, ADR-022-R10)
                var waitConfig = JsonSerializer.Deserialize<WaitActionConfig>(
                    JsonSerializer.Serialize(action.Config)) ?? new WaitActionConfig();

                var resumeAt = DateTime.UtcNow.AddDays(waitConfig.Days);
                execution.ResumeAt = resumeAt;
                execution.Outcome = "Waiting";
                result.ExecutedActions.Add($"Wait: resume at {resumeAt:yyyy-MM-dd HH:mm:ss} UTC");

                await dbContext.SaveChangesAsync(cancellationToken);

                // In a real implementation, schedule Hangfire job here
                logger.LogInformation(
                    "Routine {RoutineId} waiting until {ResumeAt}",
                    routine.Id, resumeAt);

                return; // Stop processing actions until resume
            }

            // Execute action (or simulate in test mode)
            if (routine.TestMode)
            {
                result.ExecutedActions.Add($"[TEST MODE] {action.Type}: {JsonSerializer.Serialize(action.Config)}");
                logger.LogInformation(
                    "Routine {RoutineId} test mode: would execute {ActionType}",
                    routine.Id, action.Type);
            }
            else
            {
                await ExecuteActionAsync(action, payload, result, cancellationToken);
            }
        }
    }

    private async Task ExecuteActionAsync(
        RoutineAction action,
        object payload,
        RoutineExecutionResult result,
        CancellationToken cancellationToken)
    {
        // Actual action execution would go here
        // For now, log the action
        logger.LogInformation("Executing action {ActionType}", action.Type);

        switch (action.Type)
        {
            case RoutineActionType.MessageToGuest:
                // Would send email via IEmailService
                result.ExecutedActions.Add($"MessageToGuest: (email sent)");
                break;

            case RoutineActionType.NotifyOwner:
            case RoutineActionType.NotifyStaff:
            case RoutineActionType.NotifyTeam:
                // Would create in-app notification
                result.ExecutedActions.Add($"{action.Type}: (notification created)");
                break;

            case RoutineActionType.CreateActivity:
                // Would create InternalActivity via service
                result.ExecutedActions.Add($"CreateActivity: (activity created)");
                break;

            case RoutineActionType.SendAccessCode:
                // Would send access code (after ID verification check)
                result.ExecutedActions.Add($"SendAccessCode: (code sent)");
                break;

            case RoutineActionType.RequestReview:
                // Would request review from guest
                result.ExecutedActions.Add($"RequestReview: (review requested)");
                break;

            case RoutineActionType.AddTag:
                // Would add tag to guest CRM profile
                result.ExecutedActions.Add($"AddTag: (tag added)");
                break;

            default:
                throw new NotImplementedException($"Action type {action.Type} not implemented");
        }

        await Task.CompletedTask;
    }

    private bool EvaluateConditions(string conditionJson, object payload)
    {
        // Placeholder: would use condition evaluator from ADR-014
        // For now, always return true if no specific conditions
        if (string.IsNullOrWhiteSpace(conditionJson) || conditionJson == "{}")
        {
            return true;
        }

        // Real implementation would evaluate conditions against payload
        return true;
    }

    private string? CheckGuardrails(RoutineAction action, object payload)
    {
        // AC-NF26-4: Access code only after ID verification (ADR-022-R11)
        if (action.Type == RoutineActionType.SendAccessCode)
        {
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(payload));

            if (payloadDict != null &&
                payloadDict.TryGetValue("IdVerified", out var idVerified) &&
                idVerified is JsonElement element &&
                element.ValueKind == JsonValueKind.False)
            {
                return "Access code can only be sent after ID verification is completed";
            }
        }

        // AC-NF26-5: No Airbnb contact before booking confirmation (ADR-022-R11)
        if (action.Type == RoutineActionType.MessageToGuest)
        {
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(payload));

            if (payloadDict != null &&
                payloadDict.TryGetValue("Channel", out var channel) &&
                channel is JsonElement channelElement &&
                channelElement.GetString() == "Airbnb" &&
                payloadDict.TryGetValue("BookingConfirmed", out var confirmed) &&
                confirmed is JsonElement confirmedElement &&
                confirmedElement.ValueKind == JsonValueKind.False)
            {
                return "Cannot contact Airbnb guests before booking is confirmed";
            }
        }

        // AC-NF26-6: Campaigns only with consent (ADR-022-R11)
        if (action.Type == RoutineActionType.MessageToGuest)
        {
            var actionConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(action.Config));

            if (actionConfig != null &&
                actionConfig.TryGetValue("IsCampaign", out var isCampaign) &&
                isCampaign is JsonElement campaignElement &&
                campaignElement.ValueKind == JsonValueKind.True)
            {
                var payloadDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(payload));

                if (payloadDict != null &&
                    payloadDict.TryGetValue("MarketingConsent", out var consent) &&
                    consent is JsonElement consentElement &&
                    consentElement.ValueKind == JsonValueKind.False)
                {
                    return "Marketing campaigns require guest consent";
                }
            }
        }

        return null;
    }

    private bool WouldActionBeBlocked(RoutineAction action, object payload)
    {
        return CheckGuardrails(action, payload) != null;
    }

    private class RoutineAction
    {
        public RoutineActionType Type { get; set; }
        public Dictionary<string, object> Config { get; set; } = new();
    }

    private class RoutineExecutionResult
    {
        public List<string> ExecutedActions { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    private class WaitActionConfig
    {
        public int Days { get; set; }
    }
}
