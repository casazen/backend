using Casazen.Core.Entities;
using Casazen.Infrastructure.Repositories;
using Casazen.Tests.Integration.Postgres;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A1-26 / A1-17 on a real PostgreSQL database: <see cref="UserRepository.GetPagedAsync"/> orders by most recent
/// first, clamps a page below 1 instead of sending Postgres a negative <c>OFFSET</c> (which it rejects with an
/// error), and <see cref="UserRepository.HasOtherActiveAdminAsync"/> guards the removal of the last active admin.
/// </summary>
public class UserRepositoryPostgresTests
{
    [PostgresFact]
    public async Task GetPagedAsync_MultipleUsers_OrdersNewestFirst()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("userrepo");
        await using (var seed = database.CreateContext())
        {
            seed.Users.AddRange(
                NewUser("older", "older@example.com", DateTime.UtcNow.AddDays(-2)),
                NewUser("newest", "newest@example.com", DateTime.UtcNow),
                NewUser("middle", "middle@example.com", DateTime.UtcNow.AddDays(-1)));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var repository = new UserRepository(db);

        var (users, total) = await repository.GetPagedAsync(null, null, null, page: 1, pageSize: 20);

        Assert.Equal(3, total);
        Assert.Equal(["newest", "middle", "older"], users.Select(u => u.Id));
    }

    [PostgresFact]
    public async Task GetPagedAsync_PageBelowOne_ClampsToFirstPageInsteadOfNegativeOffset()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("userrepo");
        await using (var seed = database.CreateContext())
        {
            seed.Users.Add(NewUser("only-user", "only@example.com", DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var repository = new UserRepository(db);

        // A page of 0 (or negative) used to compute a negative Skip, which Postgres rejects with an error (A1-26).
        var (users, total) = await repository.GetPagedAsync(null, null, null, page: 0, pageSize: 20);

        Assert.Equal(1, total);
        Assert.Single(users);
    }

    [PostgresFact]
    public async Task GetPagedAsync_IsActiveFilter_ReturnsOnlyMatchingUsers()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("userrepo");
        await using (var seed = database.CreateContext())
        {
            seed.Users.AddRange(
                NewUser("active-user", "active@example.com", DateTime.UtcNow, isActive: true),
                NewUser("inactive-user", "inactive@example.com", DateTime.UtcNow, isActive: false));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var repository = new UserRepository(db);

        var (activeOnly, activeTotal) = await repository.GetPagedAsync(null, null, true, 1, 20);
        var (inactiveOnly, inactiveTotal) = await repository.GetPagedAsync(null, null, false, 1, 20);

        Assert.Equal(1, activeTotal);
        Assert.Equal("active-user", activeOnly.Single().Id);
        Assert.Equal(1, inactiveTotal);
        Assert.Equal("inactive-user", inactiveOnly.Single().Id);
    }

    [PostgresFact]
    public async Task HasOtherActiveAdminAsync_OnlyOneActiveAdmin_ReturnsFalseForThatAdmin()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("userrepo");
        await using (var seed = database.CreateContext())
        {
            seed.Users.Add(NewUser("sole-admin", "admin@example.com", DateTime.UtcNow, role: UserRole.Admin));
            seed.Users.Add(NewUser("host", "host@example.com", DateTime.UtcNow, role: UserRole.PropertyOwner));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var repository = new UserRepository(db);

        Assert.False(await repository.HasOtherActiveAdminAsync("sole-admin"));
    }

    [PostgresFact]
    public async Task HasOtherActiveAdminAsync_AnotherActiveAdminExists_ReturnsTrue()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("userrepo");
        await using (var seed = database.CreateContext())
        {
            seed.Users.Add(NewUser("admin-1", "admin1@example.com", DateTime.UtcNow, role: UserRole.Admin));
            seed.Users.Add(NewUser("admin-2", "admin2@example.com", DateTime.UtcNow, role: UserRole.Admin));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var repository = new UserRepository(db);

        Assert.True(await repository.HasOtherActiveAdminAsync("admin-1"));
    }

    [PostgresFact]
    public async Task HasOtherActiveAdminAsync_OtherAdminIsDeactivated_ReturnsFalse()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("userrepo");
        await using (var seed = database.CreateContext())
        {
            seed.Users.Add(NewUser("admin-1", "admin1@example.com", DateTime.UtcNow, role: UserRole.Admin));
            seed.Users.Add(NewUser(
                "admin-2-inactive", "admin2@example.com", DateTime.UtcNow, role: UserRole.Admin, isActive: false));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var repository = new UserRepository(db);

        Assert.False(await repository.HasOtherActiveAdminAsync("admin-1"));
    }

    private static User NewUser(
        string id, string email, DateTime createdAt, bool isActive = true, UserRole role = UserRole.PropertyOwner) => new()
    {
        Id = id,
        Email = email,
        FirstName = "Test",
        LastName = "User",
        Role = role,
        IsActive = isActive,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };
}
