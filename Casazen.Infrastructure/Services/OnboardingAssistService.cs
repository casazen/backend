using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingAssistService" />
/// <remarks>
/// <para><b>Host side.</b> Every query names the org of the scope and narrows to what the scope reaches (<see cref="OnboardingReach"/>). The request
/// is saved with the panel step it asks for and the line of the log in one save, so a request never exists without its line; the "To do" is raised
/// after, and its id is stored on the request.</para>
/// <para><b>Staff side.</b> The platform administrator works across orgs, so the tenant filter is lifted by name on the reads of the request, and
/// every other statement of the update names the org <b>of the request</b> (<c>OrgId == request.OrgId</c>): the panel step, the "To do" and the line of
/// the log belong to the org of the request, never to the org of the staff user. The update runs under a transaction lock of the request, so two
/// people never close the same request in two ways.</para>
/// <para><b>Privacy.</b> The staff view is made of ids, codes, the property and its comune; the request holds only the reference to the draft, and the
/// note of staff is refused when it holds an e-mail address. Nothing here reads a draft.</para>
/// </remarks>
public sealed class OnboardingAssistService(
    AppDbContext db,
    IActivityLog activityLog,
    IInternalActivityService activities,
    TimeProvider timeProvider,
    ILogger<OnboardingAssistService> logger) : IOnboardingAssistService
{
    private static readonly OnboardingAssistStatus[] OpenStatuses = [OnboardingAssistStatus.Requested, OnboardingAssistStatus.InProgress];

    /// <summary>The page of the queue is never beyond this number: a skip of <c>(page - 1) * pageSize</c> stays an int.</summary>
    private const int MaxPage = 100_000;

    public async Task<AssistRequestView> RequestAsync(HostScope scope, string userId, AssistRequestInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(input);
        CheckUserId(userId);

        if (!Enum.IsDefined(input.Part))
            throw new DomainRuleException(OnboardingPlanCodes.PartUnknown, "OnboardingPartUnknown");

        var context = input.Context?.Trim();
        if (context is null || !OnboardingAssistRules.ContextFits(input.Part, context))
            throw new DomainRuleException(OnboardingAssistCodes.ContextInvalid, "AssistContextInvalid");

        var kind = PanelStepCatalog.TryOfContext(context, out var info) ? info : null;
        var channelCode = CheckedChannel(kind, input.ChannelCode);
        await OnboardingReach.EnsureAsync(db, scope, input.PropertyId, ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RequestOnceAsync(scope.OrgId, userId, input.Part, input.PropertyId, context, kind, channelCode, ct);
            }
            catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OnboardingAssistRequest.OpenRequestIndexName))
            {
                // A parallel request of the same part, property and channel saved first (the partial unique index): nothing of this one was saved.
                db.ChangeTracker.Clear();
                throw AlreadyOpen();
            }
            catch (DbUpdateException ex) when (attempt == 1 && OrgTeamAccess.IsUniqueViolation(ex, OnboardingPanelStep.StepIndexName))
            {
                // The panel step was started in parallel: nothing was saved, and the call reads it again.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AssistRequestView> RequestOnceAsync(
        Guid orgId,
        string userId,
        OnboardingPartKey part,
        Guid? propertyId,
        string context,
        PanelStepKindInfo? kind,
        string? channelCode,
        CancellationToken ct)
    {
        // One open request for each part, property and channel: «Fallo per me» on one portal never stands for, nor blocks, another portal.
        var open = await db.OnboardingAssistRequests
            .FirstOrDefaultAsync(
                r => r.OrgId == orgId
                     && r.PartKey == part
                     && r.PropertyId == propertyId
                     && r.ChannelCode == channelCode
                     && OpenStatuses.Contains(r.Status),
                ct);
        if (open is not null)
        {
            // A request saved without its "To do" (the call failed between the two saves) gets it now: the host that asks again is told the request
            // exists, as before, and finds its status behind a "To do" from then on. CreateAsync is idempotent: a "To do" that exists is linked.
            if (open.ActivityId is null)
                await RaiseTodoAsync(open, ct);

            throw AlreadyOpen();
        }

        var now = Now();

        // The panel step the request is about is marked as asked for; one that was never started is made, so support has the step to confirm.
        if (kind is not null)
        {
            var step = await db.OnboardingPanelSteps.FirstOrDefaultAsync(
                s => s.OrgId == orgId && s.PropertyId == propertyId && s.Kind == kind.Kind && s.ChannelCode == channelCode, ct);
            if (step is { Status: PanelStepStatus.Verified or PanelStepStatus.Confirmed })
                throw new DomainConflictException(OnboardingAssistCodes.StepAlreadyDone, "AssistStepAlreadyDone");

            if (step is null)
            {
                db.OnboardingPanelSteps.Add(new OnboardingPanelStep
                {
                    OrgId = orgId,
                    PropertyId = propertyId,
                    Kind = kind.Kind,
                    ChannelCode = channelCode,
                    Status = PanelStepStatus.AssistRequested,
                    HandoffMode = PanelHandoffMode.ManagedWindow,
                    StartedAt = now,
                });
            }
            else
            {
                step.Status = PanelStepStatus.AssistRequested;
                step.NextVerificationAt = null;

                // Support has the step: what an earlier check found no longer stands. A stored «could not tell» would let the other door on the
                // same row (the import's channel access handoff) take the host's confirmation, so the outcome and the attempts start again.
                step.LastVerificationOutcome = null;
                step.VerificationAttempts = 0;
            }
        }

        // A reference to the draft of the host's wizard (whether it exists, never what it holds), and no copy of it.
        var hasDraft = await db.WizardDrafts
            .AsNoTracking()
            .AnyAsync(
                d => d.OrgId == orgId
                     && d.UserId == userId
                     && d.WizardType == WizardType.OnboardingActivation
                     && d.EntityId == propertyId
                     && d.ExpiresAt > now,
                ct);

        var request = new OnboardingAssistRequest
        {
            OrgId = orgId,
            PartKey = part,
            PropertyId = propertyId,
            Context = context,
            ChannelCode = channelCode,
            Status = OnboardingAssistStatus.Requested,
            DraftWizardType = hasDraft ? WizardType.OnboardingActivation : null,
            DraftEntityId = hasDraft ? propertyId : null,
            RequestedByUserId = userId,
            RequestedAt = now,
            UpdatedAt = now,
        };
        db.OnboardingAssistRequests.Add(request);
        activityLog.Record(LogLine(orgId, OrgActivityType.OnboardingAssistRequested, userId, part, context, OnboardingAssistStatus.Requested));
        await db.SaveChangesAsync(ct);

        await RaiseTodoAsync(request, ct);

        logger.LogInformation(
            "Assist request {RequestId} of org {OrgId} for part {Part} ({Context}, channel {ChannelCode}) stored, To do {ActivityId}",
            request.Id, orgId, OnboardingPartCatalog.Of(part).Code, context, channelCode, request.ActivityId);
        return ViewOf(request);
    }

    /// <summary>The host's "To do" of a request (codes and ids only), found again when it exists, and its id stored on the request (saved here).</summary>
    private async Task RaiseTodoAsync(OnboardingAssistRequest request, CancellationToken ct)
    {
        request.ActivityId = await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.WizardResume,
                Action = ActivityAction.ResumeWizard,
                EntityType = OnboardingAssistCodes.TodoEntityType,
                EntityId = request.Id,
                Urgency = ActivityUrgency.Medium,
                PropertyId = request.PropertyId,
                OrgId = request.OrgId,
                Reason = OnboardingAssistCodes.ReasonRequested,
            },
            ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AssistRequestView>> ListAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var reachable = OnboardingReach.PropertyIds(db, scope);
        var orgWide = scope.IsOrgWide;
        var rows = await db.OnboardingAssistRequests
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId)
            .Where(r => (r.PropertyId == null && orgWide) || (r.PropertyId != null && reachable.Contains(r.PropertyId.Value)))
            .OrderByDescending(r => r.RequestedAt)
            .ThenBy(r => r.Id)
            .Take(OnboardingAssistLimits.MaxPageSize)
            .ToListAsync(ct);

        return [.. rows.Select(ViewOf)];
    }

    public async Task<AssistQueuePage> ListQueueAsync(OnboardingAssistStatus? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Clamp(page, 1, MaxPage);
        pageSize = pageSize <= 0 ? OnboardingAssistLimits.DefaultPageSize : Math.Min(pageSize, OnboardingAssistLimits.MaxPageSize);

        // The queue is the one place that reads the requests of every org, on purpose: the tenant filter is lifted by name, and the rows keep their org.
        var query = db.OnboardingAssistRequests.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking();
        if (status is { } wanted)
            query = query.Where(r => r.Status == wanted);

        var total = await query.CountAsync(ct);

        // Open requests first (Requested = 1, InProgress = 2 sort before Done and Cancelled), the oldest first.
        var rows = await query
            .OrderBy(r => r.Status)
            .ThenBy(r => r.RequestedAt)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var properties = await PropertiesOfAsync(rows, ct);
        return new AssistQueuePage(
            [.. rows.Select(r => QueueItemOf(r, properties.GetValueOrDefault((r.OrgId, r.PropertyId ?? Guid.Empty))))],
            total,
            page,
            pageSize);
    }

    public async Task<AssistQueueItem> UpdateAsync(
        string staffUserId,
        Guid requestId,
        OnboardingAssistStatus status,
        string? note,
        CancellationToken ct)
    {
        CheckUserId(staffUserId);
        if (status is not (OnboardingAssistStatus.InProgress or OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled))
            throw new DomainRuleException(OnboardingAssistCodes.StatusInvalid, "AssistStatusInvalid");
        if (!OnboardingAssistRules.TryNormalizeNote(note, out var normalizedNote))
            throw new DomainRuleException(OnboardingAssistCodes.NoteInvalid, "AssistNoteInvalid");

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OnboardingPanelAssist, $"assist:{requestId:N}"));

        var request = await db.OnboardingAssistRequests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new NotFoundException("Assist request not found.")
            {
                Code = OnboardingAssistCodes.NotFound,
                MessageKey = "AssistRequestNotFound",
            };
        if (request.Status is OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled)
            throw new DomainConflictException(OnboardingAssistCodes.Closed, "AssistRequestClosed");

        // From here on every statement names the org OF THE REQUEST: the step, the "To do" and the line of the log are its, not the staff user's.
        var orgId = request.OrgId;
        var now = Now();
        request.Status = status;
        request.AssignedToUserId = status == OnboardingAssistStatus.InProgress ? staffUserId : request.AssignedToUserId ?? staffUserId;
        request.UpdatedAt = now;
        if (note is not null)
            request.StaffNote = normalizedNote;
        if (status is OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled)
        {
            request.CompletedAt = now;
            await SettleStepAsync(request, status, staffUserId, now, ct);
            await CloseTodoAsync(orgId, request.ActivityId, staffUserId, now, ct);
        }

        activityLog.Record(LogLine(orgId, OrgActivityType.OnboardingAssistUpdated, staffUserId, request.PartKey, request.Context, status));
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Assist request {RequestId} of org {OrgId} set to {Status} by staff {StaffUserId}",
            request.Id, orgId, OnboardingAssistRules.StatusCode(status), staffUserId);

        var property = (await PropertiesOfAsync([request], ct)).GetValueOrDefault((orgId, request.PropertyId ?? Guid.Empty));
        return QueueItemOf(request, property);
    }

    /// <summary>
    /// What a request that is closed does to the panel step it was about (a panel context). <c>Done</c>: support did the step, so the step is
    /// confirmed with the staff user as who completed it and its own "To do" is closed. <c>Cancelled</c>: the step is the host's again, with a fresh
    /// window of checks. Only a step the request had marked as asked for (<see cref="PanelStepStatus.AssistRequested"/>) is touched, and for a
    /// channel access only the step of the request's channel: closing the request of one portal never settles the step of another. A request
    /// stored before the channel was kept (no channel) settles the steps of its kind, as it always did.
    /// </summary>
    private async Task SettleStepAsync(
        OnboardingAssistRequest request,
        OnboardingAssistStatus status,
        string staffUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (!PanelStepCatalog.TryOfContext(request.Context, out var info))
            return;

        var channelCode = request.ChannelCode;
        var steps = await db.OnboardingPanelSteps
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(s => s.OrgId == request.OrgId
                        && s.PropertyId == request.PropertyId
                        && s.Kind == info.Kind
                        && (channelCode == null || s.ChannelCode == channelCode)
                        && s.Status == PanelStepStatus.AssistRequested)
            .ToListAsync(ct);

        foreach (var step in steps)
        {
            if (status == OnboardingAssistStatus.Done)
            {
                step.Status = PanelStepStatus.Confirmed;
                step.CompletedAt = now;
                step.CompletedByUserId = staffUserId;
                step.NextVerificationAt = null;
                await CloseTodoAsync(request.OrgId, step.ActivityId, staffUserId, now, ct);
            }
            else
            {
                step.Status = PanelStepStatus.Started;
                step.VerificationAttempts = 0;
                step.LastVerificationOutcome = null;
                step.NextVerificationAt = now + PanelStepLimits.BackoffAfter(1);
            }
        }
    }

    /// <summary>
    /// Closes a "To do" of the org of the request, as the staff user (the save is the caller's). It does not go through
    /// <see cref="IInternalActivityService"/>, whose reads are those of the caller's own org: the item is the host's, found by its id AND its org.
    /// </summary>
    private async Task CloseTodoAsync(Guid orgId, Guid? activityId, string closedBy, DateTime now, CancellationToken ct)
    {
        if (activityId is not { } id)
            return;

        var todo = await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(a => a.Id == id && a.OrgId == orgId && a.Action != null, ct);
        if (todo is null || todo.CompletedAt is not null)
            return;

        todo.Status = InternalActivity.StatusCompleted;
        todo.CompletedAt = now;
        todo.ClosedBy = closedBy;
    }

    /// <summary>The property and the comune of the rows, found by the org of each row and the id (a property of another org is never matched).</summary>
    private async Task<Dictionary<(Guid OrgId, Guid PropertyId), PropertyLine>> PropertiesOfAsync(
        IReadOnlyCollection<OnboardingAssistRequest> rows,
        CancellationToken ct)
    {
        var ids = rows.Where(r => r.PropertyId is not null).Select(r => r.PropertyId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<(Guid OrgId, Guid PropertyId), PropertyLine>();

        var found = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.OrgId, p.Id, p.Name, p.City })
            .ToListAsync(ct);

        return found.ToDictionary(p => (p.OrgId, p.Id), p => new PropertyLine(p.Name, p.City));
    }

    private static AssistRequestView ViewOf(OnboardingAssistRequest r) =>
        new(
            r.Id,
            r.PartKey,
            OnboardingPartCatalog.Of(r.PartKey).Code,
            r.PropertyId,
            r.Context,
            r.Status,
            r.RequestedAt,
            r.UpdatedAt,
            r.CompletedAt,
            r.StaffNote,
            r.ActivityId,
            r.ChannelCode);

    private static AssistQueueItem QueueItemOf(OnboardingAssistRequest r, PropertyLine? property) =>
        new(
            r.Id,
            r.OrgId,
            OnboardingPartCatalog.Of(r.PartKey).Code,
            r.PropertyId,
            property?.Name,
            property?.Comune,
            r.Context,
            r.Status,
            r.RequestedAt,
            r.UpdatedAt,
            r.CompletedAt,
            r.AssignedToUserId,
            r.StaffNote,
            r.DraftWizardType,
            r.DraftEntityId,
            r.ChannelCode);

    /// <summary>The line of the activity log of the org: the part, the context and the status as short codes; never a note, a draft or a person.</summary>
    private static OrgActivity LogLine(
        Guid orgId,
        OrgActivityType type,
        string actorUserId,
        OnboardingPartKey part,
        string context,
        OnboardingAssistStatus status)
    {
        return OrgActivity.Of(
            orgId,
            type,
            actorUserId,
            orgId.ToString(),
            (OrgActivityDetailKeys.Part, OnboardingPartCatalog.Of(part).Code),
            (OrgActivityDetailKeys.Context, context),
            (OrgActivityDetailKeys.Status, OnboardingAssistRules.StatusCode(status)));
    }

    /// <summary>The channel of a channel access (1 to 40 characters); refused for every other context.</summary>
    private static string? CheckedChannel(PanelStepKindInfo? kind, string? channelCode)
    {
        var code = channelCode?.Trim();
        if (kind?.Kind == PanelStepKind.ChannelAccess)
        {
            return string.IsNullOrEmpty(code) || code.Length > OnboardingPanelStep.ChannelCodeMaxLength
                ? throw new DomainRuleException(OnboardingPanelStepCodes.ChannelInvalid, "PanelStepChannelInvalid")
                : code;
        }

        return string.IsNullOrEmpty(code)
            ? null
            : throw new DomainRuleException(OnboardingPanelStepCodes.ChannelInvalid, "PanelStepChannelInvalid");
    }

    private static DomainConflictException AlreadyOpen() =>
        new(OnboardingAssistCodes.AlreadyOpen, "AssistRequestAlreadyOpen");

    private static void CheckUserId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > OnboardingAssistRequest.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(userId));
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The name and the comune of a property, as the staff view shows them.</summary>
    private sealed record PropertyLine(string Name, string Comune);
}
