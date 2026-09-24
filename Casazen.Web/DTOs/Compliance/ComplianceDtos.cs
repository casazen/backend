using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Compliance;

public class ComplianceActivationStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Blocker { get; set; }
    public string? Message { get; set; }

    /// <summary>External guidance link of the step (CIN: <c>Compliance:CinGuidanceUrl</c>), when there is one.</summary>
    public string? LinkUrl { get; set; }

    /// <summary>Only on the <c>tourist-tax</c> step.</summary>
    public ActivationTouristTaxDto? TouristTax { get; set; }
}

/// <summary>Tourist tax of the property's comune, as shown to the host in the activation wizard.</summary>
public class ActivationTouristTaxDto
{
    public string City { get; set; } = string.Empty;

    /// <summary>Rate in force today, or null when CasaZen has no rate for the comune (the step is then a warning).</summary>
    public ActivationTouristTaxRateDto? Rate { get; set; }

    /// <summary>Slug of the public page <c>/p/tassa-soggiorno/{slug}</c>, only when a reviewed page exists.</summary>
    public string? PublicPageSlug { get; set; }
}

public class ActivationTouristTaxRateDto
{
    public decimal RatePerPersonPerNight { get; set; }
    public int? MaxNights { get; set; }
    public int MinimumAge { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? SourceUrl { get; set; }
    public TouristTaxRateVerification? VerificationLevel { get; set; }
}

public class PropertyActivationWizardDto
{
    public string ComplianceStatus { get; set; } = string.Empty;
    public IEnumerable<ComplianceActivationStepDto> Steps { get; set; } = [];
}

public class CompletePropertyActivationRequest
{
    public PropertySafetyChecklistRequest? SafetyChecklist { get; set; }
    public bool? TosAccepted { get; set; }
}

public class PropertySafetyChecklistRequest
{
    public bool SmokeDetector { get; set; }
    public bool FireExtinguisher { get; set; }
    public bool GasCompliance { get; set; }
}

public class CompletePropertyActivationResponse
{
    public string ComplianceStatus { get; set; } = string.Empty;
    public IEnumerable<string>? IncompleteBlockers { get; set; }
}

public class ComplianceSummaryItemDto
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string RouteLink { get; set; } = string.Empty;
}

public class ComplianceSummarySectionDto
{
    public int Count { get; set; }
    public IEnumerable<ComplianceSummaryItemDto> Items { get; set; } = [];
}

public class ComplianceSummaryDto
{
    public ComplianceSummarySectionDto PropertiesPending { get; set; } = new();
    public ComplianceSummarySectionDto GuestCheckInsIncomplete { get; set; } = new();
    public ComplianceSummarySectionDto CheckoutsDue { get; set; } = new();
    public ComplianceSummarySectionDto AlloggiatiFailures { get; set; } = new();
}

public class CheckoutWizardDto
{
    public IEnumerable<ComplianceActivationStepDto> Steps { get; set; } = [];
}

public class CompleteCheckoutWizardRequest
{
    public bool ConfirmDeparture { get; set; }
    public Guid? SupplierOrgId { get; set; }
    public string? ServiceNotes { get; set; }
    public string? ServiceCategory { get; set; }
}

public class CompleteCheckoutWizardResponse
{
    public bool PropertyReady { get; set; }
    public string BookingStatus { get; set; } = string.Empty;
}
