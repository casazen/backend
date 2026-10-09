using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// What the console home of a supplier shows (SP-04, <c>GET api/supplier/today</c>): the jobs of the day, the new requests
/// to answer (the most urgent first), the earnings of the month, what is left to collect and the time it takes to answer.
/// The items are <see cref="SupplierServiceRequestView"/>s: what the supplier may see before and after the take (decision D9).
/// </summary>
/// <param name="Date">Today, in Europe/Rome.</param>
/// <param name="Jobs">
/// The jobs whose day is today (taken, in progress, completed or paid), the first in time first; a job with no day (to agree
/// with the host) is not here.
/// </param>
/// <param name="NewRequests">The first requests waiting for the supplier's answer, the one with the earliest deadline first.</param>
/// <param name="NewRequestsTotal">How many requests wait for the supplier's answer.</param>
/// <param name="Earnings">The earnings of the month, the amount to collect and the average response time (an estimate until SP-15).</param>
public sealed record SupplierToday(
    DateOnly Date,
    IReadOnlyList<SupplierServiceRequestView> Jobs,
    IReadOnlyList<SupplierServiceRequestView> NewRequests,
    int NewRequestsTotal,
    SupplierEarningsSummary Earnings);

/// <summary>
/// The steps of the activation checklist of the console (SP-04, <c>GET api/supplier/checklist</c>), each computed from what the
/// supplier has really done.
/// </summary>
/// <param name="ProfileCompletionPercent">How much of the profile is filled in (identity, categories, comuni, bio, accepted terms).</param>
/// <param name="ProfileComplete">The profile is complete (100 %).</param>
/// <param name="ActiveServices">How many services the supplier has published; the step is done with at least one.</param>
/// <param name="HoursConfigured">The supplier saved working hours with at least one band.</param>
/// <param name="HoursConfiguredAt">When it last saved them (<c>SupplierSettings.HoursConfiguredAt</c>); <c>null</c> while it did not.</param>
/// <param name="ShowcasePublished">The public showcase is online: an active supplier with a showcase address.</param>
/// <param name="FirstRequestAnswered">The supplier answered a request at least once: it took one or rejected one.</param>
/// <param name="PaymentsActive">
/// Whether the supplier receives payments through CasaZen. <b>Always <c>null</c> for now</b>: the payments arrive with SP-14 and
/// SP-15, so the answer "not available yet" is not a "no" and the console does not count the step.
/// </param>
public sealed record SupplierChecklist(
    int ProfileCompletionPercent,
    bool ProfileComplete,
    int ActiveServices,
    bool HoursConfigured,
    DateTime? HoursConfiguredAt,
    bool ShowcasePublished,
    bool FirstRequestAnswered,
    bool? PaymentsActive);

/// <summary>
/// The two reads of the supplier's console home (SP-04), composed from the service requests, the profile, the catalog and the
/// agenda. Every read is scoped by the supplier org of the caller.
/// </summary>
public interface ISupplierTodayService
{
    /// <summary>The home of the supplier for today (Europe/Rome). Read-only.</summary>
    Task<SupplierToday> GetTodayAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>The activation checklist of the supplier. Read-only; <c>null</c> when the org has no supplier profile.</summary>
    Task<SupplierChecklist?> GetChecklistAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);
}
