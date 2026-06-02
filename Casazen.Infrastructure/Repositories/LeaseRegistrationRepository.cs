using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class LeaseRegistrationRepository(AppDbContext context) : ILeaseRegistrationRepository
{
    public async Task<LeaseRegistration?> GetByLeaseIdAsync(Guid leaseContractId)
        => await context.LeaseRegistrations
            .FirstOrDefaultAsync(r => r.LeaseContractId == leaseContractId);

    public async Task<IEnumerable<LeaseRegistration>> GetByStatusAsync(RegistrationStatus status)
        => await context.LeaseRegistrations
            .Include(r => r.LeaseContract)
            .Where(r => r.Status == status)
            .ToListAsync();

    public async Task<LeaseRegistration> AddAsync(LeaseRegistration registration)
    {
        context.LeaseRegistrations.Add(registration);
        await context.SaveChangesAsync();
        return registration;
    }

    public async Task<LeaseRegistration> UpdateAsync(LeaseRegistration registration)
    {
        context.LeaseRegistrations.Update(registration);
        await context.SaveChangesAsync();
        return registration;
    }
}
