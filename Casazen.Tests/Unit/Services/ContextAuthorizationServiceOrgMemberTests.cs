using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Services;

public class ContextAuthorizationServiceOrgMemberTests
{
    [Fact]
    public async Task GetUserContextsAsync_FormerOwnerOfLongRent_WithAnOldToken_DoesNotKeepLongRent()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|former", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "short-rent", "property_owner");
        AddMember(db, user.Id, org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "LongTermLandlord"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        Assert.Equal(["short-rent"], contexts.Select(c => c.ContextKey));
        Assert.False(await service.HasPermissionAsync(user.Id, "long-rent", "lease.read"));
        Assert.False(await service.HasPermissionAsync(user.Id, "long-rent", "property.write"));
        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "property.write"));
    }

    [Fact]
    public async Task GetUserContextsAsync_SameUserThatIsInNoOrgTeam_StillGetsTheTokenContexts()
    {
        await using var db = NewDb();
        var (_, user) = await SeedHostAsync(db, "auth0|legacy", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "short-rent", "property_owner");
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "LongTermLandlord"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        Assert.Equal(["long-rent", "short-rent"], contexts.Select(c => c.ContextKey));
    }

    [Fact]
    public async Task HasPermissionAsync_CollaboratorWithAnOwnerToken_HasOnlyTheCollaboratorPermissions()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|ex-owner", UserRole.None);
        AddMembership(db, user.Id, "short-rent", "staff");
        AddMember(db, user.Id, org.Id, OrgRole.Collaborator);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "LongTermLandlord"]));

        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "booking.read"));
        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "guest.write"));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "booking.write"));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "property.write"));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "payment.read"));
        Assert.False(await service.HasPermissionAsync(user.Id, "long-rent", "lease.create"));
    }

    [Fact]
    public async Task GetUserContextsAsync_OrgMemberWithNoHostMembershipInTheDb_StillGetsTheContextsOfTheToken()
    {
        // The veto starts with the first host membership in the DB. An owner whose host row was never written (an old
        // account, a database without the seeded roles) has no host data to be authoritative about: the token completes
        // the contexts exactly as before AM-01, so nobody is locked out of what they already had.
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|bare", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "account", "org_owner");
        AddMember(db, user.Id, org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner"]));

        Assert.Equal(["account", "short-rent"], (await service.GetUserContextsAsync(user.Id)).Select(c => c.ContextKey));
        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "property.write"));
    }

    [Fact]
    public async Task GetUserContextsAsync_OrgMemberWithOneHostMembership_TheTokenAddsNoOtherHostContext()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|one-row", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "account", "org_owner");
        AddMembership(db, user.Id, "long-rent", "long_term_landlord");
        AddMember(db, user.Id, org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "LongTermLandlord"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        Assert.Equal(["account", "long-rent"], contexts.Select(c => c.ContextKey));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "property.read"));
    }

    [Fact]
    public async Task GetUserContextsAsync_Veto_DoesNotRemoveStaffOrSupplierContextsOfAMember()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|dual", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "short-rent", "property_owner");
        AddMember(db, user.Id, org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "Admin", "Supplier"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        Assert.Equal(["admin", "short-rent", "supplier"], contexts.Select(c => c.ContextKey));
    }

    [Fact]
    public async Task GetUserContextsAsync_DeactivatedMember_ReachesNothing()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|off", UserRole.None);
        AddMembership(db, user.Id, "account", "org_admin");
        AddMembership(db, user.Id, "short-rent", "property_manager");
        AddMember(db, user.Id, org.Id, OrgRole.Admin, OrgMemberStatus.Deactivated);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["Supplier"]));

        Assert.Empty(await service.GetUserContextsAsync(user.Id));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "property.read"));
        Assert.False(await service.HasPermissionAsync(user.Id, "account", "org.billing.manage"));
    }

    [Fact]
    public async Task GetUserContextsAsync_ReactivatedMember_GetsItsContextsBack()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|back", UserRole.None);
        AddMembership(db, user.Id, "short-rent", "property_manager");
        var member = AddMember(db, user.Id, org.Id, OrgRole.PropertyManager, OrgMemberStatus.Deactivated);
        await db.SaveChangesAsync();
        Assert.Empty(await CreateService(db, BuildHttpContext(user.Id, [])).GetUserContextsAsync(user.Id));

        member.Status = OrgMemberStatus.Active;
        await db.SaveChangesAsync();

        var contexts = await CreateService(db, BuildHttpContext(user.Id, [])).GetUserContextsAsync(user.Id);
        Assert.Equal(["short-rent"], contexts.Select(c => c.ContextKey));
    }

    [Fact]
    public async Task GetUserContextsAsync_PropertyManagerWithNoTokenRoleAndNoMembership_IsNotMappedToTheOwnerContexts()
    {
        await using var db = NewDb();
        var (_, user) = await SeedHostAsync(db, "auth0|manager", UserRole.PropertyManager);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, []));

        Assert.Empty(await service.GetUserContextsAsync(user.Id));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "property.write"));
    }

    [Fact]
    public async Task HasPermissionAsync_PropertyManagerMembership_HasTheOperationalPermissionsButNoBillingNorTeam()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|pm", UserRole.PropertyManager);
        AddMembership(db, user.Id, "short-rent", "property_manager");
        AddMembership(db, user.Id, "long-rent", "property_manager");
        AddMember(db, user.Id, org.Id, OrgRole.PropertyManager);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyManager"]));

        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "booking.write"));
        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "payment.write"));
        Assert.True(await service.HasPermissionAsync(user.Id, "long-rent", "lease.create"));
        Assert.True(await service.HasPermissionAsync(user.Id, "short-rent", "org.suppliers.manage"));
        Assert.False(await service.HasPermissionAsync(user.Id, "account", "org.billing.manage"));
        Assert.False(await service.HasPermissionAsync(user.Id, "account", "org.members.manage"));
        Assert.False(await service.HasPermissionAsync(user.Id, "short-rent", "org.billing.manage"));
    }

    [Fact]
    public async Task GetUserContextsAsync_OwnerWithTheAccountMembership_ListsAccountWithAllOrgPermissions()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|owner", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "account", "org_owner");
        AddMembership(db, user.Id, "short-rent", "property_owner");
        AddMember(db, user.Id, org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        var account = Assert.Single(contexts, c => c.ContextKey == "account");
        Assert.Equal("Amministrazione", account.DisplayName);
        Assert.Equal("org_owner", account.RoleKey);
        Assert.Equal("/app/account", account.DefaultRoute);
        Assert.Equal(
            ["org.activity.read", "org.billing.manage", "org.billing.read", "org.members.manage", "org.settings.manage", "org.suppliers.manage"],
            account.Permissions.Order());
        Assert.True(await service.HasPermissionAsync(user.Id, "account", "org.billing.manage"));
    }

    [Theory]
    [InlineData("org_admin", true, true)]
    [InlineData("org_accountant", false, true)]
    public async Task HasPermissionAsync_AccountRoles_HoldExactlyTheirPermissions(string roleKey, bool manage, bool read)
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|acct", UserRole.None);
        AddMembership(db, user.Id, "account", roleKey);
        AddMember(db, user.Id, org.Id, roleKey == "org_admin" ? OrgRole.Admin : OrgRole.Accountant);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, []));

        Assert.Equal(manage, await service.HasPermissionAsync(user.Id, "account", "org.billing.manage"));
        Assert.Equal(manage, await service.HasPermissionAsync(user.Id, "account", "org.members.manage"));
        Assert.Equal(read, await service.HasPermissionAsync(user.Id, "account", "org.billing.read"));
    }

    [Fact]
    public async Task GetUserContextsAsync_AccountContext_WaitsForTheOnboardingLikeTheOtherHostContexts()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        var user = AddUser(db, "auth0|early", org.Id, UserRole.None);
        user.OnboardingCompletedAt = null;
        AddMembership(db, user.Id, "account", "org_admin");
        AddMember(db, user.Id, org.Id, OrgRole.Admin);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, []));

        Assert.Empty(await service.GetUserContextsAsync(user.Id));
        Assert.False(await service.HasPermissionAsync(user.Id, "account", "org.billing.manage"));
    }

    [Fact]
    public async Task GetUserContextsAsync_PlatformAdmin_IsUnchanged()
    {
        await using var db = NewDb();
        var (_, user) = await SeedHostAsync(db, "auth0|staff", UserRole.Admin);
        AddMembership(db, user.Id, "admin", "platform_admin");
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["Admin"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        var admin = Assert.Single(contexts);
        Assert.Equal("admin", admin.ContextKey);
        Assert.Equal("platform_admin", admin.RoleKey);
        Assert.Equal("/app/admin", admin.DefaultRoute);
        Assert.Contains("admin.users.manage", admin.Permissions);
    }

    [Fact]
    public async Task GetUserContextsAsync_NoTokenRoleEverGrantsTheAccountContext()
    {
        await using var db = NewDb();
        var (_, user) = await SeedHostAsync(db, "auth0|token-only", UserRole.PropertyOwner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "LongTermLandlord", "Admin", "PropertyManager", "Staff", "account", "org_owner"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        Assert.DoesNotContain(contexts, c => c.ContextKey == "account");
    }

    [Theory]
    [InlineData("account", "/app/account")]
    [InlineData("short-rent", "/app/short-rent")]
    [InlineData("long-rent", "/app/long-rent/leases")]
    [InlineData("admin", "/app/admin")]
    [InlineData("supplier", "/supplier/inbox")]
    [InlineData("unknown", "/app/choose-context")]
    public void GetDefaultRoute_EveryContext_HasItsHome(string contextKey, string expected) =>
        Assert.Equal(expected, ContextAuthorizationService.GetDefaultRoute(contextKey));

    [Fact]
    public async Task Snapshot_LoadsTheOrgMemberOfTheUser_AndNoneForAUserOutsideAnOrgTeam()
    {
        await using var db = NewDb();
        var (org, member) = await SeedHostAsync(db, "auth0|snap-member", UserRole.None);
        AddMember(db, member.Id, org.Id, OrgRole.Accountant);
        var (_, outsider) = await SeedHostAsync(db, "auth0|snap-outsider", UserRole.PropertyOwner);
        await db.SaveChangesAsync();
        var store = NewStore(db, BuildHttpContext("auth0|snap-member", []));

        var snapshot = await store.GetAsync("auth0|snap-member");
        var outsiderSnapshot = await store.GetAsync(outsider.Id);

        Assert.Equal(new OrgMemberSnapshot(org.Id, OrgRole.Accountant, OrgMemberStatus.Active), snapshot.OrgMember);
        Assert.False(snapshot.IsOrgMemberDeactivated);
        Assert.Null(outsiderSnapshot.OrgMember);
    }

    [Fact]
    public async Task Snapshot_DeactivatedMember_HasNoMembershipsAtAll()
    {
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|snap-off", UserRole.None);
        AddMembership(db, user.Id, "short-rent", "staff");
        AddMember(db, user.Id, org.Id, OrgRole.Collaborator, OrgMemberStatus.Deactivated);
        await db.SaveChangesAsync();

        var snapshot = await NewStore(db, BuildHttpContext(user.Id, [])).GetAsync(user.Id);

        Assert.True(snapshot.IsOrgMemberDeactivated);
        Assert.Empty(snapshot.Memberships);
    }

    private static async Task<(OrgEntity Org, User User)> SeedHostAsync(AppDbContext db, string userId, UserRole role)
    {
        var org = AddOrg(db);
        var user = AddUser(db, userId, org.Id, role);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, org.Id, NewLegalDocuments());
        await db.SaveChangesAsync();
        return (org, user);
    }

    private static LegalDocumentService NewLegalDocuments() =>
        new(new ConfigurationBuilder().Build(), NullLogger<LegalDocumentService>.Instance);

    private static UserAuthorizationSnapshotStore NewStore(AppDbContext db, HttpContext httpContext) =>
        new(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            new HttpContextAccessor { HttpContext = httpContext },
            new ConfigurationBuilder().Build());

    private static ContextAuthorizationService CreateService(AppDbContext db, HttpContext httpContext)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var configuration = new ConfigurationBuilder().Build();
        return new ContextAuthorizationService(
            new UserAuthorizationSnapshotStore(db, new MemoryCache(new MemoryCacheOptions()), accessor, configuration),
            new LegalDocumentService(configuration, NullLogger<LegalDocumentService>.Instance),
            accessor,
            NullLogger<ContextAuthorizationService>.Instance);
    }

    private static HttpContext BuildHttpContext(string userId, string[] roles)
    {
        var claims = new List<Claim> { new("sub", userId) };
        claims.AddRange(roles.Select(r => new Claim("https://casazen.app/roles", r)));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) };
    }
}
