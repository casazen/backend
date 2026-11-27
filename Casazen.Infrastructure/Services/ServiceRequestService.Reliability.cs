using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Services;

// What a supplier does with a request weighs on its reliability (SP-REL, SupplierRanking): an answer before the deadline earns a point, a
// job completed earns two. Both are written after the change is saved, by the winner only, and a failure to write them never fails the
// operation (ISupplierReliabilityService). The points lost for a missed deadline are written by the jobs that give the request up
// (ServiceRequestAutoCancelService), not here: nothing in this file runs when the supplier did not act.
public partial class ServiceRequestService
{
    internal static bool CountsForReliability(ServiceRequest request) => request.RentalContext != ServiceRequestRentalContext.Showcase;

    private async Task RecordReplyAsync(ServiceRequest request, DateTime? dueAt, bool asBackup, CancellationToken cancellationToken)
    {
        if (reliability is null || dueAt is null || !CountsForReliability(request))
            return;

        await reliability.RecordReplyAsync(request.SupplierOrgId, request.Id, Now(), dueAt, asBackup, cancellationToken);
    }

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
