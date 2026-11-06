using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingPanelStepService" />
/// <remarks>
/// <para><b>The nonce.</b> Every hand-over carries 256 random bits (<see cref="StateBytes"/>). Only their SHA-256 is stored
/// (<see cref="OnboardingPanelStep.HandoffNonceHash"/>), with an expiry; the value itself leaves once, inside the callback address of a panel that
/// sends the person back, and is never stored or logged. A callback presents it, it is compared in constant time and consumed in the statement that
/// claims it, so two requests with the same state never both pass. It carries no data: the step it belongs to is the one in the route.</para>
/// <para><b>The schedule.</b> The columns of the step are the whole state: <c>VerificationAttempts</c> and <c>NextVerificationAt</c>. A check claims
/// its attempt and moves the schedule <b>before</b> it calls out, so a crash, a timeout or a slow verifier never leaves a step that is checked again
/// at once. The previous check is <c>NextVerificationAt</c> minus the wait that followed it, which is how the explicit trigger knows how long ago it
/// was.</para>
/// <para><b>A failed check decides nothing.</b> A verifier that throws (Octorate down, the quota used) is logged with its code, counts as an attempt
/// and stores no outcome: it never unlocks the confirmation, and the next check comes at the next time of the schedule.</para>
/// </remarks>
public sealed class OnboardingPanelStepService(
    AppDbContext db,
    IEnumerable<IPanelStepVerifier> verifiers,
    IInternalActivityService activities,
    IOptions<OctorateOptions> octorateOptions,
    TimeProvider timeProvider,
    ILogger<OnboardingPanelStepService> logger) : IOnboardingPanelStepService
{
    /// <summary>Random bytes of a state (256 bits).</summary>
    public const int StateBytes = 32;

    public async Task<PanelStepStart> StartAsync(HostScope scope, string userId, PanelStepStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        CheckUserId(userId);

        if (!Enum.IsDefined(request.Kind))
            throw new DomainRuleException(OnboardingPanelStepCodes.KindInvalid, "PanelStepKindInvalid");

        var channelCode = CheckedChannel(request.Kind, request.ChannelCode);
        var resume = CheckedResume(request);
        var panelUrl = OnboardingChannelAccessHandoff.ParsePanelUrl(octorateOptions.Value.FrontendBaseUrl)
            ?? throw new DomainConflictException("octorate_panel_url_missing", "OctoratePanelUrlMissing");
        await OnboardingReach.EnsureAsync(db, scope, request.PropertyId, ct);

        // A panel that sends the person back uses the registered callback of CasaZen; one that does not is a window CasaZen manages.
        var callbackBase = VerifierOf(request.Kind) is { SupportsCallback: true } ? ParseCallbackBase(octorateOptions.Value.RedirectUri) : null;
        var mode = callbackBase is null ? PanelHandoffMode.ManagedWindow : PanelHandoffMode.RedirectCallback;
        var now = Now();

        OnboardingPanelStep step;
        string state;
        for (var attempt = 0; ; attempt++)
        {
            var found = await FindAsync(scope.OrgId, request.PropertyId, request.Kind, channelCode, ct);

            // A step that is done, or that support is handling, is answered as it is: nothing is restarted and no new hand-over is made.
            if (found is { Status: PanelStepStatus.Verified or PanelStepStatus.Confirmed or PanelStepStatus.AssistRequested })
                return new PanelStepStart(ViewOf(found), panelUrl, null);

            if (found is null)
            {
                found = new OnboardingPanelStep
                {
                    OrgId = scope.OrgId,
                    PropertyId = request.PropertyId,
                    Kind = request.Kind,
                    ChannelCode = channelCode,
                };
                db.OnboardingPanelSteps.Add(found);
            }

            step = found;
            state = NewState();
            Restart(step, mode, state, now, resume);

            try
            {
                // Stored before any address leaves: the step, the single hand-over and the point to resume.
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (attempt == 0 && OrgTeamAccess.IsUniqueViolation(ex, OnboardingPanelStep.StepIndexName))
            {
                // A parallel start of the same step won: read its row and apply the same restart to it.
                db.Entry(step).State = EntityState.Detached;
            }
        }

        step.ActivityId = await RaiseTodoAsync(step, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Panel step {StepId} ({Kind}) of org {OrgId} started in mode {Mode}, To do {ActivityId}",
            step.Id, step.Kind, step.OrgId, step.HandoffMode, step.ActivityId);

        var callbackUrl = callbackBase is null ? null : CallbackUrl(callbackBase, step.Id, state);
        return new PanelStepStart(ViewOf(step), panelUrl, callbackUrl);
    }

    public async Task<PanelStepView> GetStatusAsync(HostScope scope, Guid stepId, CancellationToken ct)
    {
        var step = await LoadAsync(scope, stepId, ct);
        if (IsOpen(step) && IsDue(step, Now(), explicitTrigger: false))
            await VerifyOnceAsync(step, explicitTrigger: false, ct);

        return ViewOf(step);
    }

    public async Task<PanelStepView> VerifyAsync(HostScope scope, Guid stepId, CancellationToken ct)
    {
        var step = await LoadAsync(scope, stepId, ct);
        if (IsOpen(step) && IsDue(step, Now(), explicitTrigger: true))
            await VerifyOnceAsync(step, explicitTrigger: true, ct);

        return ViewOf(step);
    }

    public async Task<PanelStepView> ConfirmAsync(HostScope scope, string userId, Guid stepId, CancellationToken ct)
    {
        CheckUserId(userId);
        var step = await LoadAsync(scope, stepId, ct);

        // Already done: the same answer again.
        if (step.Status is PanelStepStatus.Verified or PanelStepStatus.Confirmed)
            return ViewOf(step);

        if (step.Status == PanelStepStatus.NotValid)
            throw new DomainConflictException(OnboardingPanelStepCodes.NotValid, "PanelStepNotValid");
        if (step.Status == PanelStepStatus.AssistRequested)
            throw new DomainConflictException(OnboardingPanelStepCodes.AssistOpen, "PanelStepAssistOpen");

        // Only after a real verification stored «could not tell»: the confirmation is the technical fallback, never the normal path.
        if (step.LastVerificationOutcome != PanelVerificationOutcomes.Unverifiable)
            throw new DomainConflictException(OnboardingPanelStepCodes.VerificationRequired, "PanelStepVerificationRequired");

        step.Status = PanelStepStatus.Confirmed;
        step.CompletedAt = Now();
        step.CompletedByUserId = userId;
        step.NextVerificationAt = null;
        await CloseTodoAsync(step, userId, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Panel step {StepId} of org {OrgId} confirmed by the host", step.Id, step.OrgId);
        return ViewOf(step);
    }

    public async Task<PanelStepView> CompleteCallbackAsync(HostScope scope, Guid stepId, string? state, CancellationToken ct)
    {
        var step = await LoadAsync(scope, stepId, ct);
        if (step.HandoffMode != PanelHandoffMode.RedirectCallback)
            throw new DomainConflictException(OnboardingPanelStepCodes.CallbackUnsupported, "PanelStepCallbackUnsupported");

        await ConsumeStateAsync(step, state, ct);

        // The person just came back from the panel: the check is due now, whatever the schedule said.
        if (IsOpen(step))
            await VerifyOnceAsync(step, explicitTrigger: true, ct);

        return ViewOf(step);
    }

    public async Task<IReadOnlyList<PanelStepView>> ListAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var reachable = OnboardingReach.PropertyIds(db, scope);
        var orgWide = scope.IsOrgWide;
        var rows = await db.OnboardingPanelSteps
            .AsNoTracking()
            .Where(s => s.OrgId == scope.OrgId)
            .Where(s => (s.PropertyId == null && orgWide) || (s.PropertyId != null && reachable.Contains(s.PropertyId.Value)))
            .OrderByDescending(s => s.StartedAt)
            .ThenBy(s => s.Id)
            .Take(PanelStepLimits.MaxListed)
            .ToListAsync(ct);

        return [.. rows.Select(ViewOf)];
    }

    // ─── Verification ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One check of the step: under the lock of the step (a holder that finds it taken leaves, and the one that holds it stores the outcome), the
    /// attempt is claimed and the schedule moved, then the verifier of the kind is asked and its outcome stored.
    /// </summary>
    private async Task VerifyOnceAsync(OnboardingPanelStep step, bool explicitTrigger, CancellationToken ct)
    {
        await using var verifying = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.OnboardingPanelAssist, LockKey(step.Id), ct);
        if (verifying is null)
            return;

        // Read again inside the lock: the request that held it may have finished just before.
        await db.Entry(step).ReloadAsync(ct);
        var now = Now();
        if (!IsOpen(step) || !IsDue(step, now, explicitTrigger))
            return;

        step.VerificationAttempts += 1;
        step.NextVerificationAt = now + PanelStepLimits.BackoffAfter(step.VerificationAttempts);
        await db.SaveChangesAsync(ct);

        var attempt = step.VerificationAttempts;
        PanelVerificationOutcome outcome;
        try
        {
            outcome = VerifierOf(step.Kind) is { } verifier
                ? await verifier.VerifyAsync(new PanelStepContext(step.OrgId, step.PropertyId, step.Kind, step.ChannelCode), ct)
                : PanelVerificationOutcome.Unverifiable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Worth a later check, never a verdict: the attempt is counted and the schedule has moved on. Codes only, never a body.
            logger.LogWarning(
                "Verification {Attempt} of panel step {StepId} ({Kind}) of org {OrgId} could not be completed ({Error})",
                attempt, step.Id, step.Kind, step.OrgId, ex is OctorateException octorate ? octorate.Code : ex.GetType().Name);
            return;
        }

        await ApplyAsync(step, outcome, ct);
    }

    /// <summary>Stores what the verification found; a step found done is completed and its "To do" closed in the same save.</summary>
    private async Task ApplyAsync(OnboardingPanelStep step, PanelVerificationOutcome outcome, CancellationToken ct)
    {
        step.LastVerificationOutcome = outcome.Code();
        switch (outcome)
        {
            case PanelVerificationOutcome.Verified:
                var now = Now();
                step.Status = PanelStepStatus.Verified;
                step.VerifiedAt = now;
                step.CompletedAt = now;
                step.CompletedByUserId = null;
                step.NextVerificationAt = null;
                await CloseTodoAsync(step, InternalActivity.ClosedBySystem, ct);
                break;

            case PanelVerificationOutcome.NotValid:
                step.Status = PanelStepStatus.NotValid;
                break;

            default:
                // «Could not tell»: the step is open again for the person to confirm, even if an earlier check had found it not valid.
                step.Status = PanelStepStatus.Started;
                break;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Panel step {StepId} ({Kind}) of org {OrgId} verified: {Outcome} (check {Attempt})",
            step.Id, step.Kind, step.OrgId, step.LastVerificationOutcome, step.VerificationAttempts);
    }

    private static bool IsOpen(OnboardingPanelStep step) => step.Status is PanelStepStatus.Started or PanelStepStatus.NotValid;

    /// <summary>
    /// Whether a check may run now. By the schedule when <paramref name="now"/> reached <c>NextVerificationAt</c>; a step with no schedule only
    /// when the person asked. The explicit trigger of the person also runs before the schedule, if the previous check is at least
    /// <see cref="PanelStepLimits.ExplicitCheckMinGapSeconds"/> old.
    /// </summary>
    internal static bool IsDue(OnboardingPanelStep step, DateTime now, bool explicitTrigger)
    {
        if (step.NextVerificationAt is not { } next)
            return explicitTrigger;
        if (next <= now)
            return true;
        if (!explicitTrigger)
            return false;

        // The previous check is the schedule minus the wait that followed it; with no check yet there is nothing to wait for.
        if (step.VerificationAttempts == 0)
            return true;

        var previous = next - PanelStepLimits.BackoffAfter(step.VerificationAttempts);
        return now - previous >= TimeSpan.FromSeconds(PanelStepLimits.ExplicitCheckMinGapSeconds);
    }

    private IPanelStepVerifier? VerifierOf(PanelStepKind kind) =>
        verifiers.Where(v => v.Kind == kind).OrderByDescending(v => v.Order).FirstOrDefault();

    // ─── The callback ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Consumes the state of the callback: it must be the one of this hand-over, before its expiry, not used. Every refusal is the same 400, so
    /// the answer tells nothing about which check refused it, and nothing of the state is logged.
    /// </summary>
    private async Task ConsumeStateAsync(OnboardingPanelStep step, string? state, CancellationToken ct)
    {
        var presented = state?.Trim();
        if (string.IsNullOrEmpty(presented) || presented.Length > PanelStepLimits.StateMaxLength)
            throw StateInvalid();

        var now = Now();
        var presentedHash = HashOf(presented);
        var matches = step.HandoffNonceHash is { Length: OnboardingPanelStep.HandoffNonceHashLength } stored
                      && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(presentedHash));
        var alive = step.CallbackConsumedAt is null && step.HandoffExpiresAt is { } expires && expires > now;
        if (!matches || !alive)
        {
            logger.LogInformation("Panel step {StepId} callback refused (state matches {Matches}, usable {Alive})", step.Id, matches, alive);
            throw StateInvalid();
        }

        if (!await ClaimStateAsync(step, presentedHash, now, ct))
        {
            logger.LogInformation("Panel step {StepId} callback refused: the state was consumed by another request meanwhile", step.Id);
            throw StateInvalid();
        }
    }

    /// <summary>
    /// Marks the state consumed. On PostgreSQL one statement that only passes while the row still holds the presented state (its hash), not consumed
    /// and not expired: a parallel presentation of the same state, a new hand-over that replaced the nonce and the expiry between the check in memory
    /// and the claim all find it refused. On EF InMemory a tracked save.
    /// </summary>
    internal async Task<bool> ClaimStateAsync(OnboardingPanelStep step, string presentedHash, DateTime now, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var claimed = await db.OnboardingPanelSteps
                .Where(s => s.Id == step.Id
                            && s.OrgId == step.OrgId
                            && s.CallbackConsumedAt == null
                            && s.HandoffNonceHash == presentedHash
                            && s.HandoffExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.CallbackConsumedAt, (DateTime?)now), ct);
            if (claimed != 1)
                return false;

            await db.Entry(step).ReloadAsync(ct);
            return true;
        }

        step.CallbackConsumedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static DomainRuleException StateInvalid() => new(OnboardingPanelStepCodes.StateInvalid, "PanelStepStateInvalid");

    // ─── The "To do" ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The one open "To do" of the step, due tomorrow at 09:00 in Rome: a step left half way never disappears unnoticed. Codes and ids only.</summary>
    private async Task<Guid> RaiseTodoAsync(OnboardingPanelStep step, CancellationToken ct) =>
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.WizardResume,
                Action = ActivityAction.ResumeWizard,
                EntityType = OnboardingPanelStepCodes.TodoEntityType,
                EntityId = step.Id,
                Urgency = ActivityUrgency.Medium,
                PropertyId = step.PropertyId,
                OrgId = step.OrgId,
                Deadline = RomeCalendar.ToUtc(timeProvider.TodayInRomeAsDateOnly().AddDays(1), new TimeOnly(OnboardingPlanLimits.DefaultRemindHour, 0)),
                Reason = OnboardingPanelStepCodes.ReasonPending,
            },
            ct);

    /// <summary>Closes the "To do" of the step when it is open (the save is the caller's).</summary>
    private async Task CloseTodoAsync(OnboardingPanelStep step, string closedBy, CancellationToken ct)
    {
        if (step.ActivityId is not { } activityId)
            return;

        var open = await db.InternalActivities
            .AsNoTracking()
            .AnyAsync(a => a.Id == activityId && a.OrgId == step.OrgId && a.Action != null && a.CompletedAt == null, ct);
        if (open)
            await activities.CloseAsync(activityId, closedBy, ct);
    }

    // ─── Loading and views ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The step of the scope's org that the scope reaches, tracked; otherwise the 404 of a step that does not exist.</summary>
    private async Task<OnboardingPanelStep> LoadAsync(HostScope scope, Guid stepId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var step = await db.OnboardingPanelSteps.FirstOrDefaultAsync(s => s.Id == stepId && s.OrgId == scope.OrgId, ct);
        if (step is null || !await OnboardingReach.CanReachAsync(db, scope, step.PropertyId, ct))
        {
            throw new NotFoundException("Panel step not found.")
            {
                Code = OnboardingPanelStepCodes.NotFound,
                MessageKey = "PanelStepNotFound",
            };
        }

        return step;
    }

    private Task<OnboardingPanelStep?> FindAsync(Guid orgId, Guid? propertyId, PanelStepKind kind, string? channelCode, CancellationToken ct) =>
        db.OnboardingPanelSteps.FirstOrDefaultAsync(
            s => s.OrgId == orgId && s.PropertyId == propertyId && s.Kind == kind && s.ChannelCode == channelCode, ct);

    internal static PanelStepView ViewOf(OnboardingPanelStep step)
    {
        var info = PanelStepCatalog.Of(step.Kind);
        return new PanelStepView(
            step.Id,
            step.Kind,
            step.Status,
            step.HandoffMode,
            step.PropertyId,
            step.ChannelCode,
            info.Context,
            step.ActivityId,
            IsOpen(step) ? step.NextVerificationAt : null,
            step.LastVerificationOutcome,
            step.Status == PanelStepStatus.Started && step.LastVerificationOutcome == PanelVerificationOutcomes.Unverifiable,
            new PanelStepResume(step.ResumeWizardType, step.ResumeDraftEntityId, step.ResumeStepCode),
            step.StartedAt,
            step.CompletedAt);
    }

    // ─── Start ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A new window of checks and a new hand-over for a step that is started again or found not valid.</summary>
    private static void Restart(OnboardingPanelStep step, PanelHandoffMode mode, string state, DateTime now, PanelStepResume? resume)
    {
        step.Status = PanelStepStatus.Started;
        step.HandoffMode = mode;
        step.HandoffNonceHash = HashOf(state);
        step.HandoffExpiresAt = now.AddMinutes(PanelStepLimits.StateLifetimeMinutes);
        step.CallbackConsumedAt = null;
        step.StartedAt = now;
        step.VerificationAttempts = 0;
        step.NextVerificationAt = now + PanelStepLimits.BackoffAfter(1);
        step.LastVerificationOutcome = null;
        step.VerifiedAt = null;
        step.CompletedAt = null;
        step.CompletedByUserId = null;
        if (resume is not null)
        {
            step.ResumeWizardType = resume.WizardType;
            step.ResumeDraftEntityId = resume.DraftEntityId;
            step.ResumeStepCode = resume.StepCode;
        }
    }

    /// <summary>The code of the channel of a channel access (1 to 40 characters); none for the other kinds.</summary>
    private static string? CheckedChannel(PanelStepKind kind, string? channelCode)
    {
        var code = channelCode?.Trim();
        if (kind == PanelStepKind.ChannelAccess)
        {
            return string.IsNullOrEmpty(code) || code.Length > OnboardingPanelStep.ChannelCodeMaxLength
                ? throw new DomainRuleException(OnboardingPanelStepCodes.ChannelInvalid, "PanelStepChannelInvalid")
                : code;
        }

        return string.IsNullOrEmpty(code)
            ? null
            : throw new DomainRuleException(OnboardingPanelStepCodes.ChannelInvalid, "PanelStepChannelInvalid");
    }

    /// <summary>The point to resume asked for, or <c>null</c> when the request names none (what is stored stays).</summary>
    private static PanelStepResume? CheckedResume(PanelStepStartRequest request)
    {
        var stepCode = request.ResumeStepCode?.Trim();
        if (string.IsNullOrEmpty(stepCode) && request.ResumeWizardType is null && request.ResumeDraftEntityId is null)
            return null;

        var wizard = request.ResumeWizardType ?? WizardType.OnboardingActivation;
        if (!Enum.IsDefined(wizard)
            || (stepCode is not null && stepCode.Length > OnboardingPanelStep.ResumeStepCodeMaxLength)
            || request.ResumeDraftEntityId == Guid.Empty)
        {
            throw new DomainRuleException(OnboardingPanelStepCodes.ResumeInvalid, "PanelStepResumeInvalid");
        }

        return new PanelStepResume(wizard, request.ResumeDraftEntityId, string.IsNullOrEmpty(stepCode) ? null : stepCode);
    }

    private static void CheckUserId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > OnboardingPanelStep.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(userId));
    }

    /// <summary>The callback address of CasaZen as configured (absolute https, or http on this machine), no credentials and no fragment; otherwise <c>null</c>.</summary>
    internal static string? ParseCallbackBase(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        var secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        return secure && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) ? uri.AbsoluteUri : null;
    }

    /// <summary>The callback address of a step: the configured page with the step and the state (<c>?step=…&amp;state=…</c>).</summary>
    private static string CallbackUrl(string callbackBase, Guid stepId, string state) =>
        $"{callbackBase}{(callbackBase.Contains('?', StringComparison.Ordinal) ? '&' : '?')}step={stepId:D}&state={Uri.EscapeDataString(state)}";

    private static string NewState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateBytes));

    /// <summary>The SHA-256 of a state, lowercase hex: what the table holds.</summary>
    internal static string HashOf(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string LockKey(Guid stepId) => $"step:{stepId:N}";

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
