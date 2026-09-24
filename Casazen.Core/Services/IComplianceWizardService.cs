using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record ComplianceActivationStep(string Id, string Label, string Status, bool Blocker, string? Message = null)
{
    /// <summary>
    /// Resource key of the message (<c>Casazen.Web/Resources/SharedResources.resx</c>), formatted with
    /// <see cref="MessageArgs"/>. When set, the API sends its localized text in place of <see cref="Message"/>.
    /// </summary>
    public string? MessageKey { get; init; }

    public IReadOnlyList<object>? MessageArgs { get; init; }

    /// <summary>External guidance link of the step (for the CIN step: <c>Compliance:CinGuidanceUrl</c>).</summary>
    public string? LinkUrl { get; init; }

    /// <summary>Only on the <c>tourist-tax</c> step: what CasaZen knows about the tax in the property's comune.</summary>
    public TouristTaxActivationInfo? TouristTax { get; init; }
}

/// <summary>
/// Tourist tax of the property's comune as shown to the host in the activation wizard (A5-06, A8-28).
/// </summary>
/// <param name="City">City of the property, as entered by the host.</param>
/// <param name="Rate">Active rate in force today (Europe/Rome), or null when CasaZen has none for the comune.</param>
/// <param name="PublicPageSlug">
/// Comune slug of the reviewed public SEO page <c>/p/tassa-soggiorno/{slug}</c>, or null when there is no such page.
/// </param>
public record TouristTaxActivationInfo(string City, TouristTaxRate? Rate, string? PublicPageSlug);

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

    /// <summary>
    /// Completes the checkout of a booking the caller has already been authorized on (TN-3); the optional
    /// service request is created on behalf of <paramref name="userId"/> without further role checks.
    /// </summary>
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
