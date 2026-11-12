using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingFlowActivationService" />
/// <remarks>
/// <para><b>The prerequisites, again, on the server.</b> Every call reads the status of the flow on the properties through
/// <see cref="IOnboardingFlowStatusService.GetStatusesAsync"/>: what the client saw a minute ago is never trusted. Only a property whose status is
/// «to complete» for the sole reason <see cref="OnboardingFlowReasons.FlowNotActivated"/> (every prerequisite holds) is activated.</para>
/// <para><b>One unit of work.</b> The rows and their outbox events are saved by one <c>SaveChangesAsync</c>, in one transaction (ADR-022-R1): an
/// event never exists without its row, nor a row without its event. A parallel activation of the same property and flow that saved first makes
/// this one lose the race on the unique index (<see cref="PropertyFlowActivation.PropertyFlowIndexName"/>): nothing of this call is saved, the
/// unit of work is cleared and the call decides again once.</para>
/// </remarks>
public sealed class OnboardingFlowActivationService(
    AppDbContext db,
    IOnboardingFlowStatusService flowStatus,
    IDomainEventRecorder events,
    TimeProvider timeProvider,
    ILogger<OnboardingFlowActivationService> logger) : IOnboardingFlowActivationService
{
    public async Task<IReadOnlyList<FlowActivationOutcome>> ActivateAsync(
        HostScope scope,
        string actorUserId,
        OnboardingFlow flow,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        var ids = Validate(scope, actorUserId, flow, propertyIds);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ActivateOnceAsync(scope, actorUserId, flow, ids, ct);
            }
            catch (DbUpdateException ex) when (attempt == 1 && OrgTeamAccess.IsUniqueViolation(ex, PropertyFlowActivation.PropertyFlowIndexName))
            {
                // Nothing of this call was saved: the rows and the events of the failed save go, and the call reads again.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<FlowActivationOutcome>> DeactivateAsync(
        HostScope scope,
        string actorUserId,
        OnboardingFlow flow,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        var ids = Validate(scope, actorUserId, flow, propertyIds);
        var reachable = (await db.Properties
                .AsNoTracking()
                .Where(p => p.OrgId == scope.OrgId && ids.Contains(p.Id))
                .InScope(scope)
                .Select(p => p.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var rows = await db.PropertyFlowActivations
            .Where(a => a.OrgId == scope.OrgId && a.Flow == flow && ids.Contains(a.PropertyId))
            .ToDictionaryAsync(a => a.PropertyId, ct);

        var now = Now();
        var outcomes = new List<FlowActivationOutcome>(ids.Count);
        foreach (var id in ids)
        {
            if (!reachable.Contains(id))
            {
                outcomes.Add(Outcome(id, FlowActivationOutcomes.NotFound));
                continue;
            }

            if (!rows.TryGetValue(id, out var row) || row.DeactivatedAt is not null)
            {
                outcomes.Add(Outcome(id, FlowActivationOutcomes.AlreadyInactive));
                continue;
            }

            row.DeactivatedAt = now;
            row.DeactivatedByUserId = actorUserId;
            events.Record(
                DomainEventType.OnboardingFlowDeactivated,
                id,
                new OnboardingFlowEventPayload(id, flow.ToString(), row.Id),
                DedupKey("deactivated", id, flow, now),
                scope.OrgId);
            outcomes.Add(Outcome(id, FlowActivationOutcomes.Deactivated));
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        Log("deactivated", scope.OrgId, flow, outcomes);
        return outcomes;
    }

    private async Task<IReadOnlyList<FlowActivationOutcome>> ActivateOnceAsync(
        HostScope scope,
        string actorUserId,
        OnboardingFlow flow,
        IReadOnlyList<Guid> ids,
        CancellationToken ct)
    {
        var statuses = (await flowStatus.GetStatusesAsync(scope, ids, [flow], ct)).ToDictionary(status => status.PropertyId);
        var rows = await db.PropertyFlowActivations
            .Where(a => a.OrgId == scope.OrgId && a.Flow == flow && ids.Contains(a.PropertyId))
            .ToDictionaryAsync(a => a.PropertyId, ct);

        var now = Now();
        var outcomes = new List<FlowActivationOutcome>(ids.Count);
        foreach (var id in ids)
        {
            if (!statuses.TryGetValue(id, out var status))
            {
                // Another org's, deleted, or outside the reach: the same answer as an id that does not exist.
                outcomes.Add(Outcome(id, FlowActivationOutcomes.NotFound));
                continue;
            }

            if (status.State == OnboardingFlowStates.NotAvailable)
            {
                outcomes.Add(new FlowActivationOutcome(id, FlowActivationOutcomes.NotAvailable, [], status.Reason));
                continue;
            }

            if (status.State == OnboardingFlowStates.Active)
            {
                outcomes.Add(Outcome(id, FlowActivationOutcomes.AlreadyActive));
                continue;
            }

            var unmet = status.Prerequisites.Where(p => !p.Met).Select(p => p.Key).ToList();
            if (unmet.Count > 0 || status.Reason != OnboardingFlowReasons.FlowNotActivated)
            {
                outcomes.Add(new FlowActivationOutcome(id, FlowActivationOutcomes.PrerequisitesNotMet, unmet, status.Reason));
                continue;
            }

            if (!rows.TryGetValue(id, out var row))
            {
                row = new PropertyFlowActivation { OrgId = scope.OrgId, PropertyId = id, Flow = flow };
                db.PropertyFlowActivations.Add(row);
            }

            // A reactivation reuses the row: it says who activated first only through the history of the events.
            row.ActivatedAt = now;
            row.ActivatedByUserId = actorUserId;
            row.DeactivatedAt = null;
            row.DeactivatedByUserId = null;
            events.Record(
                DomainEventType.OnboardingFlowActivated,
                id,
                new OnboardingFlowEventPayload(id, flow.ToString(), row.Id),
                DedupKey("activated", id, flow, now),
                scope.OrgId);
            outcomes.Add(Outcome(id, FlowActivationOutcomes.Activated));
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        Log("activated", scope.OrgId, flow, outcomes);
        return outcomes;
    }

    /// <summary>The distinct ids of the call, in the order given; 422 for an empty list (never «all») or one beyond the cap.</summary>
    private static List<Guid> Validate(HostScope scope, string actorUserId, OnboardingFlow flow, IReadOnlyCollection<Guid> propertyIds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (actorUserId.Length > PropertyFlowActivation.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(actorUserId));
        if (!Enum.IsDefined(flow))
            throw new ArgumentOutOfRangeException(nameof(flow), flow, "Not an onboarding flow.");

        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainRuleException(OnboardingActivationCodes.PropertyIdsEmpty, "OnboardingPropertyIdsEmpty");
        if (ids.Count > OnboardingActivationLimits.MaxPropertiesPerCall)
        {
            throw new DomainRuleException(
                OnboardingActivationCodes.PropertyIdsTooMany, "OnboardingPropertyIdsTooMany", OnboardingActivationLimits.MaxPropertiesPerCall);
        }

        return ids;
    }

    /// <summary>The key of an event: one for each change of a property and flow (a later activation is a new event).</summary>
    private static string DedupKey(string change, Guid propertyId, OnboardingFlow flow, DateTime at) =>
        string.Create(CultureInfo.InvariantCulture, $"onboarding-flow-{change}:{propertyId:N}:{(int)flow}:{at.Ticks}");

    private static FlowActivationOutcome Outcome(Guid propertyId, string outcome) => new(propertyId, outcome, [], null);

    private void Log(string change, Guid orgId, OnboardingFlow flow, IReadOnlyList<FlowActivationOutcome> outcomes)
    {
        // Ids and counts only.
        logger.LogInformation(
            "Onboarding flow {Flow} of org {OrgId} {Change}: {Outcomes}",
            flow,
            orgId,
            change,
            string.Join(", ", outcomes.GroupBy(o => o.Outcome).Select(g => $"{g.Key} {g.Count()}")));
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}

/// <inheritdoc cref="IFlowActivationReader" />
public sealed class FlowActivationReader(AppDbContext db, IOnboardingFlowStatusService flowStatus) : IFlowActivationReader
{
    public async Task<bool> IsActiveAsync(Guid propertyId, OnboardingFlow flow, CancellationToken ct) =>
        (await GetActiveFlowsAsync([propertyId], ct)).TryGetValue(propertyId, out var flows) && flows.Contains(flow);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<OnboardingFlow>>> GetActiveFlowsAsync(
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var active = new Dictionary<Guid, IReadOnlySet<OnboardingFlow>>();
        var ids = propertyIds.Distinct().ToList();
        if (ids.Count == 0)
            return active;

        // Only a property with an activation standing can have an active flow: the others are not evaluated.
        var standing = await db.PropertyFlowActivations
            .AsNoTracking()
            .Where(a => ids.Contains(a.PropertyId) && a.DeactivatedAt == null)
            .Select(a => new { a.OrgId, a.PropertyId, a.Flow })
            .ToListAsync(ct);

        foreach (var org in standing.GroupBy(a => a.OrgId))
        {
            // A system read of the org's own properties (the whole org): the reach of a person plays no part in whether a flow runs.
            var statuses = await flowStatus.GetStatusesAsync(
                new HostScope(org.Key),
                [.. org.Select(a => a.PropertyId).Distinct()],
                [.. org.Select(a => a.Flow).Distinct()],
                ct);
            foreach (var property in statuses.Where(s => s.State == OnboardingFlowStates.Active).GroupBy(s => s.PropertyId))
                active[property.Key] = property.Select(s => s.Flow).ToHashSet();
        }

        return active;
    }
}
