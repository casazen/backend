using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class LeaseRegistrationAuthorizationRepository(AppDbContext context)
    : ILeaseRegistrationAuthorizationRepository
{
    public async Task<LeaseRegistrationAuthorization?> GetByLeaseIdAsync(Guid leaseContractId)
        => await context.LeaseRegistrationAuthorizations
            .Where(a => a.LeaseContractId == leaseContractId)
            .OrderByDescending(a => a.AuthorizedAt)
            .FirstOrDefaultAsync();

    public async Task<LeaseRegistrationAuthorization> AddAsync(LeaseRegistrationAuthorization authorization)
    {
        context.LeaseRegistrationAuthorizations.Add(authorization);
        await context.SaveChangesAsync();
        return authorization;
    }
}
