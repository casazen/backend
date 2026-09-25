using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;

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

    /// <summary>What keeps this blocking step incomplete, with stable codes; empty when complete.</summary>
    public IEnumerable<ActivationBlockerDto> Blockers { get; set; } = [];
}

/// <summary>One reason why the activation is blocked (CO-07): stable snake_case code and localized message.</summary>
public class ActivationBlockerDto
{
    /// <summary>Id of the wizard step (<c>safety</c>, <c>cin</c>, ...).</summary>
    public string Step { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Tourist tax of the property's comune, as shown to the host in the activation wizard.</summary>
public class ActivationTouristTaxDto
{
    public string City { get; set; } = string.Empty;

    /// <summary>Rate in force today, or null when CasaZen has no rate for the comune (the step is then a warning).</summary>
    public ActivationTouristTaxRateDto? Rate { get; set; }

    /// <summary>Slug of the public page <c>/p/tassa-soggiorno/{slug}</c>, only when a reviewed page exists.</summary>
    public string? PublicPageSlug { get; set; }

    /// <summary>True when the comune has rates only per accommodation category, unknown for the property (BK-03).</summary>
    public bool CategoryRequired { get; set; }
}

public class ActivationTouristTaxRateDto
{
    public TouristTaxCalculationMethod CalculationMethod { get; set; }
    public decimal RatePerPersonPerNight { get; set; }
    public decimal? PercentOfNightlyPrice { get; set; }
    public decimal? CapPerPersonPerNight { get; set; }
    public int? MaxNights { get; set; }
    public int MinimumAge { get; set; }
    public int? ReducedRateMaxAge { get; set; }
    public decimal? ReducedRatePerPersonPerNight { get; set; }
    public string? SeasonStart { get; set; }
    public string? SeasonEnd { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? SourceUrl { get; set; }
    public TouristTaxRateVerification? VerificationLevel { get; set; }
}

public class PropertyActivationWizardDto
{
    /// <summary><c>Pending</c>, <c>Active</c> or <c>Suspended</c> (CO-06: an active property that lost a requirement).</summary>
    public string ComplianceStatus { get; set; } = string.Empty;

    /// <summary>When the property was suspended (UTC); null unless <see cref="ComplianceStatus"/> is <c>Suspended</c>.</summary>
    public DateTime? SuspendedAt { get; set; }

    /// <summary>
    /// Blocker codes that suspended the property, as they were at the suspension (e.g. <c>activation_cin_missing</c>);
    /// empty unless suspended. The current blockers are in <see cref="Steps"/>: the change that solves the last one
    /// reactivates the property.
    /// </summary>
    public IReadOnlyList<string> SuspensionReasons { get; set; } = [];

    public IEnumerable<ComplianceActivationStepDto> Steps { get; set; } = [];
}

/// <summary>
/// Completion of the activation. The safety checklist is saved on its own endpoint
/// (<c>PUT /api/properties/{id}/compliance/safety-checklist</c>, CO-07), never here.
/// </summary>
public class CompletePropertyActivationRequest
{
    public bool? TosAccepted { get; set; }
}

public class CompletePropertyActivationResponse
{
    public string ComplianceStatus { get; set; } = string.Empty;
    public IEnumerable<string>? IncompleteBlockers { get; set; }
}

/// <summary>
/// An item of the compliance cockpit (CO-04, A5-09): the action to take and its target, never a front-end path. The
/// web app builds the route from its <c>ROUTE_MANIFEST</c> (<c>src/lib/compliance-routes.ts</c>).
/// </summary>
public class ComplianceSummaryItemDto
{
    /// <summary>Target of the action: equal to <see cref="PropertyId"/> or <see cref="BookingId"/>.</summary>
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Serialized by name (<c>ActivateProperty</c>, <c>CompleteGuestCheckIn</c>, ...).</summary>
    public ComplianceCockpitAction Action { get; set; }

    /// <summary>Set for <see cref="ComplianceCockpitAction.ActivateProperty"/>, null otherwise.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>Set for every action on a booking, null for <see cref="ComplianceCockpitAction.ActivateProperty"/>.</summary>
    public Guid? BookingId { get; set; }
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

    /// <summary>Alloggiati communications the host must send on the Questura portal (CasaZen does not transmit).</summary>
    public ComplianceSummarySectionDto AlloggiatiManualRequired { get; set; } = new();
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

    /// <summary>
    /// The host confirms that the guest arrived: a confirmed booking whose arrival was never registered is checked in
    /// with the check-out ("registra arrivo e procedi", CO-08).
    /// </summary>
    public bool RegisterArrival { get; set; }
}

public class CompleteCheckoutWizardResponse
{
    public bool PropertyReady { get; set; }
    public string BookingStatus { get; set; } = string.Empty;
}

/// <summary>Safety checklist of a short-stay property under D.L. 145/2023 art. 13-ter (CO-07).</summary>
public class SafetyChecklistDto
{
    /// <summary>1 = imported from the old checklist and still to review, 2 = current rules.</summary>
    public int SchemaVersion { get; set; }
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>Version of the confirmation text the host must confirm now.</summary>
    public string DeclarationTextVersion { get; set; } = string.Empty;

    /// <summary>False until the host saves the checklist once.</summary>
    public bool Saved { get; set; }

    /// <summary>True when the answers come from the old checklist (smoke detector, extinguisher, gas certificate).</summary>
    public bool ImportedFromLegacy { get; set; }

    public SafetyChecklistFactsDto Facts { get; set; } = new();
    public IEnumerable<SafetyChecklistItemDto> Items { get; set; } = [];

    /// <summary>One every 200 m² of floor or fraction per floor, at least one per floor; null without the floors.</summary>
    public int? MinimumExtinguishers { get; set; }

    public bool IsComplete { get; set; }
    public IEnumerable<SafetyChecklistIssueDto> Blockers { get; set; } = [];
    public IEnumerable<SafetyChecklistIssueDto> Warnings { get; set; } = [];
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmedTextVersion { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Facts of the unit declared by the host (SC-01, SC-03, floors of SC-02).</summary>
public class SafetyChecklistFactsDto
{
    public bool? Entrepreneurial { get; set; }
    public bool? HasGasSupply { get; set; }

    /// <summary>Null = not answered, empty = no combustion appliance.</summary>
    public List<CombustionAppliance>? CombustionAppliances { get; set; }

    public int? FloorCount { get; set; }

    /// <summary>m² of floor of each floor of the unit, in order; null when not given.</summary>
    public List<decimal>? FloorAreasSqm { get; set; }
}

public class SafetyChecklistItemDto
{
    public SafetyItemCode Code { get; set; }
    public SafetyItemRequirement Requirement { get; set; }
    public SafetyItemStatus Status { get; set; }
    public SafetyNotApplicableReason? NotApplicableReason { get; set; }
    public SafetyItemAnswer? Answer { get; set; }
    public int? Quantity { get; set; }
    public string? Location { get; set; }
    public SafetyDetectorType? DetectorType { get; set; }
    public DateOnly? CheckedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public Guid? EvidenceDocumentId { get; set; }
    public string? EvidenceFileName { get; set; }
    public string? Notes { get; set; }
}

public class SafetyChecklistIssueDto
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Whole checklist as saved by the host. <see cref="Confirm"/> is the final confirmation (SC-08).</summary>
public class SaveSafetyChecklistRequest
{
    public SafetyChecklistFactsDto Facts { get; set; } = new();
    public List<SaveSafetyChecklistItemRequest> Items { get; set; } = [];
    public bool Confirm { get; set; }
}

public class SaveSafetyChecklistItemRequest
{
    public SafetyItemCode Code { get; set; }

    /// <summary><c>Present</c>, <c>Missing</c> or null (not answered); "not applicable" follows from the facts.</summary>
    public SafetyItemAnswer? Answer { get; set; }

    public int? Quantity { get; set; }
    public string? Location { get; set; }
    public SafetyDetectorType? DetectorType { get; set; }
    public DateOnly? CheckedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Id of a document of the same property (uploaded with <c>POST /api/properties/{id}/documents</c>).</summary>
    public Guid? EvidenceDocumentId { get; set; }

    public string? Notes { get; set; }
}
