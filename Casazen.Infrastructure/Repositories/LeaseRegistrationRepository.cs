using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    public async Task<bool> TryReserveSubmissionAsync(LeaseRegistration registration)
    {
        context.LeaseRegistrations.Add(registration);

        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            context.Entry(registration).State = EntityState.Detached;
            return false;
        }
    }

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

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException?.Message.Contains("23505", StringComparison.Ordinal) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
    }
}
