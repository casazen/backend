using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class LeaseEventRepository(AppDbContext context) : ILeaseEventRepository
{
    public async Task<IEnumerable<LeaseEvent>> GetByLeaseIdAsync(Guid leaseContractId)
        => await context.LeaseEvents
            .Where(e => e.LeaseContractId == leaseContractId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

    public async Task<LeaseEvent> AddAsync(LeaseEvent leaseEvent)
    {
        context.LeaseEvents.Add(leaseEvent);
        await context.SaveChangesAsync();
        return leaseEvent;
    }
}
