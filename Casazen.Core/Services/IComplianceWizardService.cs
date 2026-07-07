using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record ComplianceActivationStep(string Id, string Label, string Status, bool Blocker, string? Message = null);

public record PropertySafetyChecklistInput(
    bool SmokeDetector,
    bool FireExtinguisher,
    bool GasCompliance,
    string? AcknowledgedBy);

public record CompleteCheckoutWizardInput(
    bool ConfirmDeparture,
    Guid? SupplierOrgId,
    string? ServiceNotes,
    string? ServiceCategory);

public interface IComplianceWizardService
{
    Task<(Property Property, IReadOnlyList<ComplianceActivationStep> Steps)> GetActivationWizardAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<(Property Property, IReadOnlyList<string> IncompleteBlockers)> CompleteActivationAsync(
        Guid propertyId,
        string userId,
        PropertySafetyChecklistInput? safetyChecklist,
        bool? tosAccepted,
        CancellationToken cancellationToken = default);

    Task<ComplianceSummaryResult> GetSummaryAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<(Booking Booking, IReadOnlyList<ComplianceActivationStep> Steps)> StartCheckoutWizardAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default);

    Task<(Booking Booking, bool PropertyReady)> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default);
}

public record ComplianceSummaryItem(Guid Id, string Label, string RouteLink);

public record ComplianceSummarySection(int Count, IReadOnlyList<ComplianceSummaryItem> Items);

public record ComplianceSummaryResult(
    ComplianceSummarySection PropertiesPending,
    ComplianceSummarySection GuestCheckInsIncomplete,
    ComplianceSummarySection CheckoutsDue,
    ComplianceSummarySection AlloggiatiFailures);
