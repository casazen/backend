using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

internal static class ConcordatoFacileLeaseExtensions
{
    /// <summary>
    /// Throws the 404 <c>lease_not_found</c> when the lease does not exist or belongs to another org: the same answer for both, so that
    /// nothing tells which. The query names the org on top of the tenant filter of the context. Call it before anything is paid, written or
    /// asked to ConcordatoFacile.
    /// </summary>
    public static async Task EnsureLeaseOfOrgAsync(this AppDbContext db, Guid leaseId, Guid orgId, CancellationToken cancellationToken)
    {
        var leaseOfTheOrg = await db.LeaseContracts
            .AnyAsync(l => l.Id == leaseId && l.OrgId == orgId, cancellationToken);
        if (!leaseOfTheOrg)
        {
            throw new NotFoundException($"Lease {leaseId} not found.") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };
        }
    }

    /// <summary>
    /// CF-DONE: the canone concordato draft whose guided form is opened is the service's draft from then on: an imported draft (the contract
    /// comes back signed, to be read into the draft and imported) marked <c>IsConcordatoFacileSource</c>, as «Parto da zero» makes it since
    /// CF-IMPORT. A draft made before that (origin Created, no mark) would otherwise never read the documents the service delivers into its
    /// data and could never be imported. Returns whether the lease changed; anything but a concordato draft is left as it is.
    /// </summary>
    public static bool MarkAsServiceDraft(this LeaseContract lease)
    {
        if (lease.Status != LeaseStatus.Draft || lease.ContractType != LeaseContractType.Concordato)
            return false;
        if (lease.IsConcordatoFacileSource && lease.Origin == LeaseOrigin.Imported)
            return false;

        if (lease.ReviewConfirmedAt is not null || lease.SentForSignatureAt is not null)
            return false;

        lease.IsConcordatoFacileSource = true;
        lease.Origin = LeaseOrigin.Imported;
        return true;
    }
}
