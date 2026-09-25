using System.ComponentModel.DataAnnotations;
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

    /// <summary>Stays checked out whose property was not declared ready for the next guest (CO-17).</summary>
    public ComplianceSummarySectionDto TurnoversPending { get; set; } = new();
}

/// <summary>
/// The check-out wizard of a stay (CO-17, A5-24): 5 steps (<c>stay-summary</c>, <c>alloggiati</c>, <c>cleaning</c>,
/// <c>tourist-tax</c>, <c>property-ready</c>), the saved progress and what each step shows.
/// </summary>
public class CheckoutWizardDto
{
    public Guid BookingId { get; set; }

    /// <summary><c>CheckedIn</c> while the wizard is open, <c>CheckedOut</c> once completed.</summary>
    public string BookingStatus { get; set; } = string.Empty;

    /// <summary>Id of the step the wizard opens on (saved progress; the last one after the check-out).</summary>
    public string CurrentStep { get; set; } = string.Empty;

    /// <summary>When the wizard was first opened (UTC).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the stay was checked out (UTC); null while the wizard is open or for a stay closed before CO-17.</summary>
    public DateTime? CompletedAt { get; set; }

    public IEnumerable<ComplianceActivationStepDto> Steps { get; set; } = [];

    public CheckoutStayDto Stay { get; set; } = new();
    public CheckoutAlloggiatiDto Alloggiati { get; set; } = new();
    public CheckoutCleaningDto Cleaning { get; set; } = new();
    public CheckoutTouristTaxDto TouristTax { get; set; } = new();
    public CheckoutPropertyReadyDto PropertyReady { get; set; } = new();
}

/// <summary>Step 1: the stay being closed. Dates are stay dates (midnight UTC, no time zone).</summary>
public class CheckoutStayDto
{
    public string GuestName { get; set; } = string.Empty;
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string PropertyCity { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int Nights { get; set; }
    public int NumberOfGuests { get; set; }
    public int NumberOfAdults { get; set; }
    public int NumberOfChildren { get; set; }

    /// <summary>Arrival instant (UTC) when registered on the check-in day; null otherwise.</summary>
    public DateTime? ArrivedAt { get; set; }

    public string Source { get; set; } = string.Empty;

    /// <summary>Step 1 answer saved: the host confirmed that the guest left.</summary>
    public bool DepartureConfirmed { get; set; }
}

/// <summary>Step 2: the Alloggiati Web communication (CasaZen does not transmit: "to send" until declared sent, CO-11).</summary>
public class CheckoutAlloggiatiDto
{
    public AlloggiatiWebStatus Status { get; set; }
    public bool Sent { get; set; }
    public DateTime DeadlineAt { get; set; }
    public bool IsOverdue { get; set; }
    public bool DataComplete { get; set; }
}

/// <summary>Step 3: the cleaning request of the stay (supplier of the property's comune) or skipped.</summary>
public class CheckoutCleaningDto
{
    /// <summary><c>Request</c>, <c>Skip</c> or null (not chosen yet).</summary>
    public CheckoutCleaningChoice? Choice { get; set; }

    public Guid? SupplierOrgId { get; set; }
    public string? Category { get; set; }
    public string? Notes { get; set; }

    /// <summary>The request created with the check-out, tied to the stay.</summary>
    public Guid? RequestId { get; set; }
}

/// <summary>Step 4: the tourist tax of the stay (BK-03) and how it was collected.</summary>
public class CheckoutTouristTaxDto
{
    /// <summary>
    /// Tax recorded on the booking when CasaZen priced it; null when CasaZen has no amount for the stay (channel
    /// booking, comune without a rate): never an invented one.
    /// </summary>
    public decimal? RecordedAmount { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>Paid online with the booking: the wizard proposes <c>CollectedOnline</c>.</summary>
    public bool CollectedWithOnlinePayment { get; set; }

    /// <summary><c>CollectedOnline</c>, <c>CollectedAtProperty</c>, <c>NotCollected</c>, <c>NotDue</c> or null.</summary>
    public TouristTaxCollection? Collection { get; set; }
}

/// <summary>Step 5: the property is ready for the next guest.</summary>
public class CheckoutPropertyReadyDto
{
    /// <summary>
    /// While the wizard is open, the answer saved (null = not answered); after the check-out, true only once the host
    /// declared it (<see cref="ReadyAt"/>).
    /// </summary>
    public bool? Ready { get; set; }

    public DateTime? ReadyAt { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Progress of the check-out wizard, saved as the host moves between the steps (CO-17). Nothing is created or declared
/// until the completion.
/// </summary>
public class SaveCheckoutProgressRequest
{
    /// <summary>Id of the step the host is on: <c>stay-summary</c>, <c>alloggiati</c>, <c>cleaning</c>, <c>tourist-tax</c>, <c>property-ready</c>.</summary>
    public string? CurrentStep { get; set; }

    public bool DepartureConfirmed { get; set; }

    [EnumDataType(typeof(CheckoutCleaningChoice), ErrorMessage = "CheckoutWizardValueInvalid")]
    public CheckoutCleaningChoice? CleaningChoice { get; set; }

    public Guid? SupplierOrgId { get; set; }

    [MaxLength(100, ErrorMessage = "CheckoutWizardValueInvalid")]
    public string? ServiceCategory { get; set; }

    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? ServiceNotes { get; set; }

    [EnumDataType(typeof(TouristTaxCollection), ErrorMessage = "CheckoutWizardValueInvalid")]
    public TouristTaxCollection? TouristTaxCollection { get; set; }

    public bool? PropertyReady { get; set; }

    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? PropertyNotes { get; set; }
}

/// <summary>
/// Completion of the check-out wizard (CO-08, CO-17). Without <see cref="CleaningChoice"/>, a
/// <see cref="SupplierOrgId"/> means "request" (contract of the app's quick check-out); without
/// <see cref="PropertyReady"/> the property is not ready and stays in the cockpit.
/// </summary>
public class CompleteCheckoutWizardRequest
{
    public bool ConfirmDeparture { get; set; }
    public Guid? SupplierOrgId { get; set; }

    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? ServiceNotes { get; set; }

    [MaxLength(100, ErrorMessage = "CheckoutWizardValueInvalid")]
    public string? ServiceCategory { get; set; }

    /// <summary>
    /// The host confirms that the guest arrived: a confirmed booking whose arrival was never registered is checked in
    /// with the check-out ("registra arrivo e procedi", CO-08).
    /// </summary>
    public bool RegisterArrival { get; set; }

    /// <summary>Step 3: <c>Request</c> (needs <see cref="SupplierOrgId"/>) or <c>Skip</c>.</summary>
    [EnumDataType(typeof(CheckoutCleaningChoice), ErrorMessage = "CheckoutWizardValueInvalid")]
    public CheckoutCleaningChoice? CleaningChoice { get; set; }

    /// <summary>Step 4: how the tourist tax was collected.</summary>
    [EnumDataType(typeof(TouristTaxCollection), ErrorMessage = "CheckoutWizardValueInvalid")]
    public TouristTaxCollection? TouristTaxCollection { get; set; }

    /// <summary>Step 5: the property is ready for the next guest.</summary>
    public bool? PropertyReady { get; set; }

    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? PropertyNotes { get; set; }
}

public class CompleteCheckoutWizardResponse
{
    /// <summary>True only when the host declared the property ready (never assumed).</summary>
    public bool PropertyReady { get; set; }

    public string BookingStatus { get; set; } = string.Empty;

    /// <summary>The cleaning request created for the stay, if any.</summary>
    public Guid? ServiceRequestId { get; set; }

    /// <summary>The whole wizard after the check-out.</summary>
    public CheckoutWizardDto Wizard { get; set; } = new();
}

/// <summary>The host declares the property ready after the check-out (CO-17).</summary>
public class ConfirmPropertyReadyRequest
{
    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? Notes { get; set; }
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
