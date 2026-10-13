using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Test data of the org team (AM-01) on the EF InMemory provider: a context with the seeded contexts, roles and
/// permissions (<c>EnsureCreated</c> applies the model seed, so the role ids are the migration's), host orgs, users,
/// org members and the memberships that project them.
/// </summary>
internal static class OrgTeamTestData
{
    /// <summary>
    /// A context on the InMemory database <paramref name="databaseName"/> (a new one when omitted). Opening a second
    /// context with the same name reads what the first one saved and nothing it only tracks.
    /// </summary>
    public static AppDbContext NewDb(string? databaseName = null)
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString()).Options);
        db.Database.EnsureCreated();
        return db;
    }

    public static OrgEntity AddOrg(AppDbContext db, OrgType type = OrgType.Host)
    {
        var org = new OrgEntity
        {
            Name = $"Org {Guid.NewGuid():N}",
            DisplayName = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            OrgType = type,
        };
        db.Orgs.Add(org);
        return org;
    }

    public static User AddUser(AppDbContext db, string id, Guid? orgId, UserRole role = UserRole.None)
    {
        var user = new User
        {
            Id = id,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Test",
            LastName = "User",
            Role = role,
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        return user;
    }

    public static int RoleId(AppDbContext db, string contextKey, string roleKey) =>
        db.Roles.AsNoTracking().Single(r => r.ContextKey == contextKey && r.RoleKey == roleKey).Id;

    public static void AddMembership(AppDbContext db, string userId, string contextKey, string roleKey) =>
        db.UserContextMemberships.Add(new UserContextMembership
        {
            UserId = userId,
            ContextKey = contextKey,
            RoleId = RoleId(db, contextKey, roleKey),
        });

    public static OrgMember AddMember(
        AppDbContext db,
        string userId,
        Guid orgId,
        OrgRole role,
        OrgMemberStatus status = OrgMemberStatus.Active)
    {
        var member = new OrgMember { UserId = userId, OrgId = orgId, Role = role, Status = status };
        db.OrgMembers.Add(member);
        return member;
    }

    /// <summary>The user's memberships as <c>context/roleKey</c>, sorted.</summary>
    public static async Task<List<string>> MembershipsOfAsync(AppDbContext db, string userId) =>
        await db.UserContextMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.ContextKey + "/" + m.Role.RoleKey)
            .OrderBy(m => m)
            .ToListAsync();
}
