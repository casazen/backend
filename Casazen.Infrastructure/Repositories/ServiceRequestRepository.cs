using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class ServiceRequestRepository(AppDbContext db) : IServiceRequestRepository
{
    private static readonly ServiceRequestStatus[] OpenStatuses =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    public Task<ServiceRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        Guid orgId,
        ServiceRequestStatus? status,
        Guid? propertyId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .Where(r => r.OrgId == orgId);

        if (status is not null)
            query = query.Where(r => r.Status == status.Value);

        if (propertyId is not null)
            query = query.Where(r => r.PropertyId == propertyId.Value);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForSupplierAsync(
        Guid supplierOrgId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .Where(r => r.SupplierOrgId == supplierOrgId);

        if (openOnly)
            query = query.Where(r => OpenStatuses.Contains(r.Status));

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task AddAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
