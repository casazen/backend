using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Reconciles onboarding reminders before the dashboard is read. The hourly Octorate job is not enough: an organization
/// may have no connection yet, and completing a wizard must clear its earlier attempts immediately. No external API is called.
/// </summary>
public sealed class OnboardingTodoReconciler(
    AppDbContext db,
    IOnboardingPlanService plans,
    IOnboardingFlowStatusService flows,
    IInternalActivityService activities)
{
    public async Task ReconcileAsync(HostScope scope, CancellationToken ct)
    {
        var orgId = scope.OrgId;
        var rows = await db.InternalActivities.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.ActivityType == nameof(ActivityType.WizardResume)
                        && a.Action == ActivityAction.ResumeWizard && a.CompletedAt == null)
            .Where(a => a.PropertyId == null || a.Property != null)
            .InScope(scope)
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return;

        // Completion is the org's fact, never inferred from the subset of properties a collaborator can see.
        var orgScope = new HostScope(orgId);
        var plan = await plans.GetPlanAsync(orgId, orgScope, ct);
        var completed = plan.Parts
            .Where(p => p.State is OnboardingPartStates.Done or OnboardingPartStates.NotAvailable)
            .Select(p => p.Key).ToHashSet();
        var ids = rows.Where(a => a.EntityId != null).Select(a => a.EntityId!.Value).Distinct().ToList();
        var parts = await db.OnboardingPartStates.AsNoTracking()
            .Where(s => s.OrgId == orgId && ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var steps = await db.OnboardingPanelSteps.AsNoTracking()
            .Where(s => s.OrgId == orgId && ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var assists = await db.OnboardingAssistRequests.AsNoTracking()
            .Where(r => r.OrgId == orgId && ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var flowRows = rows.Where(a => a.EntityType == OnboardingActivationCodes.FlowTodoEntityType).ToList();
        HashSet<Guid> pendingFlows = flowRows.Count == 0
            ? []
            : (await flows.GetStatusesAsync(orgScope, null, Enum.GetValues<OnboardingFlow>(), ct))
                .Where(s => s.State == OnboardingFlowStates.ToComplete)
                .Select(s => OnboardingFlowTodo.EntityIdOf(orgId, s.Flow)).ToHashSet();
        var seen = new HashSet<ProcessKey>();

        foreach (var row in rows)
        {
            ProcessKey? process = null;
            var obsolete = false;
            var settled = false;
            var id = row.EntityId ?? Guid.Empty;
            switch (row.EntityType)
            {
                case OnboardingPlanCodes.TodoEntityType:
                    obsolete = !parts.TryGetValue(id, out var part) || completed.Contains(part.PartKey);
                    if (part is not null)
                        process = new(part.PartKey, null, OnboardingAssistContexts.Part, null);
                    break;
                case OnboardingPanelStepCodes.TodoEntityType:
                    obsolete = !steps.TryGetValue(id, out var step)
                               || step.Status is PanelStepStatus.Verified or PanelStepStatus.Confirmed;
                    if (step is not null)
                    {
                        settled = step.Status is PanelStepStatus.Verified or PanelStepStatus.Confirmed;
                        var kind = PanelStepCatalog.Of(step.Kind);
                        obsolete |= completed.Contains(kind.Part);
                        process = new(kind.Part, step.PropertyId, kind.Context, step.ChannelCode);
                    }
                    break;
                case OnboardingAssistCodes.TodoEntityType:
                    obsolete = !assists.TryGetValue(id, out var assist)
                               || assist.Status is OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled;
                    if (assist is not null)
                    {
                        settled = assist.Status == OnboardingAssistStatus.Done;
                        obsolete |= completed.Contains(assist.PartKey);
                        process = new(assist.PartKey, assist.PropertyId, assist.Context, assist.ChannelCode);
                    }
                    break;
                case OnboardingActivationCodes.FlowTodoEntityType:
                    obsolete = !pendingFlows.Contains(id);
                    break;
            }

            // A reminder follows the logical part/property/channel, never the id of a historical attempt. Keep the newest
            // actionable item so its resume link remains valid; closed rows stay available for the audit trail.
            var duplicate = process is not null && (!obsolete || settled) && !seen.Add(process);
            if (obsolete || duplicate)
                await activities.CloseAsync(row.Id, InternalActivity.ClosedBySystem, ct);
        }
    }

    private sealed record ProcessKey(OnboardingPartKey Part, Guid? PropertyId, string Context, string? ChannelCode);
}
