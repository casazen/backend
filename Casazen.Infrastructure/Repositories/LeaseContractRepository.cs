using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class LeaseContractRepository(AppDbContext context) : ILeaseContractRepository
{
    public async Task<LeaseContract?> GetByIdAsync(Guid id)
        => await context.LeaseContracts.FindAsync(id);

    public async Task<LeaseContract?> GetByIdWithDetailsAsync(Guid id)
        => await context.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Registration)
            .Include(l => l.Events.OrderBy(e => e.OccurredAt))
            .FirstOrDefaultAsync(l => l.Id == id);

    public async Task<IEnumerable<LeaseContract>> GetByOwnerAsync(string ownerId)
        => await context.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Where(l => l.Property.OwnerId == ownerId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<LeaseContract>> GetByPropertyAsync(Guid propertyId)
        => await context.LeaseContracts
            .Include(l => l.Parties)
            .Where(l => l.PropertyId == propertyId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<LeaseContract>> GetByStatusAsync(LeaseStatus status)
        => await context.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Registration)
            .Where(l => l.Status == status)
            .ToListAsync();

    public async Task<LeaseContract> AddAsync(LeaseContract lease)
    {
        context.LeaseContracts.Add(lease);
        await context.SaveChangesAsync();
        return lease;
    }

    public async Task<LeaseContract> UpdateAsync(LeaseContract lease)
    {
        lease.UpdatedAt = DateTime.UtcNow;
        context.LeaseContracts.Update(lease);
        await context.SaveChangesAsync();
        return lease;
    }
}
