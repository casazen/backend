using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>The web host with the org team flag on (<c>Features:OrgTeam</c>), as it runs once the account screens exist (AM-04).</summary>
public sealed class OrgTeamEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:OrgTeam"] = "true" }));
    }
}

/// <summary>Data and calls shared by the org team integration tests.</summary>
internal static class OrgTeamHttp
{
    private const string ConsentVersion = "2026-06-v1";

    /// <summary>A person onboarded for <paramref name="orgId"/> with an org member row written directly (no roles needed).</summary>
    public static async Task<string> SeedMemberRowAsync(
        CasazenWebApplicationFactory factory, Guid orgId, OrgRole role, OrgMemberStatus status = OrgMemberStatus.Active)
    {
        var userId = $"auth0|am01-http-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Membro",
            LastName = "Org",
            OrgId = orgId,
            Role = UserRole.None,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        db.OrgMembers.Add(new OrgMember { OrgId = orgId, UserId = userId, Role = role, Status = status });
        await db.SaveChangesAsync();
        return userId;
    }

    /// <summary>A person added through the real service: the member row and its memberships (needs the seeded roles: PostgreSQL).</summary>
    public static async Task<string> AddMemberAsync(
        CasazenWebApplicationFactory factory, Guid orgId, OrgRole role, string[] areas, bool deactivate = false)
    {
        var userId = $"auth0|am01-http-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Membro",
            LastName = "Org",
            Role = UserRole.None,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IOrgMembershipService>();
        await service.AddMemberAsync(userId, orgId, role, areas, createdByUserId: null);
        if (deactivate)
            await service.DeactivateAsync(userId);
        return userId;
    }

    /// <summary>An owner as the onboarding leaves it: its org, the rental membership of its role and the org owner rows.</summary>
    public static async Task<(string UserId, Guid OrgId)> SeedOwnerWithTeamRowsAsync(CasazenWebApplicationFactory factory)
    {
        var ownerId = $"auth0|am01-owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserContextMembershipService>().GrantAsync(ownerId, [UserRole.PropertyOwner]);
        await scope.ServiceProvider.GetRequiredService<IOrgMembershipService>().EnsureOwnerAsync(ownerId, org.Id);
        return (ownerId, org.Id);
    }

    public static async Task SetMemberStatusInDatabaseAsync(CasazenWebApplicationFactory factory, string userId, OrgMemberStatus status)
    {
        // A direct write, as another API instance would make: nothing invalidates this instance's authorization cache.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == userId);
        member.Status = status;
        await db.SaveChangesAsync();
    }

    public static async Task<List<string>> ContextKeysAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/contexts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("contexts").EnumerateArray().Select(c => c.GetProperty("contextKey").GetString()!).ToList();
    }

    public static Task<HttpResponseMessage> PutOnboardingAsync(HttpClient client, string rentalType) =>
        client.PutAsJsonAsync("/api/users/onboarding", new
        {
            rentalType,
            consents = new
            {
                tosAccepted = true,
                tosVersion = ConsentVersion,
                privacyAccepted = true,
                privacyVersion = ConsentVersion,
                dpaAccepted = true,
                dpaVersion = ConsentVersion,
                subprocessorsAcknowledged = true,
                subprocessorsVersion = ConsentVersion,
            },
        });

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }
}

/// <summary>
/// AM-01 over the real pipeline: a member the org deactivated is refused with 403 <c>member_inactive</c> on every
/// authenticated endpoint from the very next request, whatever the authorization cache holds (the request tenant reads the
/// status from the database each time); the owner and the other members are not touched; <c>account_inactive</c> wins.
/// Written to run on the InMemory fallback too (no seeded roles needed).
/// </summary>
public class OrgMemberInactiveIntegrationTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    [Theory]
    [InlineData("GET", "/api/users/me")]
    [InlineData("GET", "/api/me/contexts")]
    [InlineData("GET", "/api/properties")]
    [InlineData("POST", "/api/properties")]
    [InlineData("GET", "/api/orgs/me/entitlement")]
    [InlineData("GET", "/api/bookings")]
    [InlineData("GET", "/api/supplier/profile")]
    public async Task Request_DeactivatedOrgMember_Returns403MemberInactive(string method, string path)
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am01-owner-{Guid.NewGuid():N}");
        var member = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Collaborator, OrgMemberStatus.Deactivated);
        using var client = factory.CreateAuthenticatedClient(member, roles: "PropertyOwner,Supplier");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { }),
        });

        await OrgTeamHttp.AssertProblemAsync(response, HttpStatusCode.Forbidden, "member_inactive");
    }

    [Fact]
    public async Task Request_DeactivatedOrgMember_GetsTheLocalizedMessage()
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am01-owner-{Guid.NewGuid():N}");
        var member = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Admin, OrgMemberStatus.Deactivated);
        using var italian = factory.CreateAuthenticatedClient(member);
        using var english = factory.CreateAuthenticatedClient(member);
        english.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var it = await (await italian.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();
        var en = await (await english.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.StartsWith("Il tuo accesso all'organizzazione è stato disattivato", it.GetProperty("detail").GetString());
        Assert.StartsWith("Your access to the organization has been deactivated", en.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Request_ActiveOrgMember_IsNotRefused()
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am01-owner-{Guid.NewGuid():N}");
        var member = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Collaborator);
        using var client = factory.CreateAuthenticatedClient(member);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task Deactivation_IsSeenByTheVeryNextRequest_WithoutAnyCacheInvalidation()
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am01-owner-{Guid.NewGuid():N}");
        var member = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Collaborator);
        using var client = factory.CreateAuthenticatedClient(member);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me/contexts")).StatusCode);

        await OrgTeamHttp.SetMemberStatusInDatabaseAsync(factory, member, OrgMemberStatus.Deactivated);

        await OrgTeamHttp.AssertProblemAsync(await client.GetAsync("/api/users/me"), HttpStatusCode.Forbidden, "member_inactive");
        await OrgTeamHttp.AssertProblemAsync(await client.GetAsync("/api/me/contexts"), HttpStatusCode.Forbidden, "member_inactive");

        await OrgTeamHttp.SetMemberStatusInDatabaseAsync(factory, member, OrgMemberStatus.Active);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task Deactivation_DoesNotTouchTheOwnerNorTheOtherMembersOfTheOrg()
    {
        var ownerId = $"auth0|am01-owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        var deactivated = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Collaborator, OrgMemberStatus.Deactivated);
        var colleague = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Collaborator);
        using var owner = factory.CreateAuthenticatedClient(ownerId);
        using var other = factory.CreateAuthenticatedClient(colleague);
        using var off = factory.CreateAuthenticatedClient(deactivated);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/users/me")).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(await off.GetAsync("/api/users/me"), HttpStatusCode.Forbidden, "member_inactive");
    }

    [Fact]
    public async Task Request_DeactivatedAccountAndDeactivatedMember_AnswersAccountInactive()
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am01-owner-{Guid.NewGuid():N}");
        var member = await OrgTeamHttp.SeedMemberRowAsync(factory, org.Id, OrgRole.Collaborator, OrgMemberStatus.Deactivated);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Users.SingleAsync(u => u.Id == member)).IsActive = false;
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(member);

        await OrgTeamHttp.AssertProblemAsync(await client.GetAsync("/api/users/me"), HttpStatusCode.Forbidden, "account_inactive");
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsOrgTeamOff()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("orgTeam").GetBoolean());
    }
}

/// <summary>
/// AM-01 over the real pipeline on PostgreSQL (the roles and contexts it needs come from the migration seed): the
/// onboarding makes its caller the owner of its org, <c>GET /api/me/contexts</c> keeps its answer while the flag is off,
/// the org billing policy follows the account roles, the token no longer completes the host contexts of an org member,
/// and the reconcile command is the platform admin's.
/// </summary>
public class OrgTeamPostgresIntegrationTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    [PostgresFact]
    public async Task Onboarding_NewOwner_GetsItsOrgMemberAndAccountMembership_AndItsContextsStayTheSame()
    {
        var userId = $"auth0|am01-new-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner");

        var onboarding = await OrgTeamHttp.PutOnboardingAsync(client, "ShortTerm");

        Assert.Equal(HttpStatusCode.OK, onboarding.StatusCode);
        var orgId = (await onboarding.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var member = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.UserId == userId);
            Assert.Equal((orgId, OrgRole.Owner, OrgMemberStatus.Active, PropertyScope.All), (member.OrgId, member.Role, member.Status, member.PropertyScope));
            var memberships = await db.UserContextMemberships.AsNoTracking().Where(m => m.UserId == userId)
                .Select(m => m.ContextKey + "/" + m.Role.RoleKey).ToListAsync();
            Assert.Equal(["account/org_owner", "short-rent/property_owner"], memberships.Order());
        }

        // The flag is off: the clients of today see exactly the contexts they saw before AM-01.
        Assert.Equal(["short-rent"], await OrgTeamHttp.ContextKeysAsync(client));

        // Repeating the onboarding (or its retry) changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await OrgTeamHttp.PutOnboardingAsync(client, "ShortTerm")).StatusCode);
        using var verify = factory.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await check.OrgMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == userId));
        Assert.Equal(1, await check.UserContextMemberships.CountAsync(m => m.UserId == userId && m.ContextKey == "account"));
    }

    [PostgresFact]
    public async Task OrgBillingAdmin_FollowsTheAccountRoles()
    {
        var (_, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var admin = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Admin, ["short-rent"]);
        var accountant = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Accountant, ["short-rent"]);
        var manager = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.PropertyManager, ["short-rent"]);
        var collaborator = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);

        // No role in the token: what the DB says is all there is.
        using var asAdmin = factory.CreateAuthenticatedClient(admin);
        using var asAccountant = factory.CreateAuthenticatedClient(accountant);
        using var asManager = factory.CreateAuthenticatedClient(manager);
        using var asCollaborator = factory.CreateAuthenticatedClient(collaborator);

        Assert.Equal(HttpStatusCode.OK, (await asAdmin.GetAsync("/api/orgs/me/entitlement")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asAccountant.GetAsync("/api/orgs/me/entitlement")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asManager.GetAsync("/api/orgs/me/entitlement")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asCollaborator.GetAsync("/api/orgs/me/entitlement")).StatusCode);
    }

    [PostgresFact]
    public async Task Collaborator_ReadsBookingsButCannotWriteThem()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var property = await factory.SeedPropertyAsync(ownerId);
        var collaborator = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        using var asCollaborator = factory.CreateAuthenticatedClient(collaborator);

        // A collaborator reads the booking side (booking.read) and cannot write prices or bookings: the permissions of its
        // staff role, enforced by the same context policies as before.
        Assert.Equal(HttpStatusCode.OK, (await asCollaborator.GetAsync("/api/bookings")).StatusCode);
        var write = await asCollaborator.PostAsJsonAsync("/api/bookings", new { propertyId = property.Id });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [PostgresFact]
    public async Task DeactivatedAdministrator_IsRefusedWithMemberInactiveEvenOnTheBillingEndpoints()
    {
        var (_, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var admin = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Admin, ["short-rent"]);
        using var client = factory.CreateAuthenticatedClient(admin, roles: "PropertyOwner");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orgs/me/entitlement")).StatusCode);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IOrgMembershipService>().DeactivateAsync(admin);

        await OrgTeamHttp.AssertProblemAsync(await client.GetAsync("/api/orgs/me/entitlement"), HttpStatusCode.Forbidden, "member_inactive");
        await OrgTeamHttp.AssertProblemAsync(await client.GetAsync("/api/properties"), HttpStatusCode.Forbidden, "member_inactive");
    }

    [PostgresFact]
    public async Task Veto_FormerOwnerOfLongRent_WithAnOldToken_DoesNotKeepLongRent_ButANonMemberStillDoes()
    {
        // Owner of the org, onboarded for short-term only: its token still says LongTermLandlord (the Auth0 role removal
        // failed or the token is old). As an org member its host contexts come from the DB only.
        var (member, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var asMember = factory.CreateAuthenticatedClient(member, roles: "PropertyOwner,LongTermLandlord");

        // The same token for a user that is in no org team (as every owner of before the backfill): unchanged, the token
        // completes its contexts.
        var legacyId = $"auth0|am01-legacy-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(legacyId);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserContextMembershipService>().GrantAsync(legacyId, [UserRole.PropertyOwner]);
        }

        using var asLegacy = factory.CreateAuthenticatedClient(legacyId, roles: "PropertyOwner,LongTermLandlord");

        Assert.Equal(HttpStatusCode.Forbidden, (await asMember.GetAsync("/api/leases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await asMember.GetAsync("/api/properties")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await asLegacy.GetAsync("/api/leases")).StatusCode);
        Assert.Equal(["short-rent"], await OrgTeamHttp.ContextKeysAsync(asMember));
        Assert.Equal(["long-rent", "short-rent"], await OrgTeamHttp.ContextKeysAsync(asLegacy));
    }

    [PostgresFact]
    public async Task ReconcileEndpoint_AsPlatformAdmin_IsADryRunByDefaultThenApplies()
    {
        var ownerId = $"auth0|am01-reconcile-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(ownerId);
        using var admin = factory.CreateAuthenticatedClient($"auth0|am01-staff-{Guid.NewGuid():N}", roles: "Admin");

        var dry = await (await admin.PostAsync("/api/admin/org-members/reconcile", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Contains(
            dry.GetProperty("fixes").EnumerateArray(),
            f => f.GetProperty("code").GetString() == "owner_member_created" && f.GetProperty("userId").GetString() == ownerId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.OrgMembers.IgnoreQueryFilters().AnyAsync(m => m.UserId == ownerId));
        }

        var applied = await (await admin.PostAsync("/api/admin/org-members/reconcile?dryRun=false", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(applied.GetProperty("dryRun").GetBoolean());
        using var verify = factory.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await check.OrgMembers.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.UserId == ownerId);
        Assert.Equal(OrgRole.Owner, member.Role);
        Assert.True(await check.UserContextMemberships.AnyAsync(m => m.UserId == ownerId && m.ContextKey == "account"));
    }

    [PostgresFact]
    public async Task ReconcileEndpoint_AsAnyoneButAPlatformAdmin_Returns403()
    {
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/admin/org-members/reconcile?dryRun=false", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/admin/org-members/reconcile", null)).StatusCode);
    }
}

/// <summary>AM-01 with the org team flag on: the account context is listed to its owners and administrators, never to the staff.</summary>
public class OrgTeamFlagOnIntegrationTests(OrgTeamEnabledFactory factory) : IClassFixture<OrgTeamEnabledFactory>
{
    [Fact]
    public async Task PublicFeatures_FlagOn_ReturnsOrgTeamOn()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("orgTeam").GetBoolean());
    }

    [PostgresFact]
    public async Task MeContexts_FlagOn_ListsTheAccountContextOfTheOwnerAndTheAdministrator()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var adminId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Admin, ["short-rent"]);
        using var asOwner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        using var asAdmin = factory.CreateAuthenticatedClient(adminId);

        var ownerContexts = await (await asOwner.GetAsync("/api/me/contexts")).Content.ReadFromJsonAsync<JsonElement>();
        var adminKeys = await OrgTeamHttp.ContextKeysAsync(asAdmin);

        var account = ownerContexts.GetProperty("contexts").EnumerateArray()
            .Single(c => c.GetProperty("contextKey").GetString() == "account");
        Assert.Equal("Amministrazione", account.GetProperty("displayName").GetString());
        Assert.Equal("org_owner", account.GetProperty("roleKey").GetString());
        Assert.Equal("/app/account", account.GetProperty("defaultRoute").GetString());
        Assert.Equal(
            ["org.activity.read", "org.billing.manage", "org.billing.read", "org.members.manage", "org.settings.manage", "org.suppliers.manage"],
            account.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order());
        Assert.Equal(["account", "short-rent"], adminKeys);
    }

    [PostgresFact]
    public async Task MeContexts_FlagOn_StaffContextsAreUnchanged()
    {
        var staffId = $"auth0|am01-staff-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = staffId, Email = $"{Guid.NewGuid():N}@example.com", FirstName = "Staff", LastName = "CasaZen", Role = UserRole.Admin, IsActive = true });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IUserContextMembershipService>().GrantAsync(staffId, [UserRole.Admin]);
        }

        using var client = factory.CreateAuthenticatedClient(staffId, roles: "Admin");

        Assert.Equal(["admin"], await OrgTeamHttp.ContextKeysAsync(client));
    }
}
