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

    /// <summary>
    /// Resource key of the label (<c>SharedResources</c>). When set, the API sends its localized text in place of
    /// <see cref="Label"/> (check-out wizard, CO-17).
    /// </summary>
    public string? LabelKey { get; init; }

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

/// <summary>What the host confirms and declares in the steps of the check-out wizard (CO-08, CO-17).</summary>
/// <param name="ConfirmDeparture">Step 1: the host confirms that the guest left.</param>
/// <param name="SupplierOrgId">Step 3: supplier of the cleaning request.</param>
/// <param name="ServiceNotes">Step 3: notes of the cleaning request.</param>
/// <param name="ServiceCategory">Step 3: category of the request (cleaning by default).</param>
/// <param name="RegisterArrival">
/// The host confirms that the guest arrived: a confirmed booking whose arrival was never registered is checked in with
/// the check-out ("registra arrivo e procedi", CO-08).
/// </param>
public record CompleteCheckoutWizardInput(
    bool ConfirmDeparture,
    Guid? SupplierOrgId,
    string? ServiceNotes,
    string? ServiceCategory,
    bool RegisterArrival = false)
{
    /// <summary>
    /// Step 3: <see cref="CheckoutCleaningChoice.Request"/> needs <see cref="SupplierOrgId"/>,
    /// <see cref="CheckoutCleaningChoice.Skip"/> refuses it. Null: a request when a supplier is given (the contract
    /// before CO-17), otherwise nothing chosen.
    /// </summary>
    public CheckoutCleaningChoice? CleaningChoice { get; init; }

    /// <summary>Step 4: how the tourist tax was collected; null = not declared.</summary>
    public TouristTaxCollection? TouristTaxCollection { get; init; }

    /// <summary>Step 5: the property is ready for the next guest; null or false = not ready yet (cockpit turnover).</summary>
    public bool? PropertyReady { get; init; }

    /// <summary>Step 5: notes on the state of the property.</summary>
    public string? PropertyNotes { get; init; }
}

/// <summary>The 5 steps of the check-out wizard as the API names them (<c>steps[].id</c>, <c>currentStep</c>).</summary>
public static class CheckoutWizardSteps
{
    public const string StaySummary = "stay-summary";
    public const string Alloggiati = "alloggiati";
    public const string Cleaning = "cleaning";
    public const string TouristTax = "tourist-tax";
    public const string PropertyReady = "property-ready";

    private static readonly IReadOnlyDictionary<CheckoutWizardStep, string> Ids = new Dictionary<CheckoutWizardStep, string>
    {
        [CheckoutWizardStep.StaySummary] = StaySummary,
        [CheckoutWizardStep.Alloggiati] = Alloggiati,
        [CheckoutWizardStep.Cleaning] = Cleaning,
        [CheckoutWizardStep.TouristTax] = TouristTax,
        [CheckoutWizardStep.PropertyReady] = PropertyReady,
    };

    /// <summary>Id of <paramref name="step"/>; the first step for a value that is not a step.</summary>
    public static string IdOf(CheckoutWizardStep step) => Ids.TryGetValue(step, out var id) ? id : StaySummary;

    /// <summary>The step named <paramref name="id"/>, or false when it is not one of the 5 steps.</summary>
    public static bool TryParse(string? id, out CheckoutWizardStep step)
    {
        foreach (var (candidate, candidateId) in Ids)
        {
            if (string.Equals(candidateId, id, StringComparison.Ordinal))
            {
                step = candidate;
                return true;
            }
        }

        step = CheckoutWizardStep.StaySummary;
        return false;
    }
}

/// <summary>
/// The check-out wizard of a stay as the host sees it (CO-17, A5-24): the booking (with property and guest), its
/// saved progress or outcome, the 5 steps with their status and what each step shows.
/// </summary>
/// <param name="Booking">The booking, with its property and guest, as committed now.</param>
/// <param name="Checkout">Progress and outcome of the check-out; null before the wizard is started.</param>
/// <param name="Steps">The 5 steps, in order.</param>
/// <param name="Alloggiati">Step 2.</param>
/// <param name="TouristTax">Step 4.</param>
public sealed record CheckoutWizardState(
    Booking Booking,
    StayCheckout? Checkout,
    IReadOnlyList<ComplianceActivationStep> Steps,
    CheckoutAlloggiatiSummary Alloggiati,
    CheckoutTouristTaxSummary TouristTax)
{
    /// <summary>The step the wizard opens on: the saved one, the last one once the stay is checked out.</summary>
    public CheckoutWizardStep CurrentStep =>
        Booking.Status == BookingStatus.CheckedOut
            ? CheckoutWizardStep.PropertyReady
            : Checkout?.CurrentStep ?? CheckoutWizardStep.StaySummary;

    /// <summary>The property was declared ready for the next guest.</summary>
    public bool PropertyReady => Checkout?.PropertyReadyAt is not null;
}

/// <summary>Step 2: the Alloggiati Web communication of the stay (CO-11: CasaZen does not transmit it).</summary>
/// <param name="Status">Status shown to the host (<c>AlloggiatiStatusRules.Effective</c>).</param>
/// <param name="Sent">Sent with a receipt, or declared sent by the host.</param>
/// <param name="DeadlineAt">Legal deadline of the communication (UTC).</param>
/// <param name="IsOverdue">Deadline passed and not sent.</param>
/// <param name="DataComplete">The data of every guest of the stay are complete (CO-12).</param>
public sealed record CheckoutAlloggiatiSummary(
    AlloggiatiWebStatus Status,
    bool Sent,
    DateTime DeadlineAt,
    bool IsOverdue,
    bool DataComplete);

/// <summary>Step 4: the tourist tax of the stay (BK-03) and how it was collected.</summary>
/// <param name="RecordedAmount">
/// Tax recorded on the booking when CasaZen priced it (booking site or host booking, BK-03) and it is above 0; null
/// otherwise (channel bookings, comune without a rate): CasaZen never invents an amount.
/// </param>
/// <param name="CollectedWithOnlinePayment">
/// The tax was part of the online payment of the booking site, already collected: the wizard proposes "online".
/// </param>
/// <param name="Collection">What the host declared; null = not declared.</param>
public sealed record CheckoutTouristTaxSummary(
    decimal? RecordedAmount,
    bool CollectedWithOnlinePayment,
    TouristTaxCollection? Collection);

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
    /// arrival of a confirmed booking ("registra arrivo e procedi"). Opened again, the wizard keeps its progress.
    /// </summary>
    Task<CheckoutWizardState> StartCheckoutWizardAsync(
        Guid bookingId,
        bool registerArrival = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The check-out wizard of a booking the caller has already been authorized on, without any transition: after the
    /// check-out it shows what was declared and lets the host declare the property ready (CO-17).
    /// </summary>
    Task<CheckoutWizardState> GetCheckoutWizardAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>Saves the progress of the wizard (<see cref="IStayLifecycleService.SaveCheckoutProgressAsync"/>).</summary>
    Task<CheckoutWizardState> SaveCheckoutProgressAsync(
        Guid bookingId,
        StayCheckoutProgress progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the checkout of a booking the caller has already been authorized on (TN-3), through
    /// <see cref="IStayLifecycleService.CheckOutAsync"/> (same rules as <c>POST /api/bookings/{id}/check-out</c>), once
    /// the wizard was started (409 <c>checkout_wizard_not_started</c>): the cleaning request of step 3 is created for the
    /// stay on behalf of <paramref name="userId"/> without further role checks, and the tax collection and the property
    /// readiness are recorded as declared. Nothing is assumed: a property not declared ready stays a turnover to close.
    /// </summary>
    Task<CheckoutWizardState> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The host declares the property ready after the check-out (<see cref="IStayLifecycleService.ConfirmPropertyReadyAsync"/>).
    /// </summary>
    Task<CheckoutWizardState> ConfirmPropertyReadyAsync(
        Guid bookingId,
        string? notes,
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
/// <param name="TurnoversPending">
/// Stays checked out whose property was not declared ready for the next guest, until the host declares it or a later
/// stay of the property arrives (CO-17).
/// </param>
public record ComplianceSummaryResult(
    ComplianceSummarySection PropertiesPending,
    ComplianceSummarySection GuestCheckInsIncomplete,
    ComplianceSummarySection CheckoutsDue,
    ComplianceSummarySection AlloggiatiFailures,
    ComplianceSummarySection AlloggiatiManualRequired,
    ComplianceSummarySection TurnoversPending);
