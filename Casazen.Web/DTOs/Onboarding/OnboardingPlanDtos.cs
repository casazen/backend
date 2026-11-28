using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

public sealed class OnboardingPlanDto
{
    public List<OnboardingPlanPartDto> Parts { get; set; } = [];

    public List<string> Needs { get; set; } = [];

    public OnboardingPathDto Path { get; set; } = new();

    public bool Activated { get; set; }
}

/// <summary>
/// One part of the plan. <c>code</c>: <c>octorateConnection</c>, <c>properties</c>, <c>channels</c>, <c>listings</c>, <c>pricesRules</c>,
/// <c>compliance</c>, <c>identification</c>, <c>messagesAi</c>, <c>fiscalDocuments</c>, <c>partners</c>, <c>guestRegistration</c>, <c>activation</c>.
/// <c>state</c>: <c>done</c>, <c>open</c>, <c>deferred</c>, <c>blocked</c>, <c>notAvailable</c>. <c>reason</c>: the stable code of what is missing,
/// <c>null</c> for a part that is done. <c>done</c> of <c>total</c>: the progress. <c>launchTarget</c>: the wizard that does the part.
/// <c>missing</c>: for the parts that can say which properties lack what (today <c>guestRegistration</c>), the properties that lack something, at most 50,
/// each with its reason and the step of the wizard to reopen; empty for the others and for a part that is done.
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

    public DateTime? RemindAt { get; set; }

    public OnboardingAssistDto? Assist { get; set; }

    public string LaunchTarget { get; set; } = string.Empty;

    public List<OnboardingPartMissingDto> Missing { get; set; } = [];
}

public sealed class OnboardingPartMissingDto
{
    public Guid PropertyId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}

public sealed class OnboardingAssistDto
{
    public Guid Id { get; set; }

    public OnboardingAssistStatus Status { get; set; }

    public string Context { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }
}

public sealed class OnboardingPathDto
{
    public string Code { get; set; } = string.Empty;

    public List<OnboardingPathStepDto> Steps { get; set; } = [];

    public string? Next { get; set; }
}

public sealed class OnboardingPathStepDto
{
    public string Code { get; set; } = string.Empty;

    public int Order { get; set; }

    public List<string> Parts { get; set; } = [];

    public string State { get; set; } = string.Empty;

    public bool Visible { get; set; }
}

public sealed class DeferOnboardingPartDto
{
    public DateTime? RemindAt { get; set; }
}

public sealed class OnboardingPartChangeDto
{
    public string Code { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public DateTime? RemindAt { get; set; }

    public Guid? TodoId { get; set; }
}
