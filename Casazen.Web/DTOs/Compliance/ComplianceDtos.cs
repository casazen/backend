using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

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

    public ActivationTouristTaxDto? TouristTax { get; set; }

    public IEnumerable<ActivationBlockerDto> Blockers { get; set; } = [];

    /// <summary>Non-blocking notes of the step (SU-04): e.g. the comune of the CIN differs from the property's.</summary>
    public IEnumerable<ActivationWarningDto> Warnings { get; set; } = [];
}

public class ActivationWarningDto
{
    public string Step { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ActivationBlockerDto
{
    public string Step { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ActivationTouristTaxDto
{
    public string City { get; set; } = string.Empty;

    /// <summary>Rate in force today, or null when CasaZen has no rate for the comune (the step is then a warning).</summary>
    public ActivationTouristTaxRateDto? Rate { get; set; }

    public string? PublicPageSlug { get; set; }

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

public class CompletePropertyActivationRequest
{
    public bool? TosAccepted { get; set; }

    /// <summary>
    /// The host knows that the reminders still open (the required documents, the safety checklist) are their own obligations and
    /// will complete them: needed only when some are open (409 <c>activation_reminders_ack_required</c> otherwise).
    /// </summary>
    public bool? RemindersAcknowledged { get; set; }
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

    public ComplianceCockpitAction Action { get; set; }

    /// <summary>Set for <see cref="ComplianceCockpitAction.ActivateProperty"/>, null otherwise.</summary>
    public Guid? PropertyId { get; set; }

    public Guid? BookingId { get; set; }

    /// <summary>
    /// What the item still lacks (SR-03), with stable codes and fields: the blockers of a property to activate, the fields of the
    /// Alloggiati record the guests of a stay lack, the single thing left for the other actions. Never empty.
    /// </summary>
    public IEnumerable<ComplianceMissingDto> Missing { get; set; } = [];

    /// <summary>
    /// When the item has to be done by (UTC), if the stay fixes it: the legal term of the Alloggiati communication (which runs
    /// from the arrival), the day the guest arrives (their data are needed by then), the day the guest leaves. <c>null</c> for a
    /// property to activate and for a turnover.
    /// </summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// The traffic light, serialized by name: <c>Green</c> only with an archived proof (an Alloggiati receipt of the arrival day, an ISTAT proof, the
    /// tax receipts, a valid CIN, a passed identity verification), <c>Yellow</c> while the data are entrusted to a sender within the deadline and
    /// the receipt is awaited (or the period is open), <c>Red</c> when the host has to act. A client that does not know the value shows "to verify":
    /// it never computes green.
    /// </summary>
    public ComplianceLight? Light { get; set; }

    public string? Reason { get; set; }

    /// <summary>The month (<c>yyyy-MM</c>) of an item about an ISTAT or tourist tax period; <c>null</c> for the others.</summary>
    public string? Period { get; set; }

    public ComplianceEvidenceClass? EvidenceClass { get; set; }

    public string? EvidenceSource { get; set; }

    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>
    /// <c>Alloggiati</c> or <c>Istat</c> when the outcome is held only by Octorate and CasaZen has no proof: the client offers «Verifica nel tuo
    /// Cassetto Compliance» (<c>POST api/compliance/octorate-handoffs</c> with this topic). Starting or returning from the handoff changes
    /// nothing here. <c>null</c> when none applies.
    /// </summary>
    public OctorateHandoffTopic? HandoffTopic { get; set; }
}

public class ComplianceMissingDto
{
    public string Code { get; init; } = string.Empty;

    /// <summary>The step of the activation wizard (<c>cin</c>, <c>documents</c>, ...) or the field of the Alloggiati record (<c>documentNumber</c>, ...); <c>null</c> when the code says it all.</summary>
    public string? Field { get; init; }

    public int? Count { get; init; }

    public static ComplianceMissingDto From(Casazen.Core.Services.ComplianceMissing missing) => new()
    {
        Code = missing.Code,
        Field = missing.Field,
        Count = missing.Count,
    };
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

    public ComplianceSummarySectionDto TurnoversPending { get; set; } = new();

    public ComplianceSummarySectionDto IdentityPending { get; set; } = new();

    public ComplianceSummarySectionDto ReceiptsToUpload { get; set; } = new();

    public ComplianceSummarySectionDto ManualSendsRequired { get; set; } = new();

    public ComplianceSummarySectionDto ConnectionsToFix { get; set; } = new();

    /// <summary>The ISTAT (<c>UploadIstatProof</c>) and tourist tax (<c>DeclareTouristTax</c>) of the closed months, with their <c>period</c>, and the tourist tax rates that are not known (<c>EnterMunicipalityTaxRate</c>).</summary>
    public ComplianceSummarySectionDto PeriodItems { get; set; } = new();

    public ComplianceSummarySectionDto TicinoNotifications { get; set; } = new();

    public ComplianceSummarySectionDto SwissNotifications { get; set; } = new();
}

/// <summary>
/// The check-out wizard of a stay (CO-17, A5-24): 5 steps (<c>stay-summary</c>, <c>alloggiati</c>, <c>cleaning</c>,
/// <c>tourist-tax</c>, <c>property-ready</c>), the saved progress and what each step shows.
/// </summary>
public class CheckoutWizardDto
{
    public Guid BookingId { get; set; }

    public string BookingStatus { get; set; } = string.Empty;

    public string CurrentStep { get; set; } = string.Empty;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public IEnumerable<ComplianceActivationStepDto> Steps { get; set; } = [];

    public CheckoutStayDto Stay { get; set; } = new();
    public CheckoutAlloggiatiDto Alloggiati { get; set; } = new();
    public CheckoutCleaningDto Cleaning { get; set; } = new();
    public CheckoutTouristTaxDto TouristTax { get; set; } = new();
    public CheckoutPropertyReadyDto PropertyReady { get; set; } = new();
}

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

public class CheckoutCleaningDto
{
    public CheckoutCleaningChoice? Choice { get; set; }

    public Guid? SupplierOrgId { get; set; }
    public string? Category { get; set; }
    public string? Notes { get; set; }

    public Guid? RequestId { get; set; }
}

public class CheckoutTouristTaxDto
{
    public decimal? RecordedAmount { get; set; }

    public string Currency { get; set; } = "EUR";

    public bool CollectedWithOnlinePayment { get; set; }

    public TouristTaxCollection? Collection { get; set; }
}

public class CheckoutPropertyReadyDto
{
    public bool? Ready { get; set; }

    public DateTime? ReadyAt { get; set; }
    public string? Notes { get; set; }
}

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

public class CompleteCheckoutWizardRequest
{
    public bool ConfirmDeparture { get; set; }
    public Guid? SupplierOrgId { get; set; }

    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? ServiceNotes { get; set; }

    [MaxLength(100, ErrorMessage = "CheckoutWizardValueInvalid")]
    public string? ServiceCategory { get; set; }

    public bool RegisterArrival { get; set; }

    [EnumDataType(typeof(CheckoutCleaningChoice), ErrorMessage = "CheckoutWizardValueInvalid")]
    public CheckoutCleaningChoice? CleaningChoice { get; set; }

    [EnumDataType(typeof(TouristTaxCollection), ErrorMessage = "CheckoutWizardValueInvalid")]
    public TouristTaxCollection? TouristTaxCollection { get; set; }

    public bool? PropertyReady { get; set; }

    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? PropertyNotes { get; set; }
}

public class CompleteCheckoutWizardResponse
{
    public bool PropertyReady { get; set; }

    public string BookingStatus { get; set; } = string.Empty;

    public Guid? ServiceRequestId { get; set; }

    public CheckoutWizardDto Wizard { get; set; } = new();
}

public class ConfirmPropertyReadyRequest
{
    [MaxLength(1000, ErrorMessage = "CheckoutNotesTooLong")]
    public string? Notes { get; set; }
}

public class SafetyChecklistDto
{
    public int SchemaVersion { get; set; }
    public string LegalBasis { get; set; } = string.Empty;

    public string DeclarationTextVersion { get; set; } = string.Empty;

    public bool Saved { get; set; }

    public bool ImportedFromLegacy { get; set; }

    public SafetyChecklistFactsDto Facts { get; set; } = new();
    public IEnumerable<SafetyChecklistItemDto> Items { get; set; } = [];

    public int? MinimumExtinguishers { get; set; }

    public bool IsComplete { get; set; }
    public IEnumerable<SafetyChecklistIssueDto> Blockers { get; set; } = [];
    public IEnumerable<SafetyChecklistIssueDto> Warnings { get; set; } = [];
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmedTextVersion { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SafetyChecklistFactsDto
{
    public bool? Entrepreneurial { get; set; }
    public bool? HasGasSupply { get; set; }

    public List<CombustionAppliance>? CombustionAppliances { get; set; }

    public int? FloorCount { get; set; }

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

public class SaveSafetyChecklistRequest
{
    public SafetyChecklistFactsDto Facts { get; set; } = new();
    public List<SaveSafetyChecklistItemRequest> Items { get; set; } = [];
    public bool Confirm { get; set; }
}

public class SaveSafetyChecklistItemRequest
{
    public SafetyItemCode Code { get; set; }

    public SafetyItemAnswer? Answer { get; set; }

    public int? Quantity { get; set; }
    public string? Location { get; set; }
    public SafetyDetectorType? DetectorType { get; set; }
    public DateOnly? CheckedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }

    public Guid? EvidenceDocumentId { get; set; }

    public string? Notes { get; set; }
}
