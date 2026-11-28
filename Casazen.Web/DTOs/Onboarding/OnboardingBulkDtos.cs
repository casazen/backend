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

public sealed class BulkFilterDto
{
    public List<string>? ComuneIstatCodes { get; set; }

    public List<string>? RegionCodes { get; set; }

    public List<string>? ChannelCodes { get; set; }

    public OnboardingFlow? MissingFlow { get; set; }

    public BulkFlowStateDto? FlowState { get; set; }
}

public sealed class BulkFlowStateDto
{
    public OnboardingFlow? Flow { get; set; }

    public string? State { get; set; }
}

public sealed class BulkPreviewDto
{
    public int Total { get; set; }

    public List<BulkPropertySampleDto> Sample { get; set; } = [];

    public int NotFound { get; set; }

    public bool ExceedsLimit { get; set; }

    public int MaxPropertiesPerCall { get; set; }
}

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

/// <summary>
/// The choice to apply: the sender of the Alloggiati Web and ISTAT communications (<c>ChannelManager</c>, <c>CasaZen</c> or <c>Manual</c>) and, once for the
/// org, whether CasaZen writes the guests to the channel manager on their own.
/// </summary>
public sealed class BulkChoiceDto
{
    public ComplianceSenderType? ComplianceSender { get; set; }

    public bool? AutoSendGuestsToOctorate { get; set; }
}

public sealed class BulkApplyResultDto
{
    public bool DryRun { get; set; }

    public ComplianceSenderType ComplianceSender { get; set; }

    public bool? AutoSendGuestsToOctorate { get; set; }

    public BulkApplySummaryDto Summary { get; set; } = new();

    public List<BulkApplyItemDto> Items { get; set; } = [];
}

public sealed class BulkApplySummaryDto
{
    public int Applied { get; set; }

    public int Unchanged { get; set; }

    public int Refused { get; set; }

    public int WouldChange { get; set; }
}

public sealed class BulkApplyItemDto
{
    public Guid PropertyId { get; set; }

    public string? Name { get; set; }

    public string? City { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }
}

public sealed class BulkProgressDto
{
    public int Total { get; set; }

    public int Ready { get; set; }

    public int NotReady { get; set; }

    public List<BulkFlowProgressDto> Flows { get; set; } = [];

    public BulkPilotDto Pilot { get; set; } = new();
}

public sealed class BulkFlowProgressDto
{
    public OnboardingFlow Flow { get; set; }

    public int Active { get; set; }

    public int Ready { get; set; }

    public int ToComplete { get; set; }

    public int NotAvailable { get; set; }
}

public sealed class BulkPilotRequestDto
{
    public List<Guid>? PropertyIds { get; set; }
}

public sealed class BulkPilotDto
{
    public List<BulkPilotMemberDto> Members { get; set; } = [];

    public int Size { get; set; }

    public int RecommendedMin { get; set; }

    public int MaxSize { get; set; }

    public bool BelowRecommended { get; set; }

    public List<BulkPilotFlowDto> Flows { get; set; } = [];
}

public sealed class BulkPilotMemberDto
{
    public Guid PropertyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}

public sealed class BulkPilotFlowDto
{
    public OnboardingFlow Flow { get; set; }

    public int Available { get; set; }

    public int Active { get; set; }

    public bool IsActive { get; set; }
}

public sealed class BulkActivateRequestDto
{
    public string? Cohort { get; set; }

    public List<string>? Flows { get; set; }
}

public sealed class BulkActivateResultDto
{
    public string Cohort { get; set; } = string.Empty;

    public List<BulkActivateFlowDto> Flows { get; set; } = [];
}

public sealed class BulkActivateFlowDto
{
    public OnboardingFlow Flow { get; set; }

    public List<FlowActivationOutcomeDto> Outcomes { get; set; } = [];
}
