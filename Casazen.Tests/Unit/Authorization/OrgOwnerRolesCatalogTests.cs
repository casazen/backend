using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-01 tripwire: a role that administers the org must be one of <see cref="OrgOwnerRoles"/>, or the billing policy and
/// the onboarding guard would treat a billing administrator like any other member. The seeded roles (the model's
/// <c>HasData</c>, i.e. the migration) are read, not a list copied here: a role added to the seed with
/// <c>org.billing.manage</c> and forgotten by <see cref="OrgOwnerRoles"/> fails this test.
/// </summary>
public class OrgOwnerRolesCatalogTests
{
    private sealed record SeedRole(int Id, string ContextKey, string RoleKey, IReadOnlySet<string> Permissions);

    private static IReadOnlyList<SeedRole> SeededRoles()
    {
        // The Npgsql model is built without a connection (as the architecture tests do); the design-time model carries
        // the HasData rows.
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres")
                .Options);
        var model = db.GetService<IDesignTimeModel>().Model;

        var roleRows = model.FindEntityType(typeof(Role))!.GetSeedData().ToList();
        var permissionRows = model.FindEntityType(typeof(RolePermission))!.GetSeedData().ToList();

        return roleRows
            .Select(row => new SeedRole(
                (int)row[nameof(Role.Id)]!,
                (string)row[nameof(Role.ContextKey)]!,
                (string)row[nameof(Role.RoleKey)]!,
                permissionRows
                    .Where(p => (int)p[nameof(RolePermission.RoleId)]! == (int)row[nameof(Role.Id)]!)
                    .Select(p => (string)p[nameof(RolePermission.PermissionKey)]!)
                    .ToHashSet(StringComparer.Ordinal)))
            .ToList();
    }

    [Fact]
    public void SeededRoles_EveryRoleThatManagesBilling_IsAnOwnerRole()
    {
        var forgotten = SeededRoles()
            .Where(r => r.Permissions.Contains(AccountContext.Permissions.BillingManage))
            .Where(r => !OrgOwnerRoles.IsOwnerRole(r.ContextKey, r.RoleKey))
            .Select(r => $"{r.ContextKey}/{r.RoleKey}")
            .ToList();

        Assert.True(
            forgotten.Count == 0,
            "Seeded roles holding org.billing.manage that OrgOwnerRoles does not list: " + string.Join(", ", forgotten)
            + ". Add them to OrgOwnerRoles (the billing policy and the onboarding guard read it).");
    }

    [Fact]
    public void SeededRoles_EveryRoleThatManagesTheTeam_IsAnOwnerRole()
    {
        var forgotten = SeededRoles()
            .Where(r => r.Permissions.Contains(AccountContext.Permissions.MembersManage))
            .Where(r => !OrgOwnerRoles.IsOwnerRole(r.ContextKey, r.RoleKey))
            .Select(r => $"{r.ContextKey}/{r.RoleKey}")
            .ToList();

        Assert.True(forgotten.Count == 0, "Seeded roles holding org.members.manage that OrgOwnerRoles does not list: " + string.Join(", ", forgotten));
    }

    [Fact]
    public void OwnerRoles_EveryEntry_IsASeededRole()
    {
        var seeded = SeededRoles().Select(r => (r.ContextKey, r.RoleKey)).ToHashSet();

        var stale = OrgOwnerRoles.All.Where(role => !seeded.Contains(role)).Select(r => $"{r.ContextKey}/{r.RoleKey}").ToList();

        Assert.True(stale.Count == 0, "OrgOwnerRoles lists roles that are not seeded: " + string.Join(", ", stale));
    }

    [Fact]
    public void OwnerRoles_AreExactlyTheOwnersTheAdministratorAndThePlatformAdmin()
    {
        var expected = new[]
        {
            ("short-rent", "property_owner"),
            ("long-rent", "long_term_landlord"),
            ("admin", "platform_admin"),
            ("account", "org_owner"),
            ("account", "org_admin"),
        };

        Assert.Equal(expected.Order(), OrgOwnerRoles.All.Order());
    }

    [Fact]
    public void OwnerRoles_AgreeWithTheContextBootstrapOwners()
    {
        // The fallback derived from the token (ContextAccessBootstrap) grants the owner roles of the three contexts: they
        // are the owner roles of OrgOwnerRoles, so the two never describe different owners.
        foreach (var (jwtRole, contextKey) in new[] { ("PropertyOwner", "short-rent"), ("LongTermLandlord", "long-rent"), ("Admin", "admin") })
        {
            var fallback = Assert.Single(ContextAccessBootstrap.BuildFallbackAccess([jwtRole]));

            Assert.Equal(contextKey, fallback.ContextKey);
            Assert.True(OrgOwnerRoles.IsOwnerRole(fallback.ContextKey, fallback.RoleKey), $"{fallback.ContextKey}/{fallback.RoleKey}");
        }
    }

    [Fact]
    public void OrgRoles_OnlyOwnerAndAdminMapToAnOwnerRole()
    {
        // The org role -> role key mapping and OrgOwnerRoles say the same thing about who administers the org.
        foreach (var role in Enum.GetValues<OrgRole>())
        {
            var administers = OrgRoleCatalog.ProjectionOf(role, ["short-rent", "long-rent"])
                .Any(p => OrgOwnerRoles.IsOwnerRole(p.ContextKey, p.RoleKey));

            Assert.Equal(role is OrgRole.Owner or OrgRole.Admin, administers);
        }
    }

    [Fact]
    public void SeededRoles_ModelSeedOfTheNewRoles_IsTheCatalog()
    {
        var seed = SeededRoles().ToDictionary(r => r.Id);

        foreach (var expected in OrgRoleCatalog.SeededRoles)
        {
            Assert.True(seed.TryGetValue(expected.Id, out var actual), $"Role {expected.Id} is not seeded");
            Assert.Equal(expected.ContextKey, actual!.ContextKey);
            Assert.Equal(expected.RoleKey, actual.RoleKey);
            Assert.Equal(expected.Permissions.Order(), actual.Permissions.Order());
        }
    }

    [Fact]
    public void SeededRoles_AccountContextIsSeeded_AndEveryRoleBelongsToASeededContext()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres")
                .Options);
        var contexts = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Casazen.Core.Entities.AppContext))!
            .GetSeedData()
            .Select(row => (string)row[nameof(Casazen.Core.Entities.AppContext.Key)]!)
            .ToHashSet();

        Assert.Contains(AccountContext.Key, contexts);
        Assert.All(SeededRoles(), role => Assert.Contains(role.ContextKey, contexts));
    }
}
