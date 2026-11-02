using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Compliance status of a short-stay property (CO-06, A5-20, A5-36): the single evaluation of the activation steps (base data,
/// CIN, required documents, D.L. 145/2023 safety checklist) used by the activation wizard, the activation, the re-evaluation after
/// every change and the nightly check. Only the base data and the CIN block (PO decision 2026-11-01, option A, as Airbnb and
/// Booking.com do: the CIN is the one thing the listing itself must show, art. 13-ter c. 6); the documents and the safety checklist
/// are the host's own obligations and are reminders, which the host acknowledges once when activating with some still open
/// (<see cref="HostActivation"/>). Transitions:
/// <list type="bullet">
/// <item><see cref="PropertyComplianceStatus.Pending"/> → <see cref="PropertyComplianceStatus.Active"/>: only by the
/// host's activation (<see cref="ActivateAsync"/>, terms accepted), when no blocker is left.</item>
/// <item><see cref="PropertyComplianceStatus.Active"/> → <see cref="PropertyComplianceStatus.Suspended"/>: as soon as a
/// blocker appears (<see cref="ReevaluateAsync"/>), with the blocker codes as reason and one email to the host. The
/// bookings of the property are left as they are: nothing is cancelled.</item>
/// <item><see cref="PropertyComplianceStatus.Suspended"/> → <see cref="PropertyComplianceStatus.Active"/>: as soon as
/// no blocker is left (<see cref="ReevaluateAsync"/> after the change that completes the requirements, the nightly check,
/// or the host's activation).</item>
/// </list>
/// A property in long-term mode (<see cref="RentalMode.Long"/>, PM-01) is outside all of this: it is never evaluated,
/// suspended or reactivated, its host is not written to, and it cannot be activated.
/// </summary>
public interface IPropertyComplianceStatusService
{
    /// <summary>
    /// The four compliance steps of the activation, in wizard order (<c>base-data</c>, <c>cin</c>, <c>documents</c>,
    /// <c>safety</c>), each with its <see cref="ComplianceActivationStep.Blockers"/>: the first two block (<c>complete</c> or
    /// <c>pending</c>), the last two are reminders (<see cref="ComplianceActivationStep.Blocker"/> false, <c>complete</c> or
    /// <c>warning</c>, their blockers being the points still open). Read only. The documents and the checklist are read from the
    /// database, not from the navigation properties.
    /// </summary>
    Task<IReadOnlyList<ComplianceActivationStep>> GetBlockingStepsAsync(
        Property property,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="GetBlockingStepsAsync"/> for many properties at once (the host's list, SR-07a): the very same rules, applied to
    /// facts read for all of them together — the documents of all, the safety checklists of all and the comuni list once — so the
    /// number of queries is fixed whatever the number of properties. The answer holds one entry for each property given, each
    /// equal to what <see cref="GetBlockingStepsAsync"/> says for that property alone. Read only.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ComplianceActivationStep>>> GetBlockingStepsManyAsync(
        IReadOnlyCollection<Property> properties,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates a property after a change of its CIN, documents, checklist or base data, or at the nightly check: an
    /// <see cref="PropertyComplianceStatus.Active"/> one with a blocker becomes <see cref="PropertyComplianceStatus.Suspended"/>
    /// and the host gets one email; a suspended one without blockers becomes active again; a pending one is left
    /// unchanged (never activated here); a long-term one (PM-01) is left unchanged too. Idempotent and safe under
    /// concurrency: only the call that suspends the property notifies the host.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist (or belongs to another org).</exception>
    Task<PropertyComplianceCheck> ReevaluateAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// An activation by CasaZen itself (the demo environment): <see cref="PropertyComplianceStatus.Active"/> when no blocker is
    /// left, from pending or suspended; otherwise the same as <see cref="ReevaluateAsync"/>. No reminder is asked about and no
    /// activity is recorded: a host activates with <see cref="ActivateAsync(Guid, HostActivation, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist (or belongs to another org).</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/> (422): the property is in long-term mode (PM-01).
    /// </exception>
    Task<PropertyComplianceCheck> ActivateAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host's activation (wizard, after the terms are accepted): as <see cref="ActivateAsync(Guid, CancellationToken)"/>, and
    /// when no blocker is left but some reminders are open (the documents, the safety checklist) the host must have acknowledged
    /// them (<see cref="HostActivation.RemindersAcknowledged"/>): the activation then records
    /// <see cref="OrgActivityType.PropertyActivatedWithReminders"/> in the activity log of the org, with the reminders open.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist (or belongs to another org).</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/> (422): the property is in long-term mode (PM-01).
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="RemindersAcknowledgementRequiredCode"/> (409): reminders open and not acknowledged; nothing changes.
    /// </exception>
    Task<PropertyComplianceCheck> ActivateAsync(Guid propertyId, HostActivation host, CancellationToken cancellationToken = default);

    /// <summary>Code of the 409 of a host activation with compliance reminders open and not acknowledged.</summary>
    const string RemindersAcknowledgementRequiredCode = "activation_reminders_ack_required";

    /// <summary>
    /// Re-evaluates every short-rent (PM-01) <see cref="PropertyComplianceStatus.Active"/> or
    /// <see cref="PropertyComplianceStatus.Suspended"/> property (nightly job, one-shot command of the historic recalculation). One run at a time: returns null when another run holds the lock. A property that fails
    /// is logged and counted, the run goes on. With <paramref name="dryRun"/> nothing is written and no email is queued:
    /// the report says what a real run would do.
    /// </summary>
    Task<PropertyComplianceRecalculation?> RecalculateAllAsync(bool dryRun, CancellationToken cancellationToken = default);
}

/// <summary>A host activating a property (<see cref="IPropertyComplianceStatusService.ActivateAsync(Guid, HostActivation, CancellationToken)"/>).</summary>
/// <param name="UserId">Who activates (<c>User.Id</c>): the actor of the activity line.</param>
/// <param name="RemindersAcknowledged">
/// The host declared to know that the reminders still open (documents, safety checklist) are their own obligations and to complete
/// them later. Needed only when some are open.
/// </param>
public sealed record HostActivation(string UserId, bool RemindersAcknowledged);

/// <summary>Outcome of one evaluation of a property.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="PreviousStatus">Status before the evaluation.</param>
/// <param name="Status">Status after the evaluation.</param>
/// <param name="IncompleteSteps">Blocking steps still incomplete (empty when not evaluated: pending).</param>
/// <param name="HostNotified">True when this call queued the suspension email to the host.</param>
public sealed record PropertyComplianceCheck(
    Guid PropertyId,
    PropertyComplianceStatus PreviousStatus,
    PropertyComplianceStatus Status,
    IReadOnlyList<ComplianceActivationStep> IncompleteSteps,
    bool HostNotified)
{
    /// <summary>This call moved the property from active to suspended.</summary>
    public bool Suspended => PreviousStatus == PropertyComplianceStatus.Active && Status == PropertyComplianceStatus.Suspended;

    /// <summary>This call moved the property from suspended back to active.</summary>
    public bool Reactivated => PreviousStatus == PropertyComplianceStatus.Suspended && Status == PropertyComplianceStatus.Active;
}

/// <summary>Report of <see cref="IPropertyComplianceStatusService.RecalculateAllAsync"/>.</summary>
/// <param name="DryRun">Nothing was written.</param>
/// <param name="Checked">Active and suspended properties evaluated.</param>
/// <param name="Suspended">Properties suspended (or that a real run would suspend).</param>
/// <param name="Reactivated">Suspended properties active again (or that a real run would reactivate).</param>
/// <param name="HostsNotified">Suspension emails queued (or that a real run would queue).</param>
/// <param name="Failed">Properties whose evaluation failed (logged); the run went on.</param>
/// <param name="SuspendedByBlocker">Suspended properties per blocker code (a property counts once per code).</param>
public sealed record PropertyComplianceRecalculation(
    bool DryRun,
    int Checked,
    int Suspended,
    int Reactivated,
    int HostsNotified,
    int Failed,
    IReadOnlyDictionary<string, int> SuspendedByBlocker);
