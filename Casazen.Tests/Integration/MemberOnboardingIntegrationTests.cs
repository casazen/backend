using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-00 (S2) over the real pipeline: a collaborator of an org, with a DB membership of a custom role, cannot turn itself
/// into the owner with <c>PUT</c> or <c>POST /api/users/onboarding</c> (the grant used to overwrite the role of its
/// membership and assign <c>PropertyOwner</c> in Auth0): 409 <c>member_cannot_onboard</c> with a localized text, role and
/// membership untouched. The owner of an org can still change its rental type.
/// </summary>
public class MemberOnboardingIntegrationTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-v1";
    private const string OnboardingPath = "/api/users/onboarding";

    /// <summary>Role id of the seeded short-rent <c>property_owner</c> (<c>AppDbContext</c> seed).</summary>
    private const int PropertyOwnerRoleId = 1;

    [Theory]
    [InlineData("PUT", "ShortTerm")]
    [InlineData("PUT", "LongTerm")]
    [InlineData("PUT", "Both")]
    [InlineData("POST", "ShortTerm")]
    public async Task Onboarding_CollaboratorOfTheOrg_Returns409AndKeepsItsRoleAndMembership(string verb, string rentalType)
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am00-owner-{Guid.NewGuid():N}");
        var (collaborator, roleId, roleKey) = await SeedCollaboratorAsync(org.Id);
        using var client = factory.CreateAuthenticatedClient(collaborator);

        using var response = await SendOnboardingAsync(client, verb, rentalType);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("member_cannot_onboard", problem.GetProperty("code").GetString());
        var detail = problem.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.NotEqual("MemberCannotOnboard", detail);
        Assert.Contains("collaboratore", detail, StringComparison.Ordinal);

        // Nothing changed: still the collaborator role in short-rent, no host role, no rental type, same org.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var memberships = await db.UserContextMemberships.AsNoTracking().Include(m => m.Role)
            .Where(m => m.UserId == collaborator).ToListAsync();
        var membership = Assert.Single(memberships);
        Assert.Equal("short-rent", membership.ContextKey);
        Assert.Equal(roleId, membership.RoleId);
        Assert.Equal(roleKey, membership.Role.RoleKey);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == collaborator);
        Assert.Equal(UserRole.None, user.Role);
        Assert.Null(user.RentalType);
        Assert.Equal(org.Id, user.OrgId);
    }

    [Fact]
    public async Task Onboarding_CollaboratorWithEnglishAcceptLanguage_GetsTheEnglishText()
    {
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am00-owner-{Guid.NewGuid():N}");
        var (collaborator, _, _) = await SeedCollaboratorAsync(org.Id);
        using var client = factory.CreateAuthenticatedClient(collaborator);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        using var response = await SendOnboardingAsync(client, "PUT", "ShortTerm");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("member_cannot_onboard", problem.GetProperty("code").GetString());
        Assert.Contains("collaborator", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Onboarding_OwnerOfTheOrgSwitchingRentalType_StillWorks()
    {
        // The legitimate owner is not a member: its own role (property_owner) is the owner's, so it can change what it rents.
        // PostgreSQL only: the roles it grants and revokes (long_term_landlord, property_owner) come from the migration seed.
        var ownerId = $"auth0|am00-owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserContextMemberships.Add(new UserContextMembership { UserId = ownerId, ContextKey = "short-rent", RoleId = PropertyOwnerRoleId });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(ownerId);
        using var response = await SendOnboardingAsync(client, "PUT", "LongTerm");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(org.Id, body.GetProperty("orgId").GetGuid());
        Assert.Equal(["LongTermLandlord"], body.GetProperty("rolesAssigned").EnumerateArray().Select(r => r.GetString()));

        using var verify = factory.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var contexts = await check.UserContextMemberships.AsNoTracking()
            .Where(m => m.UserId == ownerId).Select(m => m.ContextKey).ToListAsync();
        Assert.Equal(["long-rent"], contexts);
    }

    [Fact]
    public async Task Onboarding_NewUserWithoutOrgOrMembership_StillCreatesItsOrg()
    {
        var userId = $"auth0|am00-new-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner");

        using var response = await SendOnboardingAsync(client, "POST", "ShortTerm");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("orgProvisioned").GetBoolean());
    }

    private static Task<HttpResponseMessage> SendOnboardingAsync(HttpClient client, string verb, string rentalType)
    {
        var body = new
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
        };

        return verb == "PUT"
            ? client.PutAsJsonAsync(OnboardingPath, body)
            : client.PostAsJsonAsync(OnboardingPath, body);
    }

    /// <summary>
    /// A collaborator of the org: onboarded for it (PL-02), no host role of its own (<c>UserRole.None</c>), and a
    /// short-rent DB membership of a custom, non-owner role with real permissions.
    /// </summary>
    private async Task<(string UserId, int RoleId, string RoleKey)> SeedCollaboratorAsync(Guid orgId)
    {
        var userId = $"auth0|am00-collaborator-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collaboratore",
            LastName = "Org",
            OrgId = orgId,
            Role = UserRole.None,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());

        var role = new Role
        {
            Id = Random.Shared.Next(100_000, int.MaxValue),
            ContextKey = "short-rent",
            RoleKey = $"bk09_collaborator_{Guid.NewGuid():N}",
        };
        foreach (var permission in new[] { "property.read", "property.write", "booking.read", "booking.write", "payment.read" })
            role.Permissions.Add(new RolePermission { PermissionKey = permission });

        db.Roles.Add(role);
        db.UserContextMemberships.Add(new UserContextMembership { UserId = userId, ContextKey = "short-rent", RoleId = role.Id });
        await db.SaveChangesAsync();
        return (userId, role.Id, role.RoleKey);
    }
}
