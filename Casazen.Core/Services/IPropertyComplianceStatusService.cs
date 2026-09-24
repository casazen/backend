using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Compliance status of a short-stay property (CO-06, A5-20, A5-36): the single evaluation of the activation blockers
/// (base data, CIN, required documents, D.L. 145/2023 safety checklist) used by the activation wizard, the activation,
/// the re-evaluation after every change and the nightly check. Transitions:
/// <list type="bullet">
/// <item><see cref="PropertyComplianceStatus.Pending"/> → <see cref="PropertyComplianceStatus.Active"/> and
/// <see cref="PropertyComplianceStatus.Suspended"/> → <see cref="PropertyComplianceStatus.Active"/>: only by the host's
/// activation (<see cref="ActivateAsync"/>), when no blocker is left. A suspended property whose blockers are solved is
/// "ready for reactivation", never published again on its own.</item>
/// <item><see cref="PropertyComplianceStatus.Active"/> → <see cref="PropertyComplianceStatus.Suspended"/>: as soon as a
/// blocker appears (<see cref="ReevaluateAsync"/>), with the blocker codes as reason and one email to the host. The
/// bookings of the property are left as they are: nothing is cancelled.</item>
/// </list>
/// </summary>
public interface IPropertyComplianceStatusService
{
    /// <summary>
    /// The four blocking steps of the activation, in wizard order (<c>base-data</c>, <c>cin</c>, <c>documents</c>,
    /// <c>safety</c>), each <c>complete</c> or <c>pending</c> with its <see cref="ComplianceActivationStep.Blockers"/>.
    /// Read only. The documents and the checklist are read from the database, not from the navigation properties.
    /// </summary>
    Task<IReadOnlyList<ComplianceActivationStep>> GetBlockingStepsAsync(
        Property property,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates an <see cref="PropertyComplianceStatus.Active"/> property after a change of its CIN, documents,
    /// checklist or base data, or at the nightly check: with a blocker it becomes
    /// <see cref="PropertyComplianceStatus.Suspended"/> and the host gets one email. A pending or suspended property is
    /// left unchanged (never activated here). Idempotent and safe under concurrency: only the call that suspends the
    /// property notifies the host.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist (or belongs to another org).</exception>
    Task<PropertyComplianceCheck> ReevaluateAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host's activation (wizard, after the terms are accepted): <see cref="PropertyComplianceStatus.Active"/> when no
    /// blocker is left, from pending or suspended; otherwise the same as <see cref="ReevaluateAsync"/>.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist (or belongs to another org).</exception>
    Task<PropertyComplianceCheck> ActivateAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates every <see cref="PropertyComplianceStatus.Active"/> property (nightly job, one-shot command of the
    /// historic recalculation). One run at a time: returns null when another run holds the lock. A property that fails
    /// is logged and counted, the run goes on. With <paramref name="dryRun"/> nothing is written and no email is queued:
    /// the report says what a real run would do.
    /// </summary>
    Task<PropertyComplianceRecalculation?> RecalculateAllAsync(bool dryRun, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one evaluation of a property.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="PreviousStatus">Status before the evaluation.</param>
/// <param name="Status">Status after the evaluation.</param>
/// <param name="IncompleteSteps">Blocking steps still incomplete (empty when not evaluated: pending or suspended).</param>
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
}

/// <summary>Report of <see cref="IPropertyComplianceStatusService.RecalculateAllAsync"/>.</summary>
/// <param name="DryRun">Nothing was written.</param>
/// <param name="Checked">Active properties evaluated.</param>
/// <param name="Suspended">Properties suspended (or that a real run would suspend).</param>
/// <param name="HostsNotified">Suspension emails queued (or that a real run would queue).</param>
/// <param name="Failed">Properties whose evaluation failed (logged); the run went on.</param>
/// <param name="SuspendedByBlocker">Suspended properties per blocker code (a property counts once per code).</param>
public sealed record PropertyComplianceRecalculation(
    bool DryRun,
    int Checked,
    int Suspended,
    int HostsNotified,
    int Failed,
    IReadOnlyDictionary<string, int> SuspendedByBlocker);
