using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostSupplierUsageReader"/>
/// <remarks>
/// Two reads, whatever the number of requests: a thin row for each short-rent request of the host's scope (the supplier, its category,
/// its state and when it was made: no note, no price, no property, no guest), grouped here, and the name of the suppliers found.
/// <see cref="Casazen.Core.Entities.ServiceRequest"/> has two parties and no tenant filter (TN-2 allow-list): the host's rows are the
/// ones of its org (the explicit <c>OrgId</c> predicate), in its scope (<c>InScope</c>), of the short-rent context; a request born
/// from a supplier's showcase belongs to the supplier's own org and is never a host's.
/// </remarks>
public sealed class HostSupplierUsageReader(AppDbContext db) : IHostSupplierUsageReader
{
    public async Task<IReadOnlyList<HostUsedSupplier>> ListAsync(HostScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var requests = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.RentalContext == ServiceRequestRentalContext.ShortRent)
            .InScope(scope)
            .Select(r => new { r.SupplierOrgId, r.Category, r.Status, r.CreatedAt })
            .ToListAsync(cancellationToken);

        if (requests.Count == 0)
            return [];

        var supplierIds = requests.Select(r => r.SupplierOrgId).Distinct().ToList();

        // The name a supplier goes by in the marketplace is its legal name; an org without a profile (it never finished joining)
        // goes by the name of the org. SupplierProfile is keyed by the supplier org and not tenant-filtered.
        var legalNames = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => supplierIds.Contains(sp.OrgId))
            .Select(sp => new { sp.OrgId, sp.LegalName })
            .ToDictionaryAsync(sp => sp.OrgId, sp => sp.LegalName, cancellationToken);
        var orgNames = await db.Orgs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => supplierIds.Contains(o.Id))
            .Select(o => new { o.Id, o.DisplayName, o.Name })
            .ToDictionaryAsync(o => o.Id, o => string.IsNullOrWhiteSpace(o.DisplayName) ? o.Name : o.DisplayName, cancellationToken);

        return requests
            .GroupBy(r => r.SupplierOrgId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(r => r.CreatedAt).First();
                var name = legalNames.TryGetValue(group.Key, out var legal) && !string.IsNullOrWhiteSpace(legal)
                    ? legal
                    : orgNames.GetValueOrDefault(group.Key, string.Empty);

                return new HostUsedSupplier(
                    group.Key,
                    name,
                    group.Count(),
                    group.Count(r => !ServiceRequestStateMachine.IsFinal(r.Status)),
                    latest.CreatedAt,
                    latest.Category);
            })
            .OrderByDescending(supplier => supplier.LastRequestAt)
            .ThenBy(supplier => supplier.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(supplier => supplier.SupplierOrgId)
            .ToList();
    }
}
