using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The guided steps of the Octorate panel (NF-107, ONB-04, E10/F01/US03/T01): the connection of the police and of ISTAT and the access to a
/// channel, which Octorate lets a person do only from its own panel. CasaZen guides the person there and back, checks the result where it can read
/// it (D17, D31) and otherwise asks the person to confirm, and raises a "To do" so that a step left half way never disappears unnoticed.
/// </summary>
/// <remarks>
/// <para><b>Durable first.</b> <see cref="StartAsync"/> stores the step, the hand-over (the mode, a random correlation nonce kept as its hash) and
/// the point to resume before it returns any external address, and only then raises the "To do". The address carries no org, property or person.</para>
/// <para><b>Quota.</b> The state is stored. <see cref="GetStatusAsync"/> runs the verifier only when the stored schedule says it is due (a wait that
/// doubles up to <see cref="PanelStepLimits.MaxCheckIntervalSeconds"/> seconds), under a lock of the step, so a page that polls never burns the
/// quota of Octorate. <see cref="VerifyAsync"/> is the explicit trigger (the window was closed, the tab got the focus again): it is held to a minimum
/// gap from the previous check.</para>
/// <para><b>Confirmation.</b> <see cref="ConfirmAsync"/> is the technical fallback: only after a real verification stored «could not tell»
/// (<see cref="PanelVerificationOutcomes.Unverifiable"/>); 409 <see cref="OnboardingPanelStepCodes.VerificationRequired"/> before any verification,
/// 409 <see cref="OnboardingPanelStepCodes.NotValid"/> after a «not valid».</para>
/// <para><b>Reach.</b> Every operation is bound in SQL to the org and to what the scope reaches (AM-03): a step of a property the caller does not
/// reach, or one of the whole org for a caller who does not reach the whole org, is a 404, the same as a step that does not exist.</para>
/// </remarks>
public interface IOnboardingPanelStepService
{
    /// <summary>
    /// Starts, or starts again, the step. Idempotent: one row for each org, property, kind and channel. A step already verified or confirmed, or that
    /// support is handling, is answered as it is and nothing is restarted. A step started or found not valid gets a new hand-over (a new nonce) and a
    /// fresh window of checks, unless a check already ran in its window: then the attempts, the wait and the status go on as they were, so starting
    /// again never gives back the first check at once. 409 <c>octorate_panel_url_missing</c> when the address of the panel is not configured.
    /// </summary>
    Task<PanelStepStart> StartAsync(HostScope scope, string userId, PanelStepStartRequest request, CancellationToken ct);

    Task<PanelStepView> GetStatusAsync(HostScope scope, Guid stepId, CancellationToken ct);

    /// <summary>The explicit trigger of a verification (focus, close of the window): runs it when the previous check is old enough, otherwise answers the stored state.</summary>
    Task<PanelStepView> VerifyAsync(HostScope scope, Guid stepId, CancellationToken ct);

    Task<PanelStepView> ConfirmAsync(HostScope scope, string userId, Guid stepId, CancellationToken ct);

    /// <summary>
    /// The callback of a panel that sends the person back (<see cref="PanelHandoffMode.RedirectCallback"/>): consumes the state, which is valid once and
    /// before its expiry, then verifies the step. 422 <see cref="OnboardingPanelStepCodes.StateInvalid"/> for any state that is unknown, used, expired or
    /// of another step (always the same answer); 409 <see cref="OnboardingPanelStepCodes.CallbackUnsupported"/> for a step with no callback.
    /// </summary>
    Task<PanelStepView> CompleteCallbackAsync(HostScope scope, Guid stepId, string? state, CancellationToken ct);

    Task<IReadOnlyList<PanelStepView>> ListAsync(HostScope scope, CancellationToken ct);
}

/// <summary>What the host asks to start: the kind, where (the property, the channel) and the point of the wizard to come back to.</summary>
/// <param name="Kind">The kind of step.</param>
/// <param name="PropertyId">The property the step is about; <c>null</c> for a step of the whole org (needs a scope over the whole org).</param>
/// <param name="ChannelCode">The code of the channel, required for <see cref="PanelStepKind.ChannelAccess"/> and refused for the others.</param>
/// <param name="ResumeWizardType">The wizard the host leaves; with <see cref="ResumeStepCode"/> or <see cref="ResumeDraftEntityId"/> and no wizard it is the activation.</param>
/// <param name="ResumeDraftEntityId">What that wizard edits (the id of its draft entity): a reference, never a copy of the draft.</param>
/// <param name="ResumeStepCode">The step of the wizard to come back to (at most <see cref="OnboardingPanelStep.ResumeStepCodeMaxLength"/> characters).</param>
public sealed record PanelStepStartRequest(
    PanelStepKind Kind,
    Guid? PropertyId = null,
    string? ChannelCode = null,
    WizardType? ResumeWizardType = null,
    Guid? ResumeDraftEntityId = null,
    string? ResumeStepCode = null);

public sealed record PanelStepResume(WizardType? WizardType, Guid? DraftEntityId, string? StepCode);

/// <summary>
/// The stored state of a step as the host reads it. Codes, ids and instants only: no credential, no nonce, no person.
/// </summary>
/// <param name="Id">The step.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="HandoffMode">How the person is sent to the panel and comes back.</param>
/// <param name="PropertyId">The property of the step, if it has one.</param>
/// <param name="ChannelCode">The channel of a channel access.</param>
/// <param name="InstructionsKey">The code of the instructions the client shows with the images (<c>panelPolice</c>, <c>panelIstat</c>, <c>panelChannelAccess</c>).</param>
/// <param name="ActivityId">The "To do" that stands for the step while it is open; <c>null</c> once it is closed.</param>
/// <param name="NextCheckAt">When the next verification is due (UTC); <c>null</c> when the step is not open for verification.</param>
/// <param name="LastVerificationOutcome">What the last verification found (<see cref="PanelVerificationOutcomes"/>); <c>null</c> = never verified.</param>
/// <param name="CanConfirm">True when the person may confirm the step by hand: a verification stored that CasaZen cannot tell.</param>
/// <param name="Resume">The point of the wizard to come back to.</param>
/// <param name="StartedAt">When the current hand-over began (UTC).</param>
/// <param name="CompletedAt">When the step was completed (UTC); <c>null</c> while it is not.</param>
public sealed record PanelStepView(
    Guid Id,
    PanelStepKind Kind,
    PanelStepStatus Status,
    PanelHandoffMode HandoffMode,
    Guid? PropertyId,
    string? ChannelCode,
    string InstructionsKey,
    Guid? ActivityId,
    DateTime? NextCheckAt,
    string? LastVerificationOutcome,
    bool CanConfirm,
    PanelStepResume Resume,
    DateTime StartedAt,
    DateTime? CompletedAt);

/// <summary>
/// A step that was started: its stored state and the addresses to go to. <paramref name="PanelUrl"/> is, for the access to a channel whose code is a
/// number, the page of that portal's connection on the panel (<c>{base}/user/channelmanager/edit.xhtml?portal={code}</c>, verified for Booking.com
/// only), and otherwise the configured address of the panel as it is: no org, property or person in it, the portal code at most;
/// <paramref name="CallbackUrl"/> is set only for <see cref="PanelHandoffMode.RedirectCallback"/>.
/// </summary>
public sealed record PanelStepStart(PanelStepView Step, string PanelUrl, string? CallbackUrl);

public sealed record PanelStepKindInfo(PanelStepKind Kind, string Code, string Context, OnboardingPartKey Part);

public static class PanelStepCatalog
{
    public static IReadOnlyList<PanelStepKindInfo> All { get; } =
    [
        new(PanelStepKind.PoliceConnection, "policeConnection", OnboardingAssistContexts.PanelPolice, OnboardingPartKey.Compliance),
        new(PanelStepKind.IstatConnection, "istatConnection", OnboardingAssistContexts.PanelIstat, OnboardingPartKey.Compliance),
        new(PanelStepKind.ChannelAccess, "channelAccess", OnboardingAssistContexts.PanelChannelAccess, OnboardingPartKey.Channels),
    ];

    public static PanelStepKindInfo Of(PanelStepKind kind) =>
        All.FirstOrDefault(info => info.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of panel step.");

    public static bool TryOfContext(string? context, [NotNullWhen(true)] out PanelStepKindInfo? info)
    {
        info = All.FirstOrDefault(candidate => string.Equals(candidate.Context, context, StringComparison.Ordinal));
        return info is not null;
    }

    public static string CodeOf<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

public static class OnboardingPanelStepCodes
{
    public const string NotFound = "panel_step_not_found";

    /// <summary>409: the confirmation is refused because no verification has found that CasaZen cannot tell.</summary>
    public const string VerificationRequired = "panel_step_verification_required";

    /// <summary>409: the confirmation is refused because the last verification found the step not done or not valid.</summary>
    public const string NotValid = "panel_step_not_valid";

    public const string AssistOpen = "panel_step_assist_open";

    public const string CallbackUnsupported = "panel_step_callback_unsupported";

    public const string StateInvalid = "panel_step_state_invalid";

    public const string KindInvalid = "panel_step_kind_invalid";

    public const string ChannelInvalid = "panel_step_channel_invalid";

    public const string ResumeInvalid = "panel_step_resume_invalid";

    public const string NeedsWholeOrg = "onboarding_needs_whole_org";

    public const string TodoEntityType = "OnboardingPanelStep";

    public const string ReasonPending = "onboarding_panel_step_pending";
}

public static class PanelStepLimits
{
    public const int FirstCheckSeconds = 30;

    public const int MaxCheckIntervalSeconds = 600;

    public const int ExplicitCheckMinGapSeconds = 10;

    public const int MaxListed = 200;

    public const int StateLifetimeMinutes = 30;

    public const int StateMaxLength = 128;

    public static TimeSpan BackoffAfter(int attempt)
    {
        var seconds = (double)FirstCheckSeconds;
        for (var i = 1; i < attempt && seconds < MaxCheckIntervalSeconds; i++)
            seconds *= 2;

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxCheckIntervalSeconds));
    }
}
