using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// The plan of the activation of the caller's org (<c>GET api/onboarding/plan</c>, NF-104): the eleven parts in order, the five codes of «What
/// you need», the connected path of UX-10 and whether the org is activated. Codes, counts and ids only: the client translates the codes.
/// </summary>
public sealed class OnboardingPlanDto
{
    public List<OnboardingPlanPartDto> Parts { get; set; } = [];

    /// <summary>The five codes of «What you need»: <c>alloggiatiCredentials</c>, <c>istatCredentials</c>, <c>fiscalData</c>, <c>otaAccess</c>, <c>lockCode</c>.</summary>
    public List<string> Needs { get; set; } = [];

    public OnboardingPathDto Path { get; set; } = new();

    /// <summary>True when every property the caller reaches has every flow that applies to it active.</summary>
    public bool Activated { get; set; }
}

/// <summary>
/// One part of the plan. <c>code</c>: <c>octorateConnection</c>, <c>properties</c>, <c>channels</c>, <c>listings</c>, <c>pricesRules</c>,
/// <c>compliance</c>, <c>identification</c>, <c>messagesAi</c>, <c>fiscalDocuments</c>, <c>partners</c>, <c>activation</c>. <c>state</c>:
/// <c>done</c>, <c>open</c>, <c>deferred</c>, <c>blocked</c>, <c>notAvailable</c>. <c>reason</c>: the stable code of what is missing, <c>null</c>
/// for a part that is done. <c>done</c> of <c>total</c>: the progress. <c>launchTarget</c>: the wizard that does the part.
/// </summary>
public sealed class OnboardingPlanPartDto
{
    public string Code { get; set; } = string.Empty;

    public int Order { get; set; }

    public string State { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public List<string> Requires { get; set; } = [];

    public bool Deferrable { get; set; }

    public List<string> Needs { get; set; } = [];

    /// <summary>When the "To do" of a deferral or an exit is due (UTC); <c>null</c> when there is none.</summary>
    public DateTime? RemindAt { get; set; }

    /// <summary>The open «Do it for me» request of the part, if any.</summary>
    public OnboardingAssistDto? Assist { get; set; }

    public string LaunchTarget { get; set; } = string.Empty;
}

/// <summary>An open «Do it for me» request: its id, where it stands (<c>Requested</c>, <c>InProgress</c>), what it is about and when it was asked.</summary>
public sealed class OnboardingAssistDto
{
    public Guid Id { get; set; }

    public OnboardingAssistStatus Status { get; set; }

    public string Context { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }
}

/// <summary>
/// The connected path of UX-10 (<c>GET api/onboarding/path</c>, NF-10): its six steps mapped to the parts of the plan, and <c>next</c>, the first
/// visible step (<c>null</c> when none is).
/// </summary>
public sealed class OnboardingPathDto
{
    public string Code { get; set; } = string.Empty;

    public List<OnboardingPathStepDto> Steps { get; set; } = [];

    public string? Next { get; set; }
}

/// <summary>
/// One step of the path: <c>connectChannels</c>, <c>checkListings</c>, <c>pricesCalendar</c>, <c>checkInCompliance</c>, <c>messagesAi</c>,
/// <c>fiscalDocuments</c>. <c>visible</c> is true only while there is something to do in it.
/// </summary>
public sealed class OnboardingPathStepDto
{
    public string Code { get; set; } = string.Empty;

    public int Order { get; set; }

    public List<string> Parts { get; set; } = [];

    public string State { get; set; } = string.Empty;

    public bool Visible { get; set; }
}

/// <summary>The body of <c>POST api/onboarding/parts/{key}/defer</c>: the reminder (UTC), or nothing for tomorrow at 09:00 in Rome.</summary>
public sealed class DeferOnboardingPartDto
{
    public DateTime? RemindAt { get; set; }
}

/// <summary>
/// What a deferral, an exit or a resume left on a part: <c>state</c> <c>deferred</c> or <c>open</c>, the reminder and the "To do" that stands for
/// the part (<c>null</c> after a resume).
/// </summary>
public sealed class OnboardingPartChangeDto
{
    public string Code { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public DateTime? RemindAt { get; set; }

    public Guid? TodoId { get; set; }
}
