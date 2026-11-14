using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
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
/// <para><b>Staff told live.</b> A new request (never one that was open already), and a request the host reopens, tell the platform staff, after it
/// is saved: an entry of their bell and its live update (<see cref="PushTypes.AssistRequested"/>, <see cref="PushTypes.AssistReopened"/>,
/// <see cref="PushAudienceKind.PlatformStaff"/>), with the request as its only reference. Best effort: a failure is logged and never fails the host.</para>
/// <para><b>«Fatto» (E20/F03/US01/T02).</b> The verifier of the panel step runs first, for the org of the request (<see cref="IAssistPanelVerification"/>),
/// outside the lock and the transaction: a call to the channel manager never holds them. What it read decides the outcome stored on the request and on
/// the step (verified, or declared by the staff user); a «not valid» and a failed read change nothing. After the commit the administrators of the org are
/// told live (<see cref="OnboardingAssistOutcomeNotice"/>).</para>
/// </remarks>
public sealed class OnboardingAssistService(
    AppDbContext db,
    IActivityLog activityLog,
    IInternalActivityService activities,
    IPushNotificationService pushNotifications,
    TimeProvider timeProvider,
    ILogger<OnboardingAssistService> logger,
    IAssistPanelVerification? panelVerification = null,
    IInAppNotificationPublisher? notificationPublisher = null) : IOnboardingAssistService
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

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

        // The request is stored: the staff learns of it now, before the host's "To do", so a failure of the "To do" does not leave them unaware.
        TellStaff(request);
        await RaiseTodoAsync(request, ct);

        logger.LogInformation(
            "Assist request {RequestId} of org {OrgId} for part {Part} ({Context}, channel {ChannelCode}) stored, To do {ActivityId}",
            request.Id, orgId, OnboardingPartCatalog.Of(part).Code, context, channelCode, request.ActivityId);
        return ViewOf(request);
    }

    /// <summary>
    /// Tells the platform staff that a request waits in their queue, new or reopened by the host: an entry of their bell and its live update, once per
    /// request (the delivery key is the request's; a reopening adds its instant), with the request as the only reference. Nothing of the org, the
    /// property, the person or the note goes in it, and there is no text of a push: the staff has no screen in the app, so no phone is told, and the bell
    /// writes its sentence from the type. Best effort: whatever goes wrong is logged and never fails the host's request.
    /// </summary>
    private void TellStaff(OnboardingAssistRequest request, bool reopened = false)
    {
        try
        {
            pushNotifications.Enqueue(
                reopened
                    ? PushDeliveryKeys.AssistReopened(request.Id, request.ReopenedAt ?? request.UpdatedAt)
                    : PushDeliveryKeys.AssistRequested(request.Id),
                PushAudience.PlatformStaff(request.Id),
                new PushNotificationPayload(
                    string.Empty,
                    string.Empty,
                    reopened ? PushTypes.AssistReopened : PushTypes.AssistRequested,
                    BookingId: null,
                    Route: string.Empty));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The staff could not be told about assist request {RequestId} of org {OrgId}", request.Id, request.OrgId);
        }
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

        // «Fatto» on a panel step: what the panel says is read first, outside the lock and the transaction of the request (a call to the channel manager
        // never holds them). A step the panel reads as not valid stops here, and a read that failed goes to the caller: never a silent «done».
        var verification = status == OnboardingAssistStatus.Done ? await VerifyForDoneAsync(requestId, ct) : null;

        OnboardingAssistRequest request;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, ct, (PostgresAdvisoryLocks.Scope.OnboardingPanelAssist, $"assist:{requestId:N}")))
        {
            request = await db.OnboardingAssistRequests
                .IgnoreQueryFilters([Tenant])
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                ?? throw NotFound();
            if (request.Status is OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled)
                throw new DomainConflictException(OnboardingAssistCodes.Closed, "AssistRequestClosed");

            // From here on every statement names the org OF THE REQUEST: the step, the "To do" and the line of the log are its, not the staff user's.
            var now = Now();
            request.Status = status;
            request.AssignedToUserId = status == OnboardingAssistStatus.InProgress ? staffUserId : request.AssignedToUserId ?? staffUserId;
            request.UpdatedAt = now;
            if (note is not null)
                request.StaffNote = normalizedNote;
            if (status is OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled)
            {
                request.CompletedAt = now;
                request.Outcome = status == OnboardingAssistStatus.Done ? OutcomeOf(verification) : null;
                await SettleStepAsync(request, status, verification, staffUserId, now, ct);
                await CloseTodoAsync(request.OrgId, request.ActivityId, staffUserId, now, ct);
            }

            activityLog.Record(LogLine(request.OrgId, OrgActivityType.OnboardingAssistUpdated, staffUserId, request.PartKey, request.Context, status));
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        logger.LogInformation(
            "Assist request {RequestId} of org {OrgId} set to {Status} by staff {StaffUserId} (outcome {Outcome})",
            request.Id, request.OrgId, OnboardingAssistRules.StatusCode(status), staffUserId, request.Outcome);

        // The administrators of the org are told live (the bell, the pop-up, the open page): after the commit, best effort.
        if (status == OnboardingAssistStatus.Done)
            await OnboardingAssistOutcomeNotice.TellAsync(db, pushNotifications, notificationPublisher, request, logger, ct);

        var property = (await PropertiesOfAsync([request], ct)).GetValueOrDefault((request.OrgId, request.PropertyId ?? Guid.Empty));
        return QueueItemOf(request, property);
    }

    public async Task<AssistRequestDetail> GetAsync(Guid requestId, CancellationToken ct)
    {
        // Staff reads the requests of every org: the tenant filter is lifted by name, and every read of the checklist names the org of the request.
        var request = await db.OnboardingAssistRequests
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw NotFound();

        var property = (await PropertiesOfAsync([request], ct)).GetValueOrDefault((request.OrgId, request.PropertyId ?? Guid.Empty));
        return new AssistRequestDetail(QueueItemOf(request, property), await ChecklistAsync(request, ct));
    }

    public async Task<AssistRequestView> ReopenAsync(HostScope scope, string userId, Guid requestId, string? note, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CheckUserId(userId);
        if (!OnboardingAssistRules.TryNormalizeNote(note, out var hostNote) || hostNote is null)
            throw new DomainRuleException(OnboardingAssistCodes.ReopenNoteRequired, "AssistReopenNoteRequired");

        OnboardingAssistRequest request;
        try
        {
            await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, ct, (PostgresAdvisoryLocks.Scope.OnboardingPanelAssist, $"assist:{requestId:N}"));

            // The request of the org of the scope, of a property the scope reaches (a request of the whole org: a scope over the whole org). Anything
            // else is the 404 of a request that does not exist.
            request = await db.OnboardingAssistRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.OrgId == scope.OrgId, ct)
                ?? throw NotFound();
            if (!await CanReachAsync(scope, request.PropertyId, ct))
                throw NotFound();
            if (request.Status != OnboardingAssistStatus.Done)
                throw new DomainConflictException(OnboardingAssistCodes.NotDone, "AssistRequestNotDone");

            // Another open request of the same part, property and channel stands for it (the partial unique index would refuse the save anyway).
            if (await db.OnboardingAssistRequests.AnyAsync(
                    r => r.Id != request.Id
                         && r.OrgId == request.OrgId
                         && r.PartKey == request.PartKey
                         && r.PropertyId == request.PropertyId
                         && r.ChannelCode == request.ChannelCode
                         && OpenStatuses.Contains(r.Status),
                    ct))
            {
                throw AlreadyOpen();
            }

            var now = Now();
            request.Status = OnboardingAssistStatus.Requested;
            request.AssignedToUserId = null;
            request.CompletedAt = null;
            request.Outcome = null;
            request.HostNote = hostNote;
            request.ReopenedAt = now;
            request.UpdatedAt = now;

            // The «Fatto» closed the "To do" the request names: it is not the request's any more. The new one is raised after the commit, so until it is stored
            // the request names none, and a call that fails between the two saves is found and healed by the next ask (RequestOnceAsync), as for a request that
            // never had one: never a reopened request standing behind a closed "To do".
            request.ActivityId = null;
            await GiveStepBackToSupportAsync(request, ct);

            activityLog.Record(LogLine(request.OrgId, OrgActivityType.OnboardingAssistRequested, userId, request.PartKey, request.Context, request.Status));
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OnboardingAssistRequest.OpenRequestIndexName))
        {
            // A parallel request of the same part, property and channel was opened first: nothing of this reopening was saved.
            db.ChangeTracker.Clear();
            throw AlreadyOpen();
        }

        // Stored: the staff learns of it, and the host finds the request behind a "To do" again (the one of the «Fatto» was closed).
        TellStaff(request, reopened: true);
        await RaiseTodoAsync(request, ct);

        logger.LogInformation("Assist request {RequestId} of org {OrgId} reopened by the host, To do {ActivityId}", request.Id, request.OrgId, request.ActivityId);
        return ViewOf(request);
    }

    // ─── «Fatto»: the verification and the step ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What the panel says about the step of the request, before «Fatto» is stored: <c>null</c> for a request of a whole part (there is no step, support
    /// declares it), the outcome of the verifier of the kind otherwise (<see cref="PanelVerificationOutcome.Unverifiable"/> when nothing can verify it).
    /// 409 <see cref="OnboardingAssistCodes.DoneNotValid"/> when the panel reads the step as not valid; a read that failed is thrown as it came, with its
    /// code, so the request stays open and the staff user tries again later.
    /// </summary>
    private async Task<PanelVerificationOutcome?> VerifyForDoneAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.OnboardingAssistRequests
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(r => r.Id == requestId)
            .Select(r => new { r.Id, r.OrgId, r.PropertyId, r.Context, r.ChannelCode, r.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw NotFound();
        if (request.Status is OnboardingAssistStatus.Done or OnboardingAssistStatus.Cancelled)
            throw new DomainConflictException(OnboardingAssistCodes.Closed, "AssistRequestClosed");
        if (!PanelStepCatalog.TryOfContext(request.Context, out var info))
            return null;
        if (panelVerification is null)
            return PanelVerificationOutcome.Unverifiable;

        PanelVerificationOutcome outcome;
        try
        {
            outcome = await panelVerification.VerifyAsync(new PanelStepContext(request.OrgId, request.PropertyId, info.Kind, request.ChannelCode), ct);
        }
        catch (OctorateException ex)
        {
            // Worth a later try: nothing is saved and the error reaches the caller with its code. Never a «done» the panel did not confirm.
            logger.LogWarning(
                "«Done» on assist request {RequestId} of org {OrgId}: the panel could not be read ({Error}), nothing changed",
                request.Id, request.OrgId, ex.Code);
            throw;
        }

        if (outcome == PanelVerificationOutcome.NotValid)
        {
            logger.LogInformation(
                "«Done» on assist request {RequestId} of org {OrgId} refused: the panel reads the {Kind} step as not valid", request.Id, request.OrgId, info.Code);
            throw new DomainConflictException(OnboardingAssistCodes.DoneNotValid, "AssistDoneNotValid");
        }

        return outcome;
    }

    /// <summary>
    /// What a request that is closed does to the panel step it was about (a panel context). <c>Done</c>: support did the step; the step becomes
    /// <see cref="PanelStepStatus.Verified"/> when the panel read it done, otherwise <see cref="PanelStepStatus.Confirmed"/> as declared by the staff
    /// user, who is who completed it in both cases, and its own "To do" is closed. <c>Cancelled</c>: the step is the host's again, with a fresh window of
    /// checks. Only a step the request had marked as asked for (<see cref="PanelStepStatus.AssistRequested"/>) is touched, and for a channel access only
    /// the step of the request's channel: closing the request of one portal never settles the step of another. A request stored before the channel was
    /// kept (no channel) settles the steps of its kind, as it always did.
    /// </summary>
    private async Task SettleStepAsync(
        OnboardingAssistRequest request,
        OnboardingAssistStatus status,
        PanelVerificationOutcome? verification,
        string staffUserId,
        DateTime now,
        CancellationToken ct)
    {
        foreach (var step in await StepsOfAsync(request, PanelStepStatus.AssistRequested, ct))
        {
            if (status == OnboardingAssistStatus.Done)
            {
                if (verification == PanelVerificationOutcome.Verified)
                {
                    step.Status = PanelStepStatus.Verified;
                    step.VerifiedAt = now;
                    step.LastVerificationOutcome = PanelVerificationOutcomes.Verified;
                }
                else
                {
                    // Declared by support: the panel could not tell (no police account on a structure created through the API, ISTAT never).
                    step.Status = PanelStepStatus.Confirmed;
                    step.LastVerificationOutcome = PanelVerificationOutcomes.Unverifiable;
                }

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
    /// The host reopened the request: the step it was about (done by support, or anything else but already with support) is support's again, as when the
    /// host first asked. What the «Fatto» recorded on it no longer stands: the outcome, the completion and the checks start again.
    /// </summary>
    private async Task GiveStepBackToSupportAsync(OnboardingAssistRequest request, CancellationToken ct)
    {
        foreach (var step in await StepsOfAsync(request, status: null, ct))
        {
            if (step.Status == PanelStepStatus.AssistRequested)
                continue;

            step.Status = PanelStepStatus.AssistRequested;
            step.NextVerificationAt = null;
            step.LastVerificationOutcome = null;
            step.VerificationAttempts = 0;
            step.VerifiedAt = null;
            step.CompletedAt = null;
            step.CompletedByUserId = null;
        }
    }

    /// <summary>The panel steps of the request (of its org, property, kind and channel), tracked; none for a request of a whole part.</summary>
    private async Task<List<OnboardingPanelStep>> StepsOfAsync(OnboardingAssistRequest request, PanelStepStatus? status, CancellationToken ct)
    {
        if (!PanelStepCatalog.TryOfContext(request.Context, out var info))
            return [];

        var channelCode = request.ChannelCode;
        return await db.OnboardingPanelSteps
            .IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == request.OrgId
                        && s.PropertyId == request.PropertyId
                        && s.Kind == info.Kind
                        && (channelCode == null || s.ChannelCode == channelCode)
                        && (status == null || s.Status == status))
            .ToListAsync(ct);
    }

    private static string OutcomeOf(PanelVerificationOutcome? verification) =>
        verification == PanelVerificationOutcome.Verified ? OnboardingAssistOutcomes.Verified : OnboardingAssistOutcomes.Declared;

    // ─── The checklist of the staff view ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What support needs, per property of the request (E20/F03/US01/T02): presence and dates of the credentials, never a value, never decrypted (the
    /// reads project the dates only). A request of a property lists that property; a request of the whole org lists the org itself (the user of the panel
    /// of the whole org and its steps) and then its properties, by name, with the steps they already have. Every read names the org of the request.
    /// </summary>
    private async Task<IReadOnlyList<AssistChecklistProperty>> ChecklistAsync(OnboardingAssistRequest request, CancellationToken ct)
    {
        var orgId = request.OrgId;
        var kinds = KindsOf(request);
        var properties = await db.Properties
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && (request.PropertyId == null || p.Id == request.PropertyId))
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Take(OnboardingAssistLimits.MaxChecklistProperties)
            .Select(p => new { p.Id, p.Name, p.City, p.Country })
            .ToListAsync(ct);
        var ids = properties.Select(p => p.Id).ToList();

        var vault = await db.SupportVaultSecrets
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && (s.PropertyId == null || ids.Contains(s.PropertyId.Value)))
            .Select(s => new { s.PropertyId, s.Kind, s.UpdatedAt })
            .ToListAsync(ct);
        var police = await db.PropertyQuesturaCredentials
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && ids.Contains(c.PropertyId))
            .Select(c => new { c.PropertyId, c.UpdatedAt })
            .ToListAsync(ct);
        var istat = await db.PropertyIstatCredentials
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && ids.Contains(c.PropertyId))
            .Select(c => new { c.PropertyId, c.UpdatedAt })
            .ToListAsync(ct);
        var senders = await db.PropertyComplianceSenders
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && ids.Contains(s.PropertyId))
            .Select(s => new { s.PropertyId, s.AlloggiatiSender, s.LastChangedAt, s.ActivationDeclaredAt })
            .ToListAsync(ct);
        List<OnboardingPanelStep> steps = kinds.Count == 0
            ? []
            : await db.OnboardingPanelSteps
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .Where(s => s.OrgId == orgId && kinds.Contains(s.Kind) && (s.PropertyId == null || ids.Contains(s.PropertyId.Value)))
                .ToListAsync(ct);

        AssistCredentialPresence PanelUserOf(Guid? propertyId)
        {
            var secret = vault.FirstOrDefault(s => s.PropertyId == propertyId && s.Kind == SupportVaultSecretKind.OctoratePanel);
            return new AssistCredentialPresence(secret is not null, secret?.UpdatedAt);
        }

        var blocks = new List<AssistChecklistProperty>();
        if (request.PropertyId is null)
        {
            blocks.Add(new AssistChecklistProperty(
                null, null, null, PanelUserOf(null), null, null, null, StepsOf(request, kinds, steps.Where(s => s.PropertyId == null), placeholders: true)));
        }

        foreach (var property in properties)
        {
            var policeRow = police.FirstOrDefault(c => c.PropertyId == property.Id);
            var istatRow = istat.FirstOrDefault(c => c.PropertyId == property.Id);
            var istatVault = vault.FirstOrDefault(s => s.PropertyId == property.Id && s.Kind == SupportVaultSecretKind.RegionalIstat);
            var sender = senders.FirstOrDefault(s => s.PropertyId == property.Id);
            blocks.Add(new AssistChecklistProperty(
                property.Id,
                property.Name,
                property.City,
                PanelUserOf(property.Id),
                new AssistCredentialPresence(policeRow is not null, policeRow?.UpdatedAt),
                new AssistIstatPresence(
                    istatRow is not null || istatVault is not null,
                    istatRow?.UpdatedAt ?? istatVault?.UpdatedAt,
                    IstatRequirementOf(request, property.Country)),
                sender is null
                    ? null
                    : new AssistSenderLine(sender.AlloggiatiSender, sender.LastChangedAt, sender.ActivationDeclaredAt >= sender.LastChangedAt),
                StepsOf(request, kinds, steps.Where(s => s.PropertyId == property.Id), placeholders: request.PropertyId is not null)));
        }

        return blocks;
    }

    /// <summary>
    /// The kinds of panel step a request is about: the one of its panel context; for a whole part, the police and ISTAT connections of the compliance
    /// part or the channel accesses of the channels part; none for the other parts.
    /// </summary>
    private static List<PanelStepKind> KindsOf(OnboardingAssistRequest request)
    {
        if (PanelStepCatalog.TryOfContext(request.Context, out var info))
            return [info.Kind];

        return request.PartKey switch
        {
            OnboardingPartKey.Compliance => [PanelStepKind.PoliceConnection, PanelStepKind.IstatConnection],
            OnboardingPartKey.Channels => [PanelStepKind.ChannelAccess],
            _ => [],
        };
    }

    /// <summary>
    /// The steps of one block of the checklist, in the order of the kinds: each step the block has (for a channel access, the one of the request's channel,
    /// or every channel when the request names none), and, with <paramref name="placeholders"/>, a <see cref="AssistChecklistStates.NotStarted"/> line for a
    /// kind the block does not have yet (a channel access with no channel has no placeholder: there is no channel to name).
    /// </summary>
    private static List<AssistChecklistStep> StepsOf(
        OnboardingAssistRequest request,
        IReadOnlyList<PanelStepKind> kinds,
        IEnumerable<OnboardingPanelStep> rows,
        bool placeholders)
    {
        var mine = rows.ToList();
        var items = new List<AssistChecklistStep>();
        foreach (var kind in kinds)
        {
            var info = PanelStepCatalog.Of(kind);
            var channelCode = kind == PanelStepKind.ChannelAccess ? request.ChannelCode : null;
            var found = mine
                .Where(s => s.Kind == kind && (channelCode == null || s.ChannelCode == channelCode))
                .OrderBy(s => s.ChannelCode, StringComparer.Ordinal)
                .ToList();
            items.AddRange(found.Select(s => new AssistChecklistStep(
                s.Kind, s.ChannelCode, info.Context, PanelStepCatalog.CodeOf(s.Status), s.LastVerificationOutcome, s.CompletedAt)));

            if (found.Count == 0 && placeholders && (kind != PanelStepKind.ChannelAccess || channelCode is not null))
                items.Add(new AssistChecklistStep(kind, channelCode, info.Context, AssistChecklistStates.NotStarted, null, null));
        }

        return items;
    }

    /// <summary>
    /// Whether the region of the property needs ISTAT credentials: not outside Italy; needed for a request about the ISTAT connection; otherwise to verify,
    /// because the region decides and the table of the regions (E21, decision O6) is not there yet.
    /// </summary>
    private static string IstatRequirementOf(OnboardingAssistRequest request, string? country)
    {
        if (!string.Equals(country?.Trim(), "IT", StringComparison.OrdinalIgnoreCase))
            return AssistIstatRequirements.NotRequired;

        return request.Context == OnboardingAssistContexts.PanelIstat ? AssistIstatRequirements.Required : AssistIstatRequirements.ToVerify;
    }

    /// <summary>Whether the scope reaches a request of <paramref name="propertyId"/> (the whole org for none), as the host's other operations decide it.</summary>
    private Task<bool> CanReachAsync(HostScope scope, Guid? propertyId, CancellationToken ct) => OnboardingReach.CanReachAsync(db, scope, propertyId, ct);

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
            r.ChannelCode,
            r.Outcome,
            r.HostNote,
            r.ReopenedAt);

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
            r.ChannelCode,
            r.Outcome,
            r.HostNote,
            r.ReopenedAt);

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

    private static NotFoundException NotFound() =>
        new("Assist request not found.")
        {
            Code = OnboardingAssistCodes.NotFound,
            MessageKey = "AssistRequestNotFound",
        };

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
