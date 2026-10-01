using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Validation;
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

        // Enum.TryParse alone also accepts a numeric string with no declared member (e.g. "99"): filtering by it
        // would silently match nothing instead of leaving the filter unapplied like any other unknown value (PL-07).
        if (EnumNames.TryParseDefined<UserRole>(role, out var parsedRole))
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

    public async Task<(UserActivationOutcome Outcome, User? User)> SetActiveAsync(
        string id,
        bool isActive,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        // Deactivations are serialized (one key): the count of the other active admins below always sees the
        // deactivation committed by the previous holder (READ COMMITTED), so two admins deactivating each other at the
        // same time cannot leave the platform without an active admin.
        await using var transaction = isActive
            ? null
            : await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                context,
                cancellationToken,
                (PostgresAdvisoryLocks.Scope.UserDeactivation, "users"));

        var user = await context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return (UserActivationOutcome.NotFound, null);

        if (user.IsActive == isActive)
            return (UserActivationOutcome.Unchanged, user);

        if (!isActive)
        {
            // Read under the lock: an admin deactivated by a parallel request no longer acts as one.
            var actorActive = await context.Users
                .AsNoTracking()
                .Where(u => u.Id == actorId)
                .Select(u => (bool?)u.IsActive)
                .FirstOrDefaultAsync(cancellationToken);
            if (actorActive == false)
                return (UserActivationOutcome.ActorInactive, user);

            if (user.Role == UserRole.Admin &&
                !await context.Users.AnyAsync(
                    u => u.Id != id && u.IsActive && u.Role == UserRole.Admin,
                    cancellationToken))
            {
                return (UserActivationOutcome.LastActiveAdmin, user);
            }
        }

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return (UserActivationOutcome.Updated, user);
    }
}
