using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The automatic acceptance of a regular host's request (SP-04d, decision SP-D6), one place for the service and the runbook
/// (<c>docs/runbooks/suppliers.md</c> § 21.14). The supplier turns it on in its settings
/// (<see cref="Entities.SupplierSettings.AutoAcceptRegulars"/>, off for every supplier until it does); from then on a request that a
/// host makes is <b>born taken</b> (<see cref="ServiceRequestStatus.PresoInCarico"/>, <see cref="Entities.ServiceRequest.AutoAccepted"/>)
/// instead of <see cref="ServiceRequestStatus.Richiesto"/> when <b>all</b> of this holds, and otherwise stays exactly as it was:
/// <list type="number">
/// <item>the supplier turned the option on, is <c>Active</c> and has no new version of the Terms of Service to accept (the same
/// gate as a take by hand);</item>
/// <item>the request is for a <b>service of the catalog</b> that is priced (not "on quote") and its <b>time was verified by the
/// planner</b> under the supplier's calendar lock: the host did not type a duration, because a typed duration skips the check of the
/// published hours (<see cref="IsEligibleRequest"/>);</item>
/// <item>it is a <b>host's</b> request: the public showcase's private customers have no org, are not regulars and are never accepted
/// this way (their requests are not created by the host's path at all);</item>
/// <item>the host's org is a <b>regular</b> of the supplier: it has at least <c>RegularHostMinJobs</c> (3) other requests to this
/// supplier in a status of <see cref="JobStatuses"/> (<see cref="IsRegular"/>).</item>
/// </list>
/// The slot and the overlap with the jobs the supplier already accepted are checked as for any request, so an automatic acceptance
/// never makes two jobs overlap: if the time is not free the request is refused with 409 as it always was.
/// </summary>
public static class ServiceRequestAutoAccept
{
    public static readonly ServiceRequestStatus[] JobStatuses =
    [
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
        ServiceRequestStatus.Completato,
        ServiceRequestStatus.Pagato,
    ];

    public static bool IsEligibleRequest(SupplierServiceForRequest? service, int? typedDurationMinutes) =>
        service is { RequiresQuote: false } && typedDurationMinutes is null;

    public static bool IsRegular(int jobsTaken, int minJobs) => jobsTaken >= minJobs;
}
