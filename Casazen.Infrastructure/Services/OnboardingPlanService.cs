using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingPlanService" />
/// <remarks>
/// <para><b>Derived on read.</b> The state of every part is worked out on each read from what its evaluator counts in the stored facts, in the
/// precedence of <see cref="OnboardingPartStates"/>; the only stored state is the host's intent (<see cref="OnboardingPartState"/>), which never
/// makes a part done. The reads use <c>AsNoTracking</c> and never save.</para>
/// <para><b>Reach.</b> The properties the parts count are bound in SQL to the org and to what the scope reaches (<c>InScope</c>, AM-03), and only
/// the short-rent, active ones count. An open «Do it for me» request about a property the caller does not reach is not shown, nor one about the
/// whole org to a caller who does not reach the whole org (the rule of the "To do" list).</para>
/// <para><b>Mutations.</b> Every query names the org; the "To do" is raised and closed through <see cref="IInternalActivityService"/>, with codes
/// and ids only.</para>
/// </remarks>
public sealed class OnboardingPlanService(
    AppDbContext db,
    IEnumerable<IOnboardingPartEvaluator> evaluators,
    IInternalActivityService activities,
    TimeProvider timeProvider,
    ILogger<OnboardingPlanService> logger) : IOnboardingPlanService
{
    private static readonly OnboardingAssistStatus[] OpenAssistStatuses = [OnboardingAssistStatus.Requested, OnboardingAssistStatus.InProgress];

    public async Task<OnboardingPlan> GetPlanAsync(Guid orgId, HostScope scope, CancellationToken ct)
    {
        EnsureScopeOf(orgId, scope);

        var propertyIds = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .InScope(scope)
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var context = new OnboardingPartContext(scope, propertyIds);

        var stored = await db.OnboardingPartStates
            .AsNoTracking()
            .Where(s => s.OrgId == orgId)
            .ToDictionaryAsync(s => s.PartKey, ct);
        var assists = await OpenAssistsAsync(orgId, scope, ct);

        var states = new Dictionary<OnboardingPartKey, string>();
        var parts = new List<OnboardingPlanPart>(OnboardingPartCatalog.Parts.Count);
        foreach (var definition in OnboardingPartCatalog.Parts.OrderBy(part => part.Order))
        {
            var facts = await FactsOfAsync(definition.Key, context, ct);
            stored.TryGetValue(definition.Key, out var intent);
            var (state, reason) = StateOf(definition, facts, intent, states);
            states[definition.Key] = state;
            assists.TryGetValue(definition.Key, out var assist);

            parts.Add(new OnboardingPlanPart(
                definition.Key,
                definition.Code,
                definition.Order,
                state,
                reason,
                facts.Provided ? Math.Min(facts.Done, facts.Total) : 0,
                facts.Provided ? facts.Total : 0,
                [.. definition.Requires.Select(key => OnboardingPartCatalog.Of(key).Code)],
                OnboardingPartCatalog.IsDeferrable(definition.Key),
                definition.Needs,
                intent?.ActivityId is not null ? intent.RemindAt : null,
                assist,
                definition.LaunchTarget));
        }

        return new OnboardingPlan(
            parts,
            OnboardingNeedCodes.All,
            PathOf(states),
            states[OnboardingPartKey.Activation] == OnboardingPartStates.Done);
    }

    public async Task<OnboardingPath> GetPathAsync(Guid orgId, HostScope scope, CancellationToken ct) =>
        (await GetPlanAsync(orgId, scope, ct)).Path;

    public async Task<OnboardingPartChange> DeferAsync(
        Guid orgId,
        string userId,
        OnboardingPartKey key,
        DateTime? remindAt,
        CancellationToken ct)
    {
        var definition = DefinitionOf(orgId, userId, key);
        if (!OnboardingPartCatalog.IsDeferrable(key))
            throw new DomainRuleException(OnboardingPlanCodes.PartPrerequisite, "OnboardingPartPrerequisite");

        var now = Now();
        var remind = remindAt is { } chosen ? AsUtc(chosen) : DefaultRemindAt();
        if (remind <= now || remind > now.AddDays(OnboardingPlanLimits.MaxRemindDays))
            throw new DomainRuleException(OnboardingPlanCodes.RemindAtInvalid, "OnboardingRemindAtInvalid", OnboardingPlanLimits.MaxRemindDays);

        var row = await StoreAsync(orgId, userId, key, OnboardingPartStatus.Deferred, now, remind, ct);
        row.ActivityId = await RaiseTodoAsync(orgId, row, OnboardingPlanCodes.ReasonDeferred, remind, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Onboarding part {Part} of org {OrgId} deferred, reminder {RemindAt:O}, To do {ActivityId}",
            definition.Code, orgId, remind, row.ActivityId);
        return new OnboardingPartChange(key, definition.Code, OnboardingPartStates.Deferred, remind, row.ActivityId);
    }

    public async Task<OnboardingPartChange> ExitAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct)
    {
        var definition = DefinitionOf(orgId, userId, key);
        var now = Now();
        var remind = DefaultRemindAt();

        var row = await StoreAsync(orgId, userId, key, OnboardingPartStatus.Open, now, remind, ct);
        row.ActivityId = await RaiseTodoAsync(orgId, row, OnboardingPlanCodes.ReasonExit, remind, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Onboarding part {Part} of org {OrgId} left half way, reminder {RemindAt:O}, To do {ActivityId}",
            definition.Code, orgId, remind, row.ActivityId);
        return new OnboardingPartChange(key, definition.Code, OnboardingPartStates.Open, remind, row.ActivityId);
    }

    public async Task<OnboardingPartChange> ResumeAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct)
    {
        var definition = DefinitionOf(orgId, userId, key);
        var row = await db.OnboardingPartStates.FirstOrDefaultAsync(s => s.OrgId == orgId && s.PartKey == key, ct);
        if (row is null)
            return new OnboardingPartChange(key, definition.Code, OnboardingPartStates.Open, null, null);

        if (row.ActivityId is { } activityId && await IsOpenTodoAsync(orgId, activityId, ct))
            await activities.CloseAsync(activityId, userId, ct);

        row.Status = OnboardingPartStatus.Open;
        row.DeferredAt = null;
        row.RemindAt = null;
        row.ActivityId = null;
        row.UpdatedAt = Now();
        row.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Onboarding part {Part} of org {OrgId} resumed", definition.Code, orgId);
        return new OnboardingPartChange(key, definition.Code, OnboardingPartStates.Open, null, null);
    }

    /// <summary>
    /// The state and the reason of a part, in the precedence of <see cref="OnboardingPartStates"/>: not available, done, deferred, blocked (a part
    /// it requires is not done, <paramref name="earlier"/> holds the states of the parts before it), open.
    /// </summary>
    internal static (string State, string? Reason) StateOf(
        OnboardingPartDefinition definition,
        PartFacts facts,
        OnboardingPartState? intent,
        IReadOnlyDictionary<OnboardingPartKey, string> earlier)
    {
        if (!facts.Provided)
            return (OnboardingPartStates.NotAvailable, facts.Reason ?? OnboardingPartReasons.NotProvided);
        if (facts.IsComplete)
            return (OnboardingPartStates.Done, null);
        if (intent is { Status: OnboardingPartStatus.Deferred })
            return (OnboardingPartStates.Deferred, facts.Reason);
        if (definition.Requires.Any(required => !earlier.TryGetValue(required, out var state) || state != OnboardingPartStates.Done))
            return (OnboardingPartStates.Blocked, OnboardingPartReasons.RequiredPartNotDone);

        return (OnboardingPartStates.Open, facts.Reason);
    }

    /// <summary>
    /// The path of UX-10 from the states of the parts: a step is done when all its parts are, not available when all its parts are, and visible
    /// only while there is something to do in it; the next step is the first visible one.
    /// </summary>
    internal static OnboardingPath PathOf(IReadOnlyDictionary<OnboardingPartKey, string> states)
    {
        var steps = new List<OnboardingPathStep>(OnboardingPartCatalog.StartWithCasazenSteps.Count);
        foreach (var step in OnboardingPartCatalog.StartWithCasazenSteps.OrderBy(step => step.Order))
        {
            var partStates = step.Parts.Select(part => states[part]).ToList();
            var state = partStates.All(s => s == OnboardingPartStates.Done) ? OnboardingPartStates.Done
                : partStates.All(s => s == OnboardingPartStates.NotAvailable) ? OnboardingPartStates.NotAvailable
                : partStates.First(s => s is not (OnboardingPartStates.Done or OnboardingPartStates.NotAvailable));
            var visible = state is not (OnboardingPartStates.Done or OnboardingPartStates.NotAvailable);
            steps.Add(new OnboardingPathStep(
                step.Code,
                step.Order,
                [.. step.Parts.Select(part => OnboardingPartCatalog.Of(part).Code)],
                state,
                visible));
        }

        return new OnboardingPath(OnboardingPartCatalog.StartWithCasazenPath, steps, steps.FirstOrDefault(step => step.Visible)?.Code);
    }

    /// <summary>What the evaluator of the part with the highest order counts; with none, the part is not provided (never presumed done).</summary>
    private async Task<PartFacts> FactsOfAsync(OnboardingPartKey key, OnboardingPartContext context, CancellationToken ct)
    {
        var evaluator = evaluators
            .Where(e => e.Part == key)
            .OrderByDescending(e => e.Order)
            .FirstOrDefault();
        return evaluator is null
            ? PartFacts.NotAvailable(OnboardingPartReasons.NotProvided)
            : await evaluator.EvaluateAsync(context, ct);
    }

    /// <summary>The open «Do it for me» request of each part the caller may see: the most recent one.</summary>
    private async Task<IReadOnlyDictionary<OnboardingPartKey, OnboardingAssistInfo>> OpenAssistsAsync(
        Guid orgId,
        HostScope scope,
        CancellationToken ct)
    {
        var reachable = db.Properties.Where(p => p.OrgId == orgId).InScope(scope).Select(p => p.Id);
        var orgWide = scope.IsOrgWide;
        var rows = await db.OnboardingAssistRequests
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && OpenAssistStatuses.Contains(r.Status))
            .Where(r => (r.PropertyId == null && orgWide) || (r.PropertyId != null && reachable.Contains(r.PropertyId.Value)))
            .Select(r => new { r.Id, r.PartKey, r.Status, r.Context, r.RequestedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.PartKey)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var latest = group.OrderByDescending(r => r.RequestedAt).ThenBy(r => r.Id).First();
                    return new OnboardingAssistInfo(latest.Id, latest.Status, latest.Context, latest.RequestedAt);
                });
    }

    /// <summary>
    /// The row of the part with the host's intent, created the first time. Two first writes at once: the second loses the race on the unique
    /// index (<see cref="OnboardingPartState.OrgPartIndexName"/>) and writes into the first's row.
    /// </summary>
    private async Task<OnboardingPartState> StoreAsync(
        Guid orgId,
        string userId,
        OnboardingPartKey key,
        OnboardingPartStatus status,
        DateTime now,
        DateTime remindAt,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = await db.OnboardingPartStates.FirstOrDefaultAsync(s => s.OrgId == orgId && s.PartKey == key, ct);
            if (row is null)
            {
                row = new OnboardingPartState { OrgId = orgId, PartKey = key };
                db.OnboardingPartStates.Add(row);
            }

            row.Status = status;
            row.DeferredAt = status == OnboardingPartStatus.Deferred ? now : null;
            row.RemindAt = remindAt;
            row.UpdatedAt = now;
            row.UpdatedByUserId = userId;

            try
            {
                // Saved before the "To do" names it.
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException ex) when (attempt == 1 && OrgTeamAccess.IsUniqueViolation(ex, OnboardingPartState.OrgPartIndexName))
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// The one open "To do" of the part, due at <paramref name="deadline"/> with <paramref name="reason"/>: the one it already has when it says the
    /// same, otherwise that one is closed and a new one raised. Codes and ids only: no property, no person.
    /// </summary>
    private async Task<Guid> RaiseTodoAsync(Guid orgId, OnboardingPartState row, string reason, DateTime deadline, CancellationToken ct)
    {
        if (row.ActivityId is { } previous)
        {
            var open = await db.InternalActivities
                .AsNoTracking()
                .Where(a => a.Id == previous && a.OrgId == orgId && a.Action != null && a.CompletedAt == null)
                .Select(a => new { a.Reason, a.Deadline })
                .FirstOrDefaultAsync(ct);
            if (open is not null && open.Reason == reason && open.Deadline == deadline)
                return previous;
            if (open is not null)
                await activities.CloseAsync(previous, InternalActivity.ClosedBySystem, ct);
        }

        return await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.WizardResume,
                Action = ActivityAction.ResumeWizard,
                EntityType = OnboardingPlanCodes.TodoEntityType,
                EntityId = row.Id,
                Urgency = ActivityUrgency.Medium,
                OrgId = orgId,
                Deadline = deadline,
                Reason = reason,
            },
            ct);
    }

    private Task<bool> IsOpenTodoAsync(Guid orgId, Guid activityId, CancellationToken ct) =>
        db.InternalActivities
            .AsNoTracking()
            .AnyAsync(a => a.Id == activityId && a.OrgId == orgId && a.Action != null && a.CompletedAt == null, ct);

    private static OnboardingPartDefinition DefinitionOf(Guid orgId, string userId, OnboardingPartKey key)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("The plan belongs to an org.", nameof(orgId));
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > OnboardingPartState.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(userId));

        return OnboardingPartCatalog.Of(key);
    }

    private static void EnsureScopeOf(Guid orgId, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.OrgId != orgId)
            throw new ArgumentException("The scope is not one of the org of the plan.", nameof(scope));
    }

    /// <summary>Tomorrow at 09:00 in Rome, as UTC: the default reminder of a deferral and of an exit.</summary>
    private DateTime DefaultRemindAt() =>
        RomeCalendar.ToUtc(timeProvider.TodayInRomeAsDateOnly().AddDays(1), new TimeOnly(OnboardingPlanLimits.DefaultRemindHour, 0));

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>A reminder of the client as UTC: an instant with an offset is converted, one without is taken as UTC.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
