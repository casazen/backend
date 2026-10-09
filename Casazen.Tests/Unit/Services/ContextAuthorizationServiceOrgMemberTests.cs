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

/// <summary>
/// AM-01 on the context authorization: the <c>account</c> context, the veto of the token for whoever belongs to an org
/// (a former owner of a context keeps nothing of it with an old token), the immediate silence of a deactivated member
/// and the property manager that is no longer mapped to the owner's contexts. The same rule as the open PR #455 ("host
/// context memberships authoritative"), limited to org members.
/// </summary>
public class ContextAuthorizationServiceOrgMemberTests
{
    // ─── The veto ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserContextsAsync_FormerOwnerOfLongRent_WithAnOldToken_DoesNotKeepLongRent()
    {
        // The owner switched to short-term only: its long-rent membership is gone, but its token still says
        // LongTermLandlord. As an org member, the host contexts come from the DB only.
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
        // Nothing changes for whoever has no org member row: the token completes the DB memberships as before.
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
        // The ex-owner who joined another org as a collaborator: the token says PropertyOwner, the DB says staff.
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
        // From the first host row on the DB is the whole truth about the host contexts, whatever the token says.
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
        // admin and supplier are not host contexts: the staff and the supplier link are no org matter.
        await using var db = NewDb();
        var (org, user) = await SeedHostAsync(db, "auth0|dual", UserRole.PropertyOwner);
        AddMembership(db, user.Id, "short-rent", "property_owner");
        AddMember(db, user.Id, org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext(user.Id, ["PropertyOwner", "Admin", "Supplier"]));

        var contexts = await service.GetUserContextsAsync(user.Id);

        Assert.Equal(["admin", "short-rent", "supplier"], contexts.Select(c => c.ContextKey));
    }

    // ─── Deactivated member ─────────────────────────────────────────────────────────────────────────────

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

    // ─── Property manager is not the owner ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserContextsAsync_PropertyManagerWithNoTokenRoleAndNoMembership_IsNotMappedToTheOwnerContexts()
    {
        // The DB role fallback used to turn a PropertyManager into a PropertyOwner when the token carried no role (the
        // Auth0 role sync had failed): the manager became the owner. It gets nothing, like any user with no role.
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

    // ─── The account context ────────────────────────────────────────────────────────────────────────────

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
        // The account is a host context: no onboarding and consents, no account.
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
        // The staff console keeps its key, its role and its permissions: no account context for the staff.
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
        // A token cannot bring the account context: it exists only as the projection of an org member.
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

    // ─── Snapshot ───────────────────────────────────────────────────────────────────────────────────────

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
