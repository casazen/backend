using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

// The guided steps of the Octorate panel and the «Do it for me» requests (NF-107, NF-15, E10/F01/US03/T01). Codes, ids and instants only:
// no credential, no nonce, no draft, no person.

/// <summary>
/// The body of <c>POST api/onboarding/panel-steps</c>: the kind (<c>policeConnection</c>, <c>istatConnection</c>, <c>channelAccess</c>), the
/// property the step is about (none for the whole org), the channel of a channel access, and the point of the wizard to come back to (the draft is
/// named by its type and what it edits, never carried).
/// </summary>
public sealed class StartPanelStepDto
{
    public PanelStepKind Kind { get; set; }

    public Guid? PropertyId { get; set; }

    public string? ChannelCode { get; set; }

    /// <summary>The wizard the host leaves; the activation when the body names a step or a draft and no wizard.</summary>
    public WizardType? ResumeWizardType { get; set; }

    public Guid? ResumeDraftEntityId { get; set; }

    public string? ResumeStepCode { get; set; }
}

/// <summary>The body of <c>POST api/onboarding/panel-steps/{id}/callback</c>: the state the panel sent the person back with.</summary>
public sealed class PanelStepCallbackDto
{
    public string? State { get; set; }
}

/// <summary>The point of a wizard a step comes back to: a reference to the draft, never its content.</summary>
public sealed class PanelStepResumeDto
{
    public WizardType? WizardType { get; set; }

    public Guid? DraftEntityId { get; set; }

    public string? StepCode { get; set; }
}

/// <summary>
/// The stored state of a guided step. <c>kind</c>: <c>policeConnection</c>, <c>istatConnection</c>, <c>channelAccess</c>. <c>status</c>:
/// <c>started</c>, <c>verified</c>, <c>confirmed</c>, <c>notValid</c>, <c>assistRequested</c>. <c>handoffMode</c>: <c>redirectCallback</c> or
/// <c>managedWindow</c>. <c>instructionsKey</c>: the code of the instructions the client shows (<c>panelPolice</c>, <c>panelIstat</c>,
/// <c>panelChannelAccess</c>). <c>nextCheckAt</c>: when the server will check next (UTC), <c>null</c> when the step is not open.
/// <c>lastVerificationOutcome</c>: <c>verified</c>, <c>notValid</c> or <c>unverifiable</c>, <c>null</c> before any check. <c>canConfirm</c>: the
/// person may confirm by hand, because a check found that CasaZen cannot tell.
/// </summary>
public class PanelStepDto
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string HandoffMode { get; set; } = string.Empty;

    public Guid? PropertyId { get; set; }

    public string? ChannelCode { get; set; }

    public string InstructionsKey { get; set; } = string.Empty;

    /// <summary>The "To do" that stands for the step while it is open.</summary>
    public Guid? ActivityId { get; set; }

    public DateTime? NextCheckAt { get; set; }

    public string? LastVerificationOutcome { get; set; }

    public bool CanConfirm { get; set; }

    public PanelStepResumeDto Resume { get; set; } = new();

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// The answer of <c>POST api/onboarding/panel-steps</c>: the stored state, <c>panelUrl</c> (for a channel access whose code is a number the page of
/// that portal's connection on the panel, <c>.../user/channelmanager/edit.xhtml?portal={code}</c>, otherwise the address of the panel as configured:
/// no org, property or person in it) and <c>callbackUrl</c> (only for <c>redirectCallback</c>: the CasaZen page the panel sends the person back to,
/// with the step and the state to present).
/// </summary>
public sealed class StartedPanelStepDto : PanelStepDto
{
    public string PanelUrl { get; set; } = string.Empty;

    public string? CallbackUrl { get; set; }
}

/// <summary>
/// The body of <c>POST api/onboarding/assist-requests</c>: the part (<c>compliance</c>, <c>channels</c>...), what help is asked with
/// (<c>panelPolice</c>, <c>panelIstat</c>, <c>panelChannelAccess</c> or <c>part</c>), the property (none for the whole org) and, for a channel access,
/// the channel. The draft of the wizard is found by the server, never sent.
/// </summary>
public sealed class CreateAssistRequestDto
{
    public string? Part { get; set; }

    public string? Context { get; set; }

    public Guid? PropertyId { get; set; }

    public string? ChannelCode { get; set; }
}

/// <summary>A «Do it for me» request as the host reads it. <c>status</c>: <c>Requested</c>, <c>InProgress</c>, <c>Done</c>, <c>Cancelled</c>.</summary>
public sealed class AssistRequestDto
{
    public Guid Id { get; set; }

    public string Part { get; set; } = string.Empty;

    public Guid? PropertyId { get; set; }

    public string Context { get; set; } = string.Empty;

    /// <summary>
    /// The channel of a <c>panelChannelAccess</c> request (the portal code of the catalog): each portal has its own request, so the wizard shows it on
    /// the row of that portal only. <c>null</c> for every other context.
    /// </summary>
    public string? ChannelCode { get; set; }

    public OnboardingAssistStatus Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>The note of CasaZen support: at most 500 characters, no e-mail address.</summary>
    public string? StaffNote { get; set; }

    /// <summary>The "To do" that stands for the request while it is open.</summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// What «Fatto» recorded (E20/F03/US01/T02): <c>verified</c> («Il collegamento è attivo»: CasaZen read it on the panel) or <c>declared</c> («Il supporto
    /// dichiara di aver collegato …»: the panel could not be read); <c>null</c> while the request is open or after it was cancelled.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>What the host wrote when it reopened the request («Non funziona»).</summary>
    public string? HostNote { get; set; }

    /// <summary>When the host last reopened the request (UTC).</summary>
    public DateTime? ReopenedAt { get; set; }
}

/// <summary>The body of <c>PUT api/admin/onboarding/assist-requests/{id}</c>: <c>inProgress</c>, <c>done</c> or <c>cancelled</c>, and an optional note.</summary>
public sealed class UpdateAssistRequestDto
{
    public OnboardingAssistStatus? Status { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// The body of <c>POST api/onboarding/assist-requests/{id}/reopen</c> («Non funziona», E20/F03/US01/T02): what does not work, at most 500 characters, no
/// e-mail address. Required.
/// </summary>
public sealed class ReopenAssistRequestDto
{
    public string? Note { get; set; }
}

/// <summary>
/// One request as staff reads it (<c>GET api/admin/onboarding/assist-requests/{id}</c>, E20/F03/US01/T02): the request and the checklist of what support
/// needs, per property. Presence and dates only: never a credential.
/// </summary>
public sealed class AdminAssistRequestDetailDto
{
    public AdminAssistRequestDto Request { get; set; } = new();

    public List<AssistChecklistPropertyDto> Checklist { get; set; } = [];
}

/// <summary>
/// What support needs for one property of a request, or for the org itself (<c>propertyId</c> <c>null</c>). <c>panelUser</c>: the user of the channel
/// manager panel of the structure in the support vault. <c>policeCredentials</c>, <c>istatCredentials</c> (with <c>requirement</c>: <c>required</c>,
/// <c>notRequired</c>, <c>toVerify</c>) and <c>sender</c> are <c>null</c> for the org itself; <c>sender</c> is <c>null</c> too when nothing is stored (the
/// default applies). <c>steps</c>: the panel steps the request is about.
/// </summary>
public sealed class AssistChecklistPropertyDto
{
    public string? Country { get; set; }

    public Guid? PropertyId { get; set; }

    public string? PropertyName { get; set; }

    public string? Comune { get; set; }

    public AssistPresenceDto PanelUser { get; set; } = new();

    public AssistPresenceDto? PoliceCredentials { get; set; }

    public AssistIstatPresenceDto? IstatCredentials { get; set; }

    public AssistSenderDto? Sender { get; set; }

    public List<AssistChecklistStepDto> Steps { get; set; } = [];
}

/// <summary>Whether a credential is there and since when. Never a value.</summary>
public class AssistPresenceDto
{
    public bool Present { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Whether the ISTAT credentials are there, since when, and whether the region needs them: <c>required</c>, <c>notRequired</c>, <c>toVerify</c>.</summary>
public sealed class AssistIstatPresenceDto : AssistPresenceDto
{
    public string Requirement { get; set; } = string.Empty;
}

/// <summary>Who sends the police and ISTAT communications of the property, since when, and whether the host declared it active after that change.</summary>
public sealed class AssistSenderDto
{
    public ComplianceSenderType Sender { get; set; }

    public DateTime ChangedAt { get; set; }

    public bool ActivationDeclared { get; set; }
}

/// <summary>
/// One panel step to do: <c>kind</c> (<c>policeConnection</c>, <c>istatConnection</c>, <c>channelAccess</c>), the channel of a channel access,
/// <c>instructionsKey</c> (the guide the client shows: <c>panelPolice</c>, <c>panelIstat</c>, <c>panelChannelAccess</c>), <c>state</c>
/// (<c>notStarted</c>, <c>started</c>, <c>verified</c>, <c>confirmed</c>, <c>notValid</c>, <c>assistRequested</c>), what its last check found and when it
/// was completed.
/// </summary>
public sealed class AssistChecklistStepDto
{
    public string Kind { get; set; } = string.Empty;

    public string? ChannelCode { get; set; }

    public string InstructionsKey { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string? LastVerificationOutcome { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A request in the staff queue: ids, context codes (and the portal of a channel access), the property and its comune, who has it and the note. The
/// reference to the draft is the type of the wizard and what it edits: <b>never the draft</b>, never a credential, never the person who asked.
/// </summary>
public sealed class AdminAssistRequestDto
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public string Part { get; set; } = string.Empty;

    public Guid? PropertyId { get; set; }

    public string? PropertyName { get; set; }

    public string? Comune { get; set; }

    public string Context { get; set; } = string.Empty;

    /// <summary>The portal code of a <c>panelChannelAccess</c> request, so support knows which portal to connect; <c>null</c> for every other context.</summary>
    public string? ChannelCode { get; set; }

    public OnboardingAssistStatus Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? AssignedToUserId { get; set; }

    public string? StaffNote { get; set; }

    public WizardType? DraftWizardType { get; set; }

    public Guid? DraftEntityId { get; set; }

    /// <summary>What «Fatto» recorded: <c>verified</c> or <c>declared</c> (E20/F03/US01/T02); <c>null</c> while open or after a cancellation.</summary>
    public string? Outcome { get; set; }

    /// <summary>What the host wrote when it reopened the request («Non funziona»): at most 500 characters, no e-mail address.</summary>
    public string? HostNote { get; set; }

    /// <summary>When the host last reopened the request (UTC).</summary>
    public DateTime? ReopenedAt { get; set; }
}
