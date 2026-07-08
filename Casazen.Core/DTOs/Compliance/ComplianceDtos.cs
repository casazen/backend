using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs.Compliance;

public record ComplianceWizardStepDto(
    string Id,
    string Label,
    string Status,
    string? Blocker = null);

public record PropertyActivationWizardDto(
    PropertyComplianceStatus ComplianceStatus,
    IReadOnlyList<ComplianceWizardStepDto> Steps);

public record PropertyActivationCompleteResultDto(
    PropertyComplianceStatus ComplianceStatus,
    IReadOnlyList<string> Blockers);

public record ComplianceSummaryDto(
    int PropertiesPending,
    int GuestCheckInsIncomplete,
    int CheckoutsDue,
    int AlloggiatiFailures,
    IReadOnlyList<ComplianceSummaryLinkDto> Links);

public record ComplianceSummaryLinkDto(string Key, string Route, int Count);

public record CheckoutWizardDto(IReadOnlyList<ComplianceWizardStepDto> Steps);

public record CheckoutWizardCompleteRequest(bool ConfirmDeparture);

public record CheckoutWizardCompleteResultDto(bool PropertyReady, string BookingStatus);
