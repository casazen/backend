using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Keeps the "To do" list in step with the activation of the flows (NF-106, NF-108, ADR-023-R12, E10/F01/US01/T04), every hour at minute 20 UTC,
/// for each org with an Octorate connection in this environment:
/// <list type="bullet">
/// <item>ONE open "To do" for each flow that has properties «to complete» (<see cref="ActivityType.WizardResume"/>, action
/// <see cref="ActivityAction.ResumeWizard"/>, entity <c>OnboardingFlow</c> with the id <see cref="OnboardingFlowTodo.EntityIdOf"/>, reason
/// <c>flow_incomplete</c>), closed when none is;</item>
/// <item>ONE <see cref="ActivityType.ReconnectChannelManager"/> "To do" of high urgency while the connection is disconnected or in error (entity
/// <c>OctorateConnection</c>, reason <c>connection_lost</c>), closed when it is connected again.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Behind the flag.</b> Scheduled only while <c>Features:OctorateOnboarding</c> is on (removed from the schedule otherwise), and a run
/// that starts with it off writes nothing.</para>
/// <para><b>Idempotent.</b> The "To do" service keeps one open item for a type and an entity: a run, a retry or a manual trigger never adds a
/// second one. The status of the flows is read over the whole org (a system read: the reach of a person plays no part), from the stored facts only:
/// nothing calls Octorate. Codes and ids only: never a name or a person.</para>
/// <para><b>Failures.</b> An org that fails is logged with its id and the type of the error and the other orgs go on; the run then fails, so
/// Hangfire retries it (nothing is written twice).</para>
/// </remarks>
public sealed class OnboardingFlowTodoSyncJob(
    AppDbContext db,
    IOnboardingFlowStatusService flowStatus,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    ILogger<OnboardingFlowTodoSyncJob> logger)
{
    public const string RecurringJobId = "onboarding-flow-todo-sync";

    /// <summary>Every hour at minute 20, UTC.</summary>
    public const string Cron = "20 * * * *";

    private static readonly OnboardingFlow[] AllFlows = [.. Enum.GetValues<OnboardingFlow>().OrderBy(flow => (int)flow)];

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateOnboarding))
        {
            logger.LogInformation("Onboarding flow To do sync skipped: Features:OctorateOnboarding is off");
            return;
        }

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connections = await db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.Environment == environment)
            .OrderBy(c => c.OrgId)
            .Select(c => new { c.Id, c.OrgId, c.Status })
            .ToListAsync(cancellationToken);

        var failed = 0;
        foreach (var connection in connections)
        {
            try
            {
                await SyncFlowsAsync(connection.OrgId, cancellationToken);
                await SyncConnectionAsync(connection.OrgId, connection.Id, connection.Status, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                db.ChangeTracker.Clear();
                logger.LogError("Onboarding flow To do sync failed for org {OrgId} ({ExceptionType})", connection.OrgId, ex.GetType().Name);
            }
        }

        logger.LogInformation("Onboarding flow To do sync: {Orgs} orgs, {Failed} failed", connections.Count, failed);
        if (failed > 0)
            throw new InvalidOperationException($"The onboarding flow To do sync failed for {failed} of {connections.Count} orgs.");
    }

    /// <summary>One open "To do" for each flow of the org with properties to complete; the one of a flow with none is closed.</summary>
    private async Task SyncFlowsAsync(Guid orgId, CancellationToken ct)
    {
        var statuses = await flowStatus.GetStatusesAsync(new HostScope(orgId), null, AllFlows, ct);
        foreach (var flow in AllFlows)
        {
            var entityId = OnboardingFlowTodo.EntityIdOf(orgId, flow);
            if (statuses.Any(status => status.Flow == flow && status.State == OnboardingFlowStates.ToComplete))
            {
                await activities.CreateAsync(
                    new CreateTodoRequest
                    {
                        Type = ActivityType.WizardResume,
                        Action = ActivityAction.ResumeWizard,
                        EntityType = OnboardingActivationCodes.FlowTodoEntityType,
                        EntityId = entityId,
                        Urgency = ActivityUrgency.Medium,
                        OrgId = orgId,
                        Reason = OnboardingActivationCodes.ReasonFlowIncomplete,
                    },
                    ct);
            }
            else
            {
                await CloseOpenAsync(orgId, ActivityType.WizardResume, OnboardingActivationCodes.FlowTodoEntityType, entityId, ct);
            }
        }
    }

    /// <summary>The high-urgency "To do" of a connection that is disconnected or in error (ADR-023-R12), closed once it is connected again.</summary>
    private async Task SyncConnectionAsync(Guid orgId, Guid connectionId, OctorateConnectionStatus status, CancellationToken ct)
    {
        if (status == OctorateConnectionStatus.Connected)
        {
            await CloseOpenAsync(orgId, ActivityType.ReconnectChannelManager, OnboardingActivationCodes.ConnectionTodoEntityType, connectionId, ct);
            return;
        }

        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.ReconnectChannelManager,
                Action = ActivityAction.ReconnectChannel,
                EntityType = OnboardingActivationCodes.ConnectionTodoEntityType,
                EntityId = connectionId,
                Urgency = ActivityUrgency.High,
                OrgId = orgId,
                Reason = OnboardingActivationCodes.ReasonConnectionLost,
            },
            ct);
    }

    private async Task CloseOpenAsync(Guid orgId, ActivityType type, string entityType, Guid entityId, CancellationToken ct)
    {
        var name = type.ToString();
        var open = await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == name
                        && a.EntityType == entityType
                        && a.EntityId == entityId)
            .Select(a => a.Id)
            .ToListAsync(ct);
        foreach (var id in open)
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);
    }
}

/// <summary>The id of the "To do" of a flow of an org (E10/F01/US01/T04): stable, so the job finds the item it raised before.</summary>
public static class OnboardingFlowTodo
{
    /// <summary>
    /// The first 16 bytes of the SHA-256 of <c>onboarding-flow:{orgId}:{flow}</c> (the org as <c>D</c>, lowercase with hyphens; the flow by its member
    /// name), as a <see cref="Guid"/>.
    /// </summary>
    public static Guid EntityIdOf(Guid orgId, OnboardingFlow flow)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"onboarding-flow:{orgId:D}:{flow}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
