using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the two jobs of the OTA conversations have in common (E05/F01/US01/T03): which orgs they read. A system read over every org, with the org
/// named in the predicate and no tenant filter, as the jobs of the bookings do; each org is then read for itself under
/// <see cref="Casazen.Core.Services.IOctorateOrgScope.Use"/>.
/// </summary>
internal static class OtaMessageRuns
{
    internal static async Task<IReadOnlyList<Guid>> ListConnectedOrgsAsync(AppDbContext db, OctorateEnvironment environment, CancellationToken ct) =>
        await db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => c.OrgId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);
}
