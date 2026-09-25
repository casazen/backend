using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;

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

    /// <summary>
    /// What keeps a blocking step from being complete, with stable codes (e.g. the items of the safety checklist,
    /// <c>safety_gas_detector_missing</c>); empty when the step is complete or not blocking.
    /// </summary>
    public IReadOnlyList<ActivationBlocker> Blockers { get; init; } = [];
}

/// <summary>
/// One reason why the activation is blocked: stable snake_case <see cref="Code"/> (the frontend translates it) and the
/// resource key of its message in <c>SharedResources</c>, formatted with <see cref="MessageArgs"/>.
/// </summary>
public sealed record ActivationBlocker(string Code, string MessageKey, IReadOnlyList<object> MessageArgs)
{
    public ActivationBlocker(string code, string messageKey)
        : this(code, messageKey, [])
    {
    }
}

/// <summary>
/// Tourist tax of the property's comune as shown to the host in the activation wizard (A5-06, A8-28).
/// </summary>
/// <param name="City">City of the property, as entered by the host.</param>
/// <param name="Rate">Active rate in force today (Europe/Rome), or null when CasaZen has none for the comune.</param>
/// <param name="PublicPageSlug">
/// Comune slug of the reviewed public SEO page <c>/p/tassa-soggiorno/{slug}</c>, or null when there is no such page.
/// </param>
public record TouristTaxActivationInfo(string City, TouristTaxRate? Rate, string? PublicPageSlug)
{
    /// <summary>
    /// True when the comune has rates only for specific accommodation categories (Roma, Venezia): CasaZen does not know
    /// the category of the property, so it cannot pick one.
    /// </summary>
    public bool CategoryRequired { get; init; }
}

/// <param name="ConfirmDeparture">The host confirms that the guest left.</param>
/// <param name="SupplierOrgId">Optional supplier of the turnover request.</param>
/// <param name="ServiceNotes">Notes of the turnover request.</param>
/// <param name="ServiceCategory">Category of the turnover request (cleaning by default).</param>
/// <param name="RegisterArrival">
/// The host confirms that the guest arrived: a confirmed booking whose arrival was never registered is checked in with
/// the check-out ("registra arrivo e procedi", CO-08).
/// </param>
public record CompleteCheckoutWizardInput(
    bool ConfirmDeparture,
    Guid? SupplierOrgId,
    string? ServiceNotes,
    string? ServiceCategory,
    bool RegisterArrival = false);

public interface IComplianceWizardService
{
    Task<(Property Property, IReadOnlyList<ComplianceActivationStep> Steps)> GetActivationWizardAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates the property when no blocking step is left, otherwise leaves it pending. The safety checklist is saved
    /// on its own (<see cref="IPropertySafetyChecklistService"/>), never here. Returns the blocking steps still
    /// incomplete, each with its <see cref="ComplianceActivationStep.Blockers"/>.
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException"><c>activation_tos_required</c> without the terms accepted.</exception>
    Task<(Property Property, IReadOnlyList<ComplianceActivationStep> IncompleteBlockers)> CompleteActivationAsync(
        Guid propertyId,
        string userId,
        bool? tosAccepted,
        CancellationToken cancellationToken = default);

    Task<ComplianceSummaryResult> GetSummaryAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the check-out wizard of a booking the caller has already been authorized on (TN-3), with the rules of
    /// <see cref="IStayLifecycleService.StartCheckOutAsync"/>: <paramref name="registerArrival"/> registers first the
    /// arrival of a confirmed booking ("registra arrivo e procedi").
    /// </summary>
    Task<(Booking Booking, IReadOnlyList<ComplianceActivationStep> Steps)> StartCheckoutWizardAsync(
        Guid bookingId,
        bool registerArrival = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the checkout of a booking the caller has already been authorized on (TN-3), through
    /// <see cref="IStayLifecycleService.CheckOutAsync"/> (same rules as <c>POST /api/bookings/{id}/check-out</c>); the
    /// optional service request is created on behalf of <paramref name="userId"/> without further role checks.
    /// </summary>
    Task<(Booking Booking, bool PropertyReady)> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An item of the compliance cockpit (CO-04, A5-09): what to do (<see cref="Action"/>) and on what, never a front-end
/// path. <see cref="Id"/> is the target of the action: the property for
/// <see cref="ComplianceCockpitAction.ActivateProperty"/> (<see cref="PropertyId"/>), the booking for every other
/// action (<see cref="BookingId"/>).
/// </summary>
public sealed record ComplianceSummaryItem
{
    private ComplianceSummaryItem(Guid id, string label, ComplianceCockpitAction action, Guid? propertyId, Guid? bookingId)
    {
        Id = id;
        Label = label;
        Action = action;
        PropertyId = propertyId;
        BookingId = bookingId;
    }

    public Guid Id { get; }

    public string Label { get; }

    public ComplianceCockpitAction Action { get; }

    public Guid? PropertyId { get; }

    public Guid? BookingId { get; }

    /// <summary>True when the target of <paramref name="action"/> is a property, false when it is a booking.</summary>
    public static bool TargetsProperty(ComplianceCockpitAction action) => action == ComplianceCockpitAction.ActivateProperty;

    public static ComplianceSummaryItem ForProperty(ComplianceCockpitAction action, Guid propertyId, string label) =>
        TargetsProperty(action)
            ? new ComplianceSummaryItem(propertyId, label, action, propertyId, null)
            : throw new ArgumentException($"{action} targets a booking, not a property.", nameof(action));

    public static ComplianceSummaryItem ForBooking(ComplianceCockpitAction action, Guid bookingId, string label) =>
        !TargetsProperty(action)
            ? new ComplianceSummaryItem(bookingId, label, action, null, bookingId)
            : throw new ArgumentException($"{action} targets a property, not a booking.", nameof(action));
}

public record ComplianceSummarySection(int Count, IReadOnlyList<ComplianceSummaryItem> Items);

/// <param name="AlloggiatiFailures">Alloggiati communications in error or rejected.</param>
/// <param name="AlloggiatiManualRequired">
/// Alloggiati communications the host must send on the Questura portal (CasaZen does not transmit): never counted
/// as done (CO-11).
/// </param>
public record ComplianceSummaryResult(
    ComplianceSummarySection PropertiesPending,
    ComplianceSummarySection GuestCheckInsIncomplete,
    ComplianceSummarySection CheckoutsDue,
    ComplianceSummarySection AlloggiatiFailures,
    ComplianceSummarySection AlloggiatiManualRequired);
