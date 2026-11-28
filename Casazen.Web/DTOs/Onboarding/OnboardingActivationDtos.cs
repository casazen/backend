using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

public sealed class FlowActivationRequestDto
{
    public List<Guid>? PropertyIds { get; set; }
}

public sealed class FlowActivationResultDto
{
    public OnboardingFlow Flow { get; set; }

    public List<FlowActivationOutcomeDto> Outcomes { get; set; } = [];
}

/// <summary>
/// The outcome for one property. <c>outcome</c>: <c>activated</c>, <c>alreadyActive</c>, <c>prerequisitesNotMet</c>, <c>notAvailable</c>,
/// <c>notFound</c> (activation); <c>deactivated</c>, <c>alreadyInactive</c>, <c>notFound</c> (deactivation). <c>unmetPrerequisites</c>: the keys
/// of what does not hold (<c>connection</c>, <c>mapping</c>, <c>singleSender</c>, <c>content</c>, <c>cin</c>, <c>taxRate</c>, <c>messageSync</c>,
/// <c>issuer</c>, <c>bookings</c>: the list only grows). <c>reason</c>: the code of a <c>prerequisitesNotMet</c> or <c>notAvailable</c> outcome. Codes only.
/// </summary>
public sealed class FlowActivationOutcomeDto
{
    public Guid PropertyId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public List<string> UnmetPrerequisites { get; set; } = [];

    public string? Reason { get; set; }
}

public sealed class FlowTestRunRequestDto
{
    public Guid PropertyId { get; set; }

    public Guid? BookingId { get; set; }
}

public sealed class FlowTestRunDto
{
    public Guid PropertyId { get; set; }

    public Guid BookingId { get; set; }

    public List<FlowCheckDto> Checks { get; set; } = [];
}

public sealed class FlowCheckDto
{
    public OnboardingFlow Flow { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public List<string> Reasons { get; set; } = [];
}
