using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

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

    public WizardType? ResumeWizardType { get; set; }

    public Guid? ResumeDraftEntityId { get; set; }

    public string? ResumeStepCode { get; set; }
}

public sealed class PanelStepCallbackDto
{
    public string? State { get; set; }
}

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

public sealed class AssistRequestDto
{
    public Guid Id { get; set; }

    public string Part { get; set; } = string.Empty;

    public Guid? PropertyId { get; set; }

    public string Context { get; set; } = string.Empty;

    public string? ChannelCode { get; set; }

    public OnboardingAssistStatus Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? StaffNote { get; set; }

    public Guid? ActivityId { get; set; }

    public string? Outcome { get; set; }

    public string? HostNote { get; set; }

    public DateTime? ReopenedAt { get; set; }
}

public sealed class UpdateAssistRequestDto
{
    public OnboardingAssistStatus? Status { get; set; }

    public string? Note { get; set; }
}

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

    public string? ChannelCode { get; set; }

    public OnboardingAssistStatus Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? AssignedToUserId { get; set; }

    public string? StaffNote { get; set; }

    public WizardType? DraftWizardType { get; set; }

    public Guid? DraftEntityId { get; set; }

    public string? Outcome { get; set; }

    public string? HostNote { get; set; }

    public DateTime? ReopenedAt { get; set; }
}
