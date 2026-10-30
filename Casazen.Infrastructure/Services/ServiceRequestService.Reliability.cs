using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Services;

// What a supplier does with a request weighs on its reliability (SP-REL, SupplierRanking): an answer before the deadline earns a point, a
// job completed earns two. Both are written after the change is saved, by the winner only, and a failure to write them never fails the
// operation (ISupplierReliabilityService). The points lost for a missed deadline are written by the jobs that give the request up
// (ServiceRequestAutoCancelService), not here: nothing in this file runs when the supplier did not act.
public partial class ServiceRequestService
{
    /// <summary>
    /// Whether what the supplier does with <paramref name="request"/> counts for its ranking: the requests of the hosts do (short and long
    /// rent); a booking from the supplier's own public showcase does not. The customer of a showcase chose the supplier itself and the
    /// deadline is the supplier's own setting, so neither the ranking of the hosts nor its inputs have anything to do with them.
    /// </summary>
    internal static bool CountsForReliability(ServiceRequest request) => request.RentalContext != ServiceRequestRentalContext.Showcase;

    /// <summary>
    /// The supplier answered a new request (took it, refused it, proposed another time, sent a quote): a point if that was before
    /// <paramref name="dueAt"/>, the deadline the request had <b>before</b> the answer (the answer itself clears it). No deadline, no point.
    /// <paramref name="asBackup"/>: the supplier answered as one of the backups of the request (SP-BKP), read before the answer changes its role.
    /// </summary>
    private async Task RecordReplyAsync(ServiceRequest request, DateTime? dueAt, bool asBackup, CancellationToken cancellationToken)
    {
        if (reliability is null || dueAt is null || !CountsForReliability(request))
            return;

        await reliability.RecordReplyAsync(request.SupplierOrgId, request.Id, Now(), dueAt, asBackup, cancellationToken);
    }

    /// <summary>The supplier completed the work: a job done, which stays a clean one until a payer disputes it.</summary>
    private async Task RecordCompletedAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (reliability is null || !CountsForReliability(request))
            return;

        await reliability.RecordAsync(
            request.SupplierOrgId,
            SupplierReliabilityEventKind.JobCompletedClean,
            request.Id,
            request.CompletedAt ?? Now(),
            asBackup: false,
            detail: null,
            cancellationToken);
    }
}
