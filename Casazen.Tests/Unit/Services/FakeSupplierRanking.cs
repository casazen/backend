using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The ranking of the active suppliers of a comune and category (<see cref="ISupplierMatchService.RankActiveAsync"/>) over the test database,
/// without the whole supplier service: the active profiles whose comuni and categories contain the ones asked, by legal name, so a test
/// names its backups in the order it expects them chosen. <see cref="ISupplierMatchService.MatchAsync"/> is not used by the backups.
/// </summary>
internal sealed class FakeSupplierRanking(AppDbContext db) : ISupplierMatchService
{
    public Task<SupplierMatchResult> MatchAsync(
        Guid orgId,
        Guid propertyId,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The backup suppliers rank with RankActiveAsync.");

    public async Task<IReadOnlyList<RankedSupplier>> RankActiveAsync(
        ComuneTarget target,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default)
    {
        var comune = target.IstatCode ?? target.Name ?? string.Empty;
        var profiles = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.Status == SupplierStatus.Active)
            .ToListAsync(cancellationToken);

        return profiles
            .Where(sp => sp.ComuniJson.Contains(comune, StringComparison.Ordinal) && sp.CategoriesJson.Contains(category, StringComparison.Ordinal))
            .OrderBy(sp => sp.LegalName, StringComparer.Ordinal)
            .Select(sp => new RankedSupplier(sp, 70, 0))
            .ToList();
    }
}
