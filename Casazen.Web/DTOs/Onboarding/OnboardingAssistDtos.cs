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
/// The answer of <c>POST api/onboarding/panel-steps</c>: the stored state, <c>panelUrl</c> (the address of the panel as configured: no org, property
/// or person in it) and <c>callbackUrl</c> (only for <c>redirectCallback</c>: the CasaZen page the panel sends the person back to, with the step and
/// the state to present).
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

    public OnboardingAssistStatus Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>The note of CasaZen support: at most 500 characters, no e-mail address.</summary>
    public string? StaffNote { get; set; }

    /// <summary>The "To do" that stands for the request while it is open.</summary>
    public Guid? ActivityId { get; set; }
}

/// <summary>The body of <c>PUT api/admin/onboarding/assist-requests/{id}</c>: <c>inProgress</c>, <c>done</c> or <c>cancelled</c>, and an optional note.</summary>
public sealed class UpdateAssistRequestDto
{
    public OnboardingAssistStatus? Status { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// A request in the staff queue: ids, context codes, the property and its comune, who has it and the note. The reference to the draft is the type
/// of the wizard and what it edits: <b>never the draft</b>, never a credential, never the person who asked.
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

    public OnboardingAssistStatus Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? AssignedToUserId { get; set; }

    public string? StaffNote { get; set; }

    public WizardType? DraftWizardType { get; set; }

    public Guid? DraftEntityId { get; set; }
}
