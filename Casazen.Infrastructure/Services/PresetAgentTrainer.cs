using System.Diagnostics;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed record AgentExample(string Channel, string Status, int Nights, int Guests, bool NoShow, bool Execute)
{
    internal AgentStay Stay => new(Channel, Status, Nights, Guests, NoShow);
}

public sealed record AgentTrainingDraft(string? Guidance, AgentExample[]? Training, AgentExample[]? Evaluation);
public sealed record AgentScore(bool? Execute, int Tokens, long Milliseconds, string? Error);
public sealed record AgentEvaluation(AgentExample Example, AgentScore Current, AgentScore Candidate);
public sealed record TrainedAgent(int Version, string Guidance, AgentExample[] Examples);
public sealed record AgentTrainingState(int Revision = 0, TrainedAgent? Active = null, TrainedAgent? Previous = null,
    TrainedAgent? Candidate = null, AgentTrainingDraft? Draft = null, AgentEvaluation[]? Results = null, int LastVersion = 0);

public sealed class PresetAgentTrainer(AppDbContext db, IServiceScopeFactory scopes, ILogger<PresetAgentTrainer> logger)
{
    private static AgentTrainingState Read(PresetAgentTraining row) =>
        JsonSerializer.Deserialize<AgentTrainingState>(row.StateJson)! with { Revision = row.Revision };

    internal static async Task<TrainedAgent?> ActiveAsync(AppDbContext db, PresetAgent preset, CancellationToken ct)
    {
        var row = await db.PresetAgentTraining.AsNoTracking().SingleOrDefaultAsync(p => p.Key == preset.Key, ct);
        return row is null ? null : Read(row).Active;
    }

    public async Task<AgentTrainingState> GetAsync(string key, CancellationToken ct) => Read(await GetRowAsync(key, ct));

    public async Task<AgentTrainingState> TrainAsync(string key, AgentTrainingDraft draft, int revision, CancellationToken ct)
    {
        var preset = PresetAgentCatalog.All.FirstOrDefault(p => p.Key == key) ?? throw Missing();
        var training = draft.Training;
        var evaluation = draft.Evaluation;
        if (training is not { Length: >= 2 and <= 40 } || evaluation is not { Length: >= 2 and <= 6 } ||
            training.Concat(evaluation).Any(example => example is null || !Valid(example)) ||
            training.Select(e => e.Execute).Distinct().Count() != 2 || evaluation.Select(e => e.Execute).Distinct().Count() != 2 ||
            training.Concat(evaluation).Select(e => e.Stay).Distinct().Count() != training.Length + evaluation.Length ||
            draft.Guidance is { Length: > 800 })
            throw new DomainRuleException("agent_training_examples", "AgentTrainingExamples");

        var row = await GetRowAsync(key, ct);
        var state = Read(row);
        CheckRevision(state, revision);
        var candidate = new TrainedAgent(state.LastVersion + 1,
            ServiceRequestAssistPrompt.CleanText(draft.Guidance ?? ""), training);
        RoutineAction.TryParseList(JsonSerializer.Serialize(new[] { new { type = preset.Action.ToString() } }), out var actions);
        var results = new AgentEvaluation[evaluation.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, evaluation.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = 3,
            CancellationToken = ct,
        }, async (index, token) =>
        {
            var example = evaluation[index];
            var current = ScoreAsync(example, state.Active, token);
            var proposed = ScoreAsync(example, candidate, token);
            await Task.WhenAll(current, proposed);
            results[index] = new AgentEvaluation(example, await current, await proposed);
        });
        return await SaveAsync(row, state with
        {
            Candidate = candidate, Draft = draft, Results = results, LastVersion = candidate.Version,
        }, ct);

        async Task<AgentScore> ScoreAsync(AgentExample example, TrainedAgent? profile, CancellationToken token)
        {
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider.GetRequiredService<IAiProvider>();
            var started = Stopwatch.GetTimestamp();
            var plan = await RoutineAgentPlanner.PlanAsync(provider, logger, preset.ExampleRoutine(), actions, 0, example.Stay, [], token, profile);
            return new AgentScore(plan.Error is null ? plan.Selected.Contains(0) : null, plan.Tokens,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, plan.Error);
        }
    }

    public async Task<AgentTrainingState> ActivateAsync(string key, int revision, bool restore, CancellationToken ct)
    {
        var row = await GetRowAsync(key, ct);
        var state = Read(row);
        CheckRevision(state, revision);
        if (!restore && (state.Candidate is null || state.Results is null ||
            state.Results.Any(r => r.Candidate.Error is not null) ||
            state.Results.Count(r => r.Candidate.Execute == r.Example.Execute) < state.Results.Count(r => r.Current.Execute == r.Example.Execute)))
            throw new DomainConflictException("agent_training_regression", "AgentTrainingRegression");
        if (restore && state.Active is null && state.Previous is null)
            throw new DomainConflictException("agent_training_restore", "AgentTrainingRestore");

        return await SaveAsync(row, state with
        {
            Active = restore ? state.Previous : state.Candidate, Previous = state.Active, Candidate = null,
        }, ct);
    }

    // A nearest-neighbour classifier trained on labelled stays. Only the three closest examples go into the AI request.
    internal static AgentExample[] Neighbours(TrainedAgent profile, AgentStay stay) =>
        profile.Examples.OrderBy(example =>
            (example.Status == stay.Status ? 0 : 5) + (example.NoShow == stay.NoShow ? 0 : 5) +
            (example.Channel == stay.Channel ? 0 : 3) + Math.Min(Math.Abs(example.Nights - stay.Nights), 5) +
            Math.Min(Math.Abs(example.Guests - stay.Guests), 3)).Take(3).ToArray();

    private static bool Valid(AgentExample example) =>
        Enum.GetNames<BookingSource>().Contains(example.Channel) && Enum.GetNames<BookingStatus>().Contains(example.Status) &&
        example.Nights is >= 1 and <= 365 && example.Guests is >= 1 and <= 100;

    private async Task<PresetAgentTraining> GetRowAsync(string key, CancellationToken ct) =>
        await db.PresetAgentTraining.SingleOrDefaultAsync(p => p.Key == key, ct) ?? throw Missing();

    private static NotFoundException Missing() => new("Unknown preset agent") { Code = "agent_preset_missing" };

    private static void CheckRevision(AgentTrainingState state, int revision)
    {
        if (state.Revision != revision) throw Conflict();
    }

    private async Task<AgentTrainingState> SaveAsync(PresetAgentTraining row, AgentTrainingState state, CancellationToken ct)
    {
        state = state with { Revision = ++row.Revision };
        row.StateJson = JsonSerializer.Serialize(state);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict();
        }
        return state;
    }

    private static DomainConflictException Conflict() => new("agent_training_changed", "AgentTrainingChanged");
}
