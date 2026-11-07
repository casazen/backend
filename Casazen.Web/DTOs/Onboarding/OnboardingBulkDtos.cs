using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// Which properties a bulk call is about (<c>POST api/onboarding/bulk/preview</c> and <c>apply</c>). <c>kind</c> is <c>all</c>, <c>ids</c> or
/// <c>filter</c>; <c>propertyIds</c> goes with <c>ids</c> (1 to <c>Onboarding:MaxPropertiesPerCall</c>, never an empty list) and <c>filter</c> with
/// <c>filter</c>. What the kind does not use must be left out: a body that names a filter and says <c>all</c> is refused, never read as «all».
/// </summary>
public sealed class BulkSelectionDto
{
    public string? Kind { get; set; }

    public List<Guid>? PropertyIds { get; set; }

    public BulkFilterDto? Filter { get; set; }
}

/// <summary>
/// The criteria of a filtered selection: every criterion given must hold, and a list holds when any of its values does. At least one criterion
/// is given and a list that is given is not empty (422 <c>onboarding_selection_invalid</c>).
/// </summary>
public sealed class BulkFilterDto
{
    /// <summary>ISTAT codes (6 digits) of the comune of the property.</summary>
    public List<string>? ComuneIstatCodes { get; set; }

    /// <summary>CasaZen region codes (<c>LOM</c>) of the property.</summary>
    public List<string>? RegionCodes { get; set; }

    /// <summary>Codes of a channel (<c>booking_xml</c>) the property has a channel state for.</summary>
    public List<string>? ChannelCodes { get; set; }

    /// <summary>The flow is available for the property and not active yet (<c>toComplete</c>).</summary>
    public OnboardingFlow? MissingFlow { get; set; }

    /// <summary>The flow is in exactly this state on the property.</summary>
    public BulkFlowStateDto? FlowState { get; set; }
}

/// <summary>A flow and one of its states: <c>active</c>, <c>toComplete</c> or <c>notAvailable</c>.</summary>
public sealed class BulkFlowStateDto
{
    public OnboardingFlow? Flow { get; set; }

    public string? State { get; set; }
}

/// <summary>The answer of a preview: the total, a sample of 20 properties, the ids asked that are not properties of the selection.</summary>
public sealed class BulkPreviewDto
{
    public int Total { get; set; }

    public List<BulkPropertySampleDto> Sample { get; set; } = [];

    public int NotFound { get; set; }

    /// <summary>True when the total is more than <c>maxPropertiesPerCall</c>: narrow the selection before applying.</summary>
    public bool ExceedsLimit { get; set; }

    public int MaxPropertiesPerCall { get; set; }
}

/// <summary>One property of the sample of a preview.</summary>
public sealed class BulkPropertySampleDto
{
    public Guid PropertyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;
}

/// <summary>
/// The body of <c>POST api/onboarding/bulk/apply</c>: the selection and the choice. Nothing is written unless <c>dryRun</c> is false AND
/// <c>confirmed</c> is true (both default to false): without them the answer says what each property would do.
/// </summary>
public sealed class BulkApplyRequestDto
{
    public BulkSelectionDto? Selection { get; set; }

    public BulkChoiceDto? Choice { get; set; }

    public bool DryRun { get; set; }

    public bool Confirmed { get; set; }
}

/// <summary>The choice to apply: the sender of the Alloggiati Web and ISTAT communications (<c>ChannelManager</c>, <c>CasaZen</c> or <c>Manual</c>).</summary>
public sealed class BulkChoiceDto
{
    public ComplianceSenderType? ComplianceSender { get; set; }
}

/// <summary>
/// The answer of an apply: one item for each property. <c>dryRun</c> is true when nothing was written (a dry run, or no confirmation). An item's
/// <c>outcome</c> is <c>applied</c>, <c>unchanged</c>, <c>refused</c> (with the <c>reason</c>, the code of the rule) or, only when nothing was
/// written, <c>wouldChange</c>. Codes only.
/// </summary>
public sealed class BulkApplyResultDto
{
    public bool DryRun { get; set; }

    public ComplianceSenderType ComplianceSender { get; set; }

    public BulkApplySummaryDto Summary { get; set; } = new();

    public List<BulkApplyItemDto> Items { get; set; } = [];
}

/// <summary>How many properties are in each outcome of an apply.</summary>
public sealed class BulkApplySummaryDto
{
    public int Applied { get; set; }

    public int Unchanged { get; set; }

    public int Refused { get; set; }

    public int WouldChange { get; set; }
}

/// <summary>The outcome for one property; <c>name</c> and <c>city</c> are <c>null</c> for an id that is not a property of the selection.</summary>
public sealed class BulkApplyItemDto
{
    public Guid PropertyId { get; set; }

    public string? Name { get; set; }

    public string? City { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }
}

/// <summary>«<c>ready</c> of <c>total</c> ready», the four counts of each flow and the pilot group.</summary>
public sealed class BulkProgressDto
{
    public int Total { get; set; }

    public int Ready { get; set; }

    public int NotReady { get; set; }

    public List<BulkFlowProgressDto> Flows { get; set; } = [];

    public BulkPilotDto Pilot { get; set; } = new();
}

/// <summary>
/// One flow over the properties: <c>active</c> runs, <c>ready</c> has every prerequisite met and waits for the activation, <c>toComplete</c> still
/// lacks something, <c>notAvailable</c> does not apply. The four add up to the properties.
/// </summary>
public sealed class BulkFlowProgressDto
{
    public OnboardingFlow Flow { get; set; }

    public int Active { get; set; }

    public int Ready { get; set; }

    public int ToComplete { get; set; }

    public int NotAvailable { get; set; }
}

/// <summary>The body of <c>PUT api/onboarding/bulk/pilot</c>: 1 to <c>Onboarding:PilotGroupMaxSize</c> distinct properties. An empty list is 422, never «no pilot group».</summary>
public sealed class BulkPilotRequestDto
{
    public List<Guid>? PropertyIds { get; set; }
}

/// <summary>
/// The pilot group: its members, the recommended minimum (5) and the maximum, <c>belowRecommended</c> when it has fewer than the minimum (not an
/// error), and for each flow how many members it is available for and active on: <c>isActive</c> when it has at least one and every one has it active.
/// </summary>
public sealed class BulkPilotDto
{
    public List<BulkPilotMemberDto> Members { get; set; } = [];

    public int Size { get; set; }

    public int RecommendedMin { get; set; }

    public int MaxSize { get; set; }

    public bool BelowRecommended { get; set; }

    public List<BulkPilotFlowDto> Flows { get; set; } = [];
}

/// <summary>A property of the pilot group.</summary>
public sealed class BulkPilotMemberDto
{
    public Guid PropertyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}

/// <summary>How one flow has got on the pilot group.</summary>
public sealed class BulkPilotFlowDto
{
    public OnboardingFlow Flow { get; set; }

    public int Available { get; set; }

    public int Active { get; set; }

    public bool IsActive { get; set; }
}

/// <summary>
/// The body of <c>POST api/onboarding/bulk/activate</c>: the cohort (<c>pilot</c> or <c>rest</c>) and the flows (member names of
/// <c>OnboardingFlow</c>), at least one. An empty list of flows is 422, never «all the flows».
/// </summary>
public sealed class BulkActivateRequestDto
{
    public string? Cohort { get; set; }

    public List<string>? Flows { get; set; }
}

/// <summary>The activation of a cohort: for each flow asked, the outcome of each property as the activation of a flow gives it.</summary>
public sealed class BulkActivateResultDto
{
    public string Cohort { get; set; } = string.Empty;

    public List<BulkActivateFlowDto> Flows { get; set; } = [];
}

/// <summary>One flow of an activation: <c>activated</c>, <c>alreadyActive</c>, <c>prerequisitesNotMet</c> (with the keys), <c>notAvailable</c> or <c>notFound</c> for each property.</summary>
public sealed class BulkActivateFlowDto
{
    public OnboardingFlow Flow { get; set; }

    public List<FlowActivationOutcomeDto> Outcomes { get; set; } = [];
}
