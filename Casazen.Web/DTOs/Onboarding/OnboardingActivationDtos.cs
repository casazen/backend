using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// The body of <c>POST api/onboarding/flows/{flow}/activate</c> and <c>deactivate</c>: the properties, 1 to <c>Onboarding:MaxPropertiesPerCall</c>
/// (500) distinct ids. An empty or missing list is 422 <c>onboarding_property_ids_empty</c>: it never means «all».
/// </summary>
public sealed class FlowActivationRequestDto
{
    public List<Guid>? PropertyIds { get; set; }
}

/// <summary>The answer of an activation or a deactivation: the flow and one outcome for each property asked about, in the order given.</summary>
public sealed class FlowActivationResultDto
{
    /// <summary>The flow, by its member name.</summary>
    public OnboardingFlow Flow { get; set; }

    public List<FlowActivationOutcomeDto> Outcomes { get; set; } = [];
}

/// <summary>
/// The outcome for one property. <c>outcome</c>: <c>activated</c>, <c>alreadyActive</c>, <c>prerequisitesNotMet</c>, <c>notAvailable</c>,
/// <c>notFound</c> (activation); <c>deactivated</c>, <c>alreadyInactive</c>, <c>notFound</c> (deactivation). <c>unmetPrerequisites</c>: the keys
/// of what does not hold (<c>connection</c>, <c>mapping</c>, <c>singleSender</c>, <c>content</c>, <c>cin</c>, <c>taxRate</c>). <c>reason</c>: the
/// code of a <c>prerequisitesNotMet</c> or <c>notAvailable</c> outcome. Codes only.
/// </summary>
public sealed class FlowActivationOutcomeDto
{
    public Guid PropertyId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public List<string> UnmetPrerequisites { get; set; } = [];

    public string? Reason { get; set; }
}

/// <summary>The body of <c>POST api/onboarding/test-run</c>: the property and, optionally, the booking to run the final test on.</summary>
public sealed class FlowTestRunRequestDto
{
    public Guid PropertyId { get; set; }

    /// <summary>A booking of the property imported from Octorate; without one, the most recent such booking.</summary>
    public Guid? BookingId { get; set; }
}

/// <summary>The answer of the final test: the property, the booking it ran on, and one check per flow.</summary>
public sealed class FlowTestRunDto
{
    public Guid PropertyId { get; set; }

    public Guid BookingId { get; set; }

    public List<FlowCheckDto> Checks { get; set; } = [];
}

/// <summary>
/// One flow of the final test: <c>outcome</c> <c>pass</c>, <c>fail</c> or <c>skipped</c>, with reason codes (on a <c>pass</c> they are
/// information, such as <c>guest_data_missing</c> before the arrival).
/// </summary>
public sealed class FlowCheckDto
{
    public OnboardingFlow Flow { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public List<string> Reasons { get; set; } = [];
}
