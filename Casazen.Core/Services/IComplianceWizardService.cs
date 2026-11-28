using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public record ComplianceActivationStep(string Id, string Label, string Status, bool Blocker, string? Message = null)
{
    public string? MessageKey { get; init; }

    public IReadOnlyList<object>? MessageArgs { get; init; }

    /// <summary>External guidance link of the step (for the CIN step: <c>Compliance:CinGuidanceUrl</c>).</summary>
    public string? LinkUrl { get; init; }

    public string? LabelKey { get; init; }

    public TouristTaxActivationInfo? TouristTax { get; init; }

    public IReadOnlyList<ActivationBlocker> Blockers { get; init; } = [];

    public IReadOnlyList<ActivationWarning> Warnings { get; init; } = [];
}

public sealed record ActivationWarning(string Code, string MessageKey, IReadOnlyList<object> MessageArgs)
{
    public ActivationWarning(string code, string messageKey)
        : this(code, messageKey, [])
    {
    }
}

public sealed record ActivationBlocker(string Code, string MessageKey, IReadOnlyList<object> MessageArgs)
{
    public ActivationBlocker(string code, string messageKey)
        : this(code, messageKey, [])
    {
    }
}

public record TouristTaxActivationInfo(string City, TouristTaxRate? Rate, string? PublicPageSlug)
{
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

    public TouristTaxCollection? TouristTaxCollection { get; init; }

    public bool? PropertyReady { get; init; }

    public string? PropertyNotes { get; init; }
}

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

    public static string IdOf(CheckoutWizardStep step) => Ids.TryGetValue(step, out var id) ? id : StaySummary;

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

public sealed record CheckoutWizardState(
    Booking Booking,
    StayCheckout? Checkout,
    IReadOnlyList<ComplianceActivationStep> Steps,
    CheckoutAlloggiatiSummary Alloggiati,
    CheckoutTouristTaxSummary TouristTax)
{
    public CheckoutWizardStep CurrentStep =>
        Booking.Status == BookingStatus.CheckedOut
            ? CheckoutWizardStep.PropertyReady
            : Checkout?.CurrentStep ?? CheckoutWizardStep.StaySummary;

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
    /// Activates the property when no blocking step is left (the base data and the CIN), otherwise leaves it pending. The
    /// reminders still open (the documents, the safety checklist) need <paramref name="remindersAcknowledged"/>, which is
    /// recorded in the activity log of the org. The safety checklist is saved on its own
    /// (<see cref="IPropertySafetyChecklistService"/>), never here. Returns the blocking steps still incomplete, each with its
    /// <see cref="ComplianceActivationStep.Blockers"/>.
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <c>activation_tos_required</c> without the terms accepted; <c>activation_reminders_ack_required</c> with reminders open
    /// and not acknowledged.
    /// </exception>
    Task<(Property Property, IReadOnlyList<ComplianceActivationStep> IncompleteBlockers)> CompleteActivationAsync(
        Guid propertyId,
        string userId,
        bool? tosAccepted,
        bool? remindersAcknowledged = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The compliance cockpit of the caller (US-019 AC10): the properties to activate, the check-ins to complete, the
    /// check-outs due, the Alloggiati to send and the turnovers to close, <b>of the properties <paramref name="scope"/>
    /// reaches</b> (AM-03: it used to filter by org only, so a member «Solo alcuni» saw the whole org's work).
    /// </summary>
    Task<ComplianceSummaryResult> GetSummaryAsync(HostScope scope, CancellationToken cancellationToken = default);

    Task<CheckoutWizardState> StartCheckoutWizardAsync(
        Guid bookingId,
        bool registerArrival = false,
        CancellationToken cancellationToken = default);

    Task<CheckoutWizardState> GetCheckoutWizardAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task<CheckoutWizardState> SaveCheckoutProgressAsync(
        Guid bookingId,
        StayCheckoutProgress progress,
        CancellationToken cancellationToken = default);

    Task<CheckoutWizardState> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default);

    Task<CheckoutWizardState> ConfirmPropertyReadyAsync(
        Guid bookingId,
        string? notes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An item of the compliance cockpit (CO-04, A5-09): what to do (<see cref="Action"/>) and on what, never a front-end
/// path. <see cref="Id"/> is the target of the action: the property for the actions that target one
/// (<see cref="TargetsProperty"/>: <see cref="ComplianceCockpitAction.ActivateProperty"/> and the property actions of CHK-12), the
/// booking for every other action (<see cref="BookingId"/>). Since E03/F02/US01/T03 every item also says whether the host is compliant:
/// its <see cref="Light"/> and a stable <see cref="Reason"/>, add-only like the other new members.
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

    public IReadOnlyList<ComplianceMissing> Missing { get; init; } = [];

    /// <summary>
    /// When the item has to be done by (UTC), if the stay fixes it (<see cref="ComplianceDeadlines"/>): the legal term of the
    /// Alloggiati communication, the day the guest arrives, the day the guest leaves. Null for the others. The Home's "Da fare"
    /// gives the same term, but only for the first things to do: the page of the adempimenti dates every row it lists.
    /// </summary>
    public DateTime? DueAt { get; init; }

    public ComplianceLight? Light { get; init; }

    public string? Reason { get; init; }

    /// <summary>The month (<c>yyyy-MM</c>) of an item about an ISTAT or tourist tax period; <c>null</c> for the others.</summary>
    public string? Period { get; init; }

    public ComplianceEvidenceClass? EvidenceClass { get; init; }

    public string? EvidenceSource { get; init; }

    public DateTime? LastVerifiedAt { get; init; }

    public OctorateHandoffTopic? HandoffTopic { get; init; }

    /// <summary>
    /// True when the target of <paramref name="action"/> is a property, false when it is a booking. The property actions are
    /// <see cref="ComplianceCockpitAction.ActivateProperty"/> and, since CHK-12, the ones that fix a connection, a credential, a period
    /// or a rate.
    /// </summary>
    public static bool TargetsProperty(ComplianceCockpitAction action) =>
        action is ComplianceCockpitAction.ActivateProperty
            or ComplianceCockpitAction.ActivateOctorateSend
            or ComplianceCockpitAction.UploadIstatProof
            or ComplianceCockpitAction.EnterMunicipalityTaxRate
            or ComplianceCockpitAction.DeclareTouristTax
            or ComplianceCockpitAction.RenewPoliceCredentials
            or ComplianceCockpitAction.NotifyTicinoPolice
            or ComplianceCockpitAction.NotifyGuestsAuthority;

    public static ComplianceSummaryItem ForProperty(ComplianceCockpitAction action, Guid propertyId, string label) =>
        TargetsProperty(action)
            ? new ComplianceSummaryItem(propertyId, label, action, propertyId, null)
            : throw new ArgumentException($"{action} targets a booking, not a property.", nameof(action));

    public static ComplianceSummaryItem ForBooking(ComplianceCockpitAction action, Guid bookingId, string label) =>
        !TargetsProperty(action)
            ? new ComplianceSummaryItem(bookingId, label, action, null, bookingId)
            : throw new ArgumentException($"{action} targets a property, not a booking.", nameof(action));
}

/// <summary>
/// When an item of the cockpit about a stay has to be done by (<see cref="ComplianceSummaryItem.DueAt"/>), the same rule the Home's
/// "Da fare" uses (<c>HostTodayService</c>): the legal term of the Alloggiati communication, which runs from the arrival
/// (<see cref="AlloggiatiTerms.DeadlineUtc(DateTime?, DateTime, DateTime)"/>), the day the guest arrives (the data are needed by then)
/// and the day the guest leaves. Nothing for the others (a property to activate, a turnover): no date fixes them.
/// </summary>
public static class ComplianceDeadlines
{
    public static DateTime? Of(ComplianceCockpitAction action, DateTime checkInDate, DateTime checkOutDate, DateTime? arrivedAt) => action switch
    {
        ComplianceCockpitAction.ResolveAlloggiatiFailure or ComplianceCockpitAction.SendAlloggiati =>
            AlloggiatiTerms.DeadlineUtc(arrivedAt, checkInDate, checkOutDate),
        ComplianceCockpitAction.CompleteGuestCheckIn => AlloggiatiTerms.ArrivalDayStartUtc(checkInDate),
        ComplianceCockpitAction.CheckOut => RomeCalendar.StartOfDayUtc(checkOutDate),
        _ => null,
    };
}

public record ComplianceSummarySection(int Count, IReadOnlyList<ComplianceSummaryItem> Items)
{
    public static ComplianceSummarySection Empty { get; } = new(0, []);
}

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
    ComplianceSummarySection TurnoversPending)
{
    public ComplianceSummarySection IdentityPending { get; init; } = ComplianceSummarySection.Empty;

    /// <summary>
    /// The Alloggiati communications that were sent, entrusted to the channel manager or declared sent, with the receipt archived (green) or
    /// awaited (yellow, then red the day after). A stay here is not in <see cref="AlloggiatiManualRequired"/>: the host does not send it by hand.
    /// </summary>
    public ComplianceSummarySection ReceiptsToUpload { get; init; } = ComplianceSummarySection.Empty;

    public ComplianceSummarySection ManualSendsRequired { get; init; } = ComplianceSummarySection.Empty;

    public ComplianceSummarySection ConnectionsToFix { get; init; } = ComplianceSummarySection.Empty;

    /// <summary>The ISTAT and tourist tax of the closed months, and the tourist tax rates that are not known.</summary>
    public ComplianceSummarySection PeriodItems { get; init; } = ComplianceSummarySection.Empty;

    /// <summary>
    /// The arrival days of the properties in canton Ticino whose guests are not notified to the cantonal police yet (casazen/backend#979, within
    /// 24 hours of the arrival): one item per property and day (<c>NotifyTicinoPolice</c>, the day in <c>Period</c>), oldest first. Filled whatever
    /// the traffic light: it is the police obligation of those properties, which owe no Alloggiati communication.
    /// </summary>
    public ComplianceSummarySection TicinoNotifications { get; init; } = ComplianceSummarySection.Empty;

    /// <summary>
    /// The arrival days of the properties of a canton other than Ticino whose guests are not notified to the cantonal authority yet, where the canton asks for an
    /// electronic notification and the duty of the property is <c>Required</c> (E31/F02/US01/T01, NF-131): one item per property and day
    /// (<c>NotifyGuestsAuthority</c>, the day in <c>Period</c>, the deadline of the regime in <c>DueAt</c>, <c>null</c> where the regime has none), oldest first. Ticino is
    /// never here: it has its own section above. Empty while <c>Features:GuestRegistrationRegimes</c> is off. Filled whatever the traffic light, like the Ticino section.
    /// </summary>
    public ComplianceSummarySection SwissNotifications { get; init; } = ComplianceSummarySection.Empty;
}
