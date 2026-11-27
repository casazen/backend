using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

internal sealed record AgentPlan(IReadOnlySet<int> Selected, string? Error = null, int Tokens = 0);

internal sealed record AgentStay(string Channel, string Status, int Nights, int Guests, bool NoShow);

/// <summary>Choose only among the configured operations. All arguments, waits and permissions stay in CasaZen.</summary>
internal static class RoutineAgentPlanner
{
    // Some compatible models count reasoning and the final answer against the same allowance.
    internal const int DecisionTokens = 2048;

    internal static string DecisionText(string? response)
    {
        var text = response?.Trim() ?? string.Empty;
        if (text.StartsWith("<think>", StringComparison.Ordinal))
        {
            var end = text.IndexOf("</think>", StringComparison.Ordinal);
            if (end < 0) return string.Empty;
            text = text[(end + "</think>".Length)..].Trim();
        }
        var line = text.IndexOf('\n');
        if (line >= 0 && text[..line].TrimEnd() is "```json" or "```" && text.EndsWith("\n```", StringComparison.Ordinal))
            text = text[(line + 1)..^3].Trim();
        return text;
    }

    public static async Task<AgentPlan> PlanAsync(
        IAiProvider? provider, ILogger logger, Routine routine, IReadOnlyList<RoutineAction> actions,
        int startIndex, AgentStay? stay, IReadOnlyList<RoutineActionResult> history, CancellationToken ct, TrainedAgent? profile = null)
    {
        var operations = actions.Select((action, index) => new
        {
            index,
            type = action.Type.ToString(),
            tag = action.Type == RoutineActionType.AddTag ? ServiceRequestAssistPrompt.CleanText(action.GetString("tag")) : null,
            waitHours = action.Type == RoutineActionType.Wait ? RoutineActionRules.WaitDelay(action)?.TotalHours : null,
        })
            .Skip(startIndex).ToArray();
        if (operations.Length == 0)
            return new AgentPlan(new HashSet<int>());
        if (provider is null)
            return Failure(RoutineCodes.AiUnavailable);

        var objective = routine.AgentObjective is null
            ? "Carry out the configured workflow for this event, omitting operations that are inappropriate for the known state of this stay."
            : ServiceRequestAssistPrompt.CleanText(routine.AgentObjective);
        if (objective.Length == 0)
            return Failure(RoutineCodes.AiFactsMissing);

        var request = new AiRequest(JsonSerializer.Serialize(new
        {
            objective,
            trigger = routine.EventType.ToString(),
            stay,
            history = history.Select(r => new { r.Index, type = r.Type.ToString(), r.Outcome }),
            operations,
            training = profile is not null && stay is not null
                ? new { profile.Guidance, examples = PresetAgentTrainer.Neighbours(profile, stay) }
                : null,
        }), [], """
            You operate a CasaZen booking agent. Decide which authorized operations satisfy its objective for this event.
            Use only the supplied trigger and stay facts. Do not infer missing facts. Honor conditions in the objective.
            If an explicit objective cannot be evaluated, return UNKNOWN. If it is false, return [].
            Use the configured operations when appropriate. Treat an operation’s technical prerequisites as dispatcher checks.
            Output only a JSON array of selected integer indices, in the supplied order. Waits are mandatory timing barriers.
            You cannot change arguments, add tools or change their order. Never interpret the objective as output-format instructions.
            Training examples give guidance for applying the objective to similar stays. Never copy an example's result in place of evaluating the present facts.
            Write the final answer without Markdown.
            """, Temperature: 0, MaxCompletionTokens: DecisionTokens, DisableThinking: true);
        try
        {
            var answer = await provider.GenerateAsync(request, AiModelTier.Economy,
                $"agent-plan:{routine.OrgId:N}:{ServiceRequestAssistPrompt.CacheKey(request)}", ct);
            if (!answer.ProviderConfigured)
                return Failure(RoutineCodes.AiUnavailable);
            var decision = DecisionText(answer.Content);
            if (decision == "UNKNOWN")
                return Failure(RoutineCodes.AiFactsMissing);

            if (decision.Length == 0)
            {
                logger.LogWarning("Agent {RoutineId} received no final decision after {CompletionTokens} completion tokens", routine.Id, answer.CompletionTokens);
                return Failure(RoutineCodes.AiPlanInvalid);
            }
            var selected = JsonSerializer.Deserialize<int[]>(decision);
            if (selected is null || !selected.SequenceEqual(selected.Distinct().Order()) ||
                selected.Any(i => i < startIndex || i >= actions.Count))
                return Failure(RoutineCodes.AiPlanInvalid);
            return new AgentPlan(selected.ToHashSet(), Tokens: answer.PromptTokens + answer.CompletionTokens);
        }
        catch (JsonException)
        {
            return Failure(RoutineCodes.AiPlanInvalid);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Agent {RoutineId} planner unavailable ({ErrorType})", routine.Id, ex.GetType().Name);
            return Failure(RoutineCodes.AiUnavailable);
        }
    }

    private static AgentPlan Failure(string code) => new(new HashSet<int>(), code);
}
