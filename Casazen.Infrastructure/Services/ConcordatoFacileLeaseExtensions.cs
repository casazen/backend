using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The rule that opens everything the ConcordatoFacile endpoints do for a lease (CF-FIX, CF-FIX2): the lease is the caller's org's.
/// </summary>
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
}
