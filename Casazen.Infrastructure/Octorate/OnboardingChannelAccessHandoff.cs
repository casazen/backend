using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IChannelAccessHandoff"/> over the <see cref="OnboardingPanelStep"/> rows of epic E10 (NF-107): the durable handoff of the person
/// to the Octorate panel for the access to one OTA channel (E02/F01/US03). It is the minimal form of the <c>channelAccess</c> panel step: the
/// service of the panel steps of E10 owns the callback, the status endpoints and the verifiers, and replaces this class by registering its
/// own <see cref="IChannelAccessHandoff"/>; until then this one starts the step, checks the access and takes the confirmation.
/// </summary>
/// <remarks>
/// <para><b>Written before it is returned.</b> <see cref="StartAsync"/> saves the step, the single hand-over and the resume point (the batch
/// and the channel the person comes back to) before it returns the address of the panel, so a person who closes the tab, reloads or changes
/// device finds the exact step. The address is the configured panel address (<c>Octorate:FrontendBaseUrl</c>) as it is: absolute https, no
/// query and no fragment, so it carries no organization, property or person data.</para>
/// <para><b>How the access is checked.</b> The panel offers no callback for the login to a channel, so the handoff is
/// <see cref="PanelHandoffMode.ManagedWindow"/> and CasaZen reads, at most once for each backoff window, the connections the org's token
/// sees: a connection of the channel that is authorized (<see cref="OctorateConnectionSummary.IsAuthorized"/>) is the proof, and completes
/// the step, when it is new to this hand-over: the person logged in on it after the hand-over began, or it belongs to an accommodation the
/// batch is about (<see cref="IsProofAsync"/>). A connection the org already had working for another property is no proof. When the checks
/// run out (<see cref="OctorateExistingInventoryOptions.AccessCheckMaxAttempts"/>) without that proof the outcome is stored as «could not
/// tell» and only then may the person confirm: the confirmation is the fallback, never the normal path.</para>
/// <para>No credential is ever stored or logged: the nonce column stays empty (no callback), the log lines carry ids and codes.</para>
/// </remarks>
public sealed class OnboardingChannelAccessHandoff(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOptions<OctorateOptions> options,
    TimeProvider timeProvider,
    ILogger<OnboardingChannelAccessHandoff> logger) : IChannelAccessHandoff
{
    /// <summary>The step of the wizard the person comes back to, stored as the resume point of the panel step.</summary>
    public const string ResumeStepCode = "existingInventoryAccess";

    public async Task<ChannelAccessHandoffStart> StartAsync(
        Guid orgId,
        string channelCode,
        ChannelAccessResumePoint resume,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resume);
        var code = CheckedChannel(channelCode);
        var panelUrl = ParsePanelUrl(options.Value.FrontendBaseUrl)
            ?? throw new DomainConflictException("octorate_panel_url_missing", "OctoratePanelUrlMissing");

        // A parallel start of the same step loses on the unique index of the steps: it reads the winner's row and applies the same update.
        for (var attempt = 0; ; attempt++)
        {
            var step = await FindAsync(orgId, code, ct);
            var now = Now();
            if (step is null)
            {
                step = new OnboardingPanelStep
                {
                    OrgId = orgId,
                    PropertyId = null,
                    Kind = PanelStepKind.ChannelAccess,
                    ChannelCode = code,
                    Status = PanelStepStatus.Started,
                    HandoffMode = PanelHandoffMode.ManagedWindow,
                    StartedAt = now,
                    NextVerificationAt = now.Add(options.Value.ExistingInventory.BackoffAfter(1)),
                };
                db.OnboardingPanelSteps.Add(step);
            }
            else if (step.Status is PanelStepStatus.Started or PanelStepStatus.NotValid)
            {
                // A step the person left half way, or whose check found the access not valid, starts again with a fresh window of checks.
                step.Status = PanelStepStatus.Started;
                step.HandoffMode = PanelHandoffMode.ManagedWindow;
                step.StartedAt = now;
                step.VerificationAttempts = 0;
                step.LastVerificationOutcome = null;
                step.NextVerificationAt = now.Add(options.Value.ExistingInventory.BackoffAfter(1));
            }

            step.ResumeWizardType = WizardType.OnboardingActivation;
            step.ResumeDraftEntityId = resume.BatchId;
            step.ResumeStepCode = ResumeStepCode;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt == 0 && OrgTeamAccess.IsUniqueViolation(ex, OnboardingPanelStep.StepIndexName))
            {
                // Only the row that lost is forgotten: the context is shared with the import, which holds other rows.
                db.Entry(step).State = EntityState.Detached;
                continue;
            }

            logger.LogInformation(
                "Channel access step {StepId} of org {OrgId} for channel {ChannelCode} started: batch {BatchId}",
                step.Id, orgId, code, resume.BatchId);
            return new ChannelAccessHandoffStart(step.Id, step.HandoffMode, panelUrl);
        }
    }

    public async Task<ChannelAccessSnapshot> GetAsync(Guid orgId, string channelCode, bool verifyIfDue, CancellationToken ct)
    {
        var code = CheckedChannel(channelCode);
        var step = await FindAsync(orgId, code, ct);
        if (step is null)
            return new ChannelAccessSnapshot(null, ChannelAccessState.NotStarted);

        if (verifyIfDue
            && step.Status == PanelStepStatus.Started
            && step.LastVerificationOutcome != PanelVerificationOutcomes.Unverifiable
            && step.NextVerificationAt is { } due
            && due <= Now())
        {
            await VerifyAsync(step, code, ct);
        }

        return new ChannelAccessSnapshot(step.Id, StateOf(step));
    }

    public async Task ConfirmAsync(Guid orgId, string userId, string channelCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var code = CheckedChannel(channelCode);
        var step = await FindAsync(orgId, code, ct)
            ?? throw new DomainConflictException("panel_step_verification_required", "ChannelAccessVerificationRequired");

        if (step.Status is PanelStepStatus.Verified or PanelStepStatus.Confirmed)
            return;

        if (step.Status == PanelStepStatus.NotValid)
            throw new DomainConflictException("panel_step_not_valid", "ChannelAccessNotValid");

        // Only after a check stored «could not tell»: the confirmation is the technical fallback, never the normal path.
        if (step.LastVerificationOutcome != PanelVerificationOutcomes.Unverifiable)
            throw new DomainConflictException("panel_step_verification_required", "ChannelAccessVerificationRequired");

        var now = Now();
        step.Status = PanelStepStatus.Confirmed;
        step.CompletedAt = now;
        step.CompletedByUserId = userId.Length <= OnboardingPanelStep.UserIdMaxLength ? userId : userId[..OnboardingPanelStep.UserIdMaxLength];
        step.NextVerificationAt = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Channel access step {StepId} of org {OrgId} for channel {ChannelCode} confirmed by user {UserId}", step.Id, orgId, code, userId);
    }

    /// <summary>
    /// The address of the panel from the configured base: absolute https for a named host, no credentials, no query, no fragment, the default
    /// port and no space, backslash or control character; otherwise <c>null</c>. What is returned is the parse the address is built from.
    /// </summary>
    internal static string? ParsePanelUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        foreach (var c in text)
        {
            if (c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme == Uri.UriSchemeHttps
               && uri.HostNameType == UriHostNameType.Dns
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.IsNullOrEmpty(uri.Query)
               && string.IsNullOrEmpty(uri.Fragment)
               && uri.IsDefaultPort
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>The state the import shows for a step: what the stored status and the last check say.</summary>
    internal static ChannelAccessState StateOf(OnboardingPanelStep step) => step.Status switch
    {
        PanelStepStatus.Verified => ChannelAccessState.Verified,
        PanelStepStatus.Confirmed => ChannelAccessState.Confirmed,
        PanelStepStatus.NotValid => ChannelAccessState.NotValid,
        _ when step.LastVerificationOutcome == PanelVerificationOutcomes.Unverifiable => ChannelAccessState.NeedsConfirmation,
        _ when step.VerificationAttempts > 0 => ChannelAccessState.Verifying,
        _ => ChannelAccessState.Waiting,
    };

    /// <summary>
    /// One check of the access: the attempt is claimed first (the next one is not due before the backoff), then the connections of the org
    /// are read; an authorized connection of the channel completes the step, and the last attempt without it stores «could not tell». An
    /// error of Octorate counts as an attempt and decides nothing.
    /// </summary>
    private async Task VerifyAsync(OnboardingPanelStep step, string channelCode, CancellationToken ct)
    {
        var settings = options.Value.ExistingInventory;
        var attempt = step.VerificationAttempts + 1;
        step.VerificationAttempts = attempt;
        step.NextVerificationAt = Now().Add(settings.BackoffAfter(attempt));
        await db.SaveChangesAsync(ct);

        var ofChannel = new List<OctorateConnectionSummary>();
        try
        {
            OctorateConnectionSummary[] connections;
            using (orgScope.Use(step.OrgId))
                connections = await client.GetOrgConnectionsAsync(ct);

            ofChannel.AddRange(connections.Where(c => string.Equals(c.ChannelId, channelCode, StringComparison.Ordinal) && c.IsAuthorized));
        }
        catch (OctorateException ex)
        {
            logger.LogWarning(
                "Channel access check {Attempt} of org {OrgId} for channel {ChannelCode} could not read the connections ({Error})",
                attempt, step.OrgId, channelCode, ex.Code);
        }

        var authorized = ofChannel.Count > 0 && await IsProofAsync(step, ofChannel, ct);
        var now = Now();
        if (authorized)
        {
            step.Status = PanelStepStatus.Verified;
            step.LastVerificationOutcome = PanelVerificationOutcomes.Verified;
            step.VerifiedAt = now;
            step.CompletedAt = now;
            step.NextVerificationAt = null;
        }
        else if (attempt >= settings.EffectiveAccessCheckMaxAttempts)
        {
            step.LastVerificationOutcome = PanelVerificationOutcomes.Unverifiable;
            step.NextVerificationAt = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether an authorized connection of the channel proves the access was given for THIS hand-over. One that was already authorized before the
    /// person was sent to the panel (the org has a working connection of the channel for another property) says nothing about it, or the step
    /// would be verified at the first check without the person giving anything. It is a proof when the person logged in on it after the
    /// hand-over began (<see cref="OctorateConnectionSummary.LoginAt"/>), or when it belongs to an accommodation the batch is about.
    /// </summary>
    private async Task<bool> IsProofAsync(OnboardingPanelStep step, IReadOnlyList<OctorateConnectionSummary> authorized, CancellationToken ct)
    {
        if (authorized.Any(c => c.LoginAt is { } loggedAt && loggedAt >= step.StartedAt))
            return true;

        var accommodations = await BatchAccommodationsAsync(step, ct);
        return authorized.Any(c => accommodations.Contains(c.AccommodationId));
    }

    /// <summary>
    /// The accommodations of Octorate the batch of the hand-over is about: the ones its properties are confirmed to. The batch is the resume
    /// point of the step. The whole inventory names no property, so no accommodation stands for it: only a login after the hand-over proves it.
    /// </summary>
    private async Task<HashSet<string>> BatchAccommodationsAsync(OnboardingPanelStep step, CancellationToken ct)
    {
        var accommodations = new HashSet<string>(StringComparer.Ordinal);
        if (step.ResumeDraftEntityId is not { } batchId)
            return accommodations;

        var batch = await db.OctorateInventoryImportBatches
            .AsNoTracking()
            .Where(b => b.OrgId == step.OrgId && b.Id == batchId)
            .Select(b => new { b.ScopeKind, b.ScopePropertyIdsJson })
            .FirstOrDefaultAsync(ct);
        if (batch is null || batch.ScopeKind == OctorateInventoryScopeKind.AllInventory)
            return accommodations;

        var propertyIds = ExistingInventoryImportService.ParseIds(batch.ScopePropertyIdsJson);
        if (propertyIds.Count == 0)
            return accommodations;

        var confirmed = await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.OrgId == step.OrgId && m.Status == OctorateMappingStatus.Confirmed && m.PropertyId != null && propertyIds.Contains(m.PropertyId.Value))
            .Select(m => m.OctorateAccommodationId)
            .ToListAsync(ct);
        accommodations.UnionWith(confirmed);
        return accommodations;
    }

    /// <summary>The step of the org for the channel: one row for each org, kind and channel, with no property (unique index).</summary>
    private Task<OnboardingPanelStep?> FindAsync(Guid orgId, string channelCode, CancellationToken ct) =>
        db.OnboardingPanelSteps.FirstOrDefaultAsync(
            s => s.OrgId == orgId && s.PropertyId == null && s.Kind == PanelStepKind.ChannelAccess && s.ChannelCode == channelCode, ct);

    private static string CheckedChannel(string channelCode)
    {
        var code = channelCode?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > OnboardingPanelStep.ChannelCodeMaxLength)
            throw new ArgumentException($"A channel code has 1 to {OnboardingPanelStep.ChannelCodeMaxLength} characters.", nameof(channelCode));

        return code;
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
