using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Infrastructure.Repositories;

public class UserRepository(AppDbContext context) : IUserRepository
{
    public async Task<User?> GetByIdAsync(string id)
    {
        return await context.Users.FindAsync(id);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await context.Users
            .FirstOrDefaultAsync(u => u.Email == email);
    }

    public async Task<IEnumerable<User>> GetAllAsync()
    {
        return await context.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.Email)
            .ToListAsync();
    }

    public async Task<User> AddAsync(User user)
    {
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    public async Task<(User User, bool Created)> AddIfAbsentAsync(User user)
    {
        context.Users.Add(user);
        try
        {
            await context.SaveChangesAsync();
            return (user, true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel first request of the same user inserted the row first (A1-14): keep that row.
            context.Entry(user).State = EntityState.Detached;
            var stored = await context.Users.FirstOrDefaultAsync(u => u.Id == user.Id);
            if (stored is null)
                throw;
            return (stored, false);
        }
    }

    public async Task UpdateAsync(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        // A tracked user saves only the columns that changed. Update() would rewrite every column, and a copy
        // loaded before a parallel request linked the org would put OrgId back to null (A1-14).
        if (context.Entry(user).State == EntityState.Detached)
            context.Users.Update(user);
        await context.SaveChangesAsync();
    }

    public async Task<User?> GetBySubAsync(string sub)
    {
        return await context.Users.FindAsync(sub);
    }

    public async Task<(IEnumerable<User> Users, int TotalCount)> GetPagedAsync(
        string? search, string? role, bool? isActive, int page, int pageSize)
    {
        var query = context.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Email, pattern) ||
                EF.Functions.ILike(u.FirstName, pattern) ||
                EF.Functions.ILike(u.LastName, pattern));
        }

        if (!string.IsNullOrWhiteSpace(role) && Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsedRole))
        {
            query = query.Where(u => u.Role == parsedRole);
        }

        if (isActive.HasValue)
        {
            query = query.Where(u => u.IsActive == isActive.Value);
        }

        var totalCount = await query.CountAsync();

        var users = await query
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (users, totalCount);
    }

    public async Task DeleteAsync(string id)
    {
        var user = await GetByIdAsync(id);
        if (user != null)
        {
            // Soft delete — preserve audit trail
            user.IsActive = false;
            user.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }
}
