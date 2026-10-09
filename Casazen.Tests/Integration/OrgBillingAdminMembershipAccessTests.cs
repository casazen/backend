using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-00 (S1, D12) over the real pipeline: every action under the policy <c>OrgBillingAdmin</c> (Orgs, Billing, Connect,
/// Branding, Domain, SiteDocuments, found by reflection) answers 403 <c>forbidden</c> to a member of the org who has a DB
/// membership and no <c>Staff</c> claim, and to a <c>PropertyManager</c>; the owner of the org is not refused. The member
/// is as strong as a collaborator can be inside the context (<c>property.write</c>, <c>payment.write</c>…): the org
/// policy, not the context permissions, decides. The authorization runs before the body is read, so the empty bodies
/// sent here are never looked at.
/// </summary>
public class OrgBillingAdminMembershipAccessTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    /// <summary>Permissions of the custom role of the members: everything a collaborator could be given in the context.</summary>
    private static readonly string[] MemberPermissions =
    [
        "property.read", "property.write", "booking.read", "booking.write", "payment.read", "payment.write",
        "guest.read", "guest.write", "lease.read", "lease.create",
    ];

    /// <summary>
    /// <c>context of the membership, role key, JWT roles, DB role of the user</c>; no context means no membership at all.
    /// </summary>
    public static TheoryData<string?, string?, string?, UserRole> Members => new()
    {
        // A member with the membership only: no JWT role, no Staff claim.
        { "short-rent", "bk09_collaborator", null, UserRole.None },
        { "long-rent", "bk09_collaborator", null, UserRole.None },
        { "short-rent", "property_manager", null, UserRole.None },
        { "long-rent", "staff", null, UserRole.None },
        // D12: the property manager, by JWT role and by membership.
        { "short-rent", "property_manager", "PropertyManager", UserRole.PropertyManager },
        { null, null, "PropertyManager", UserRole.PropertyManager },
    };

    [Theory]
    [MemberData(nameof(Members))]
    public async Task EveryOrgBillingAdminAction_AsMemberOfTheOrg_Returns403(
        string? contextKey,
        string? roleKey,
        string? jwtRoles,
        UserRole dbRole)
    {
        Assert.NotEmpty(OrgBillingAdminActions.All);
        var org = await factory.SeedOrgForOwnerAsync($"auth0|am00-owner-{Guid.NewGuid():N}");
        var member = await SeedMemberAsync(org.Id, contextKey, roleKey, dbRole);
        using var client = factory.CreateAuthenticatedClient(member, jwtRoles);

        var notRefused = new List<string>();
        foreach (var action in OrgBillingAdminActions.All)
        {
            using var request = BuildRequest(action, org.Id);
            using var response = await client.SendAsync(request);

            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                notRefused.Add($"{action} answered {(int)response.StatusCode}");
                continue;
            }

            // The authorization refused it (not onboarding_required, not account_inactive).
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (problem.GetProperty("code").GetString() != "forbidden")
                notRefused.Add($"{action} answered 403 {problem.GetProperty("code").GetString()}");
        }

        Assert.True(
            notRefused.Count == 0,
            "A member of the org (no Staff claim) reached the org billing actions: " + string.Join("; ", notRefused));
    }

    [Fact]
    public async Task EveryOrgBillingAdminAction_AsOwnerOfTheOrg_IsNotRefusedByTheAuthorization()
    {
        // The positive control of the test above: the 403 of the member is the policy's, not a route nobody can call.
        var ownerId = $"auth0|am00-owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        using var client = factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");

        var refused = new List<string>();
        foreach (var action in OrgBillingAdminActions.All)
        {
            using var request = BuildRequest(action, org.Id);
            using var response = await client.SendAsync(request);

            // Whatever the action answers with an empty body (200, 400, 404, 409, 422…): it ran, so it was authorized.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                refused.Add($"{action} answered {(int)response.StatusCode}");
        }

        Assert.True(refused.Count == 0, "The owner of the org was refused: " + string.Join("; ", refused));
    }

    private static HttpRequestMessage BuildRequest(OrgBillingAdminAction action, Guid orgId)
    {
        var request = new HttpRequestMessage(new HttpMethod(action.HttpMethod), action.ResolveRoute(orgId));
        if (action.HttpMethod is "POST" or "PUT" or "PATCH")
        {
            // [Consumes("multipart/form-data")] endpoints are matched by content type before the authorization runs.
            request.Content = action.ConsumedContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true
                ? new MultipartFormDataContent { { new ByteArrayContent([0]), "file", "x.png" } }
                : JsonContent.Create(new { });
        }

        return request;
    }

    /// <summary>
    /// A user of the org who completed the onboarding for it (PL-02), with a DB membership of a custom role in the given
    /// context (or none): a collaborator of the org, not its owner.
    /// </summary>
    private async Task<string> SeedMemberAsync(Guid orgId, string? contextKey, string? roleKey, UserRole dbRole)
    {
        var userId = $"auth0|am00-member-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collaboratore",
            LastName = "Org",
            OrgId = orgId,
            Role = dbRole,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());

        if (contextKey is not null && roleKey is not null)
        {
            // The role key is unique per context: a suffix keeps the cases independent of each other.
            var role = new Role
            {
                Id = Random.Shared.Next(100_000, int.MaxValue),
                ContextKey = contextKey,
                RoleKey = $"{roleKey}_{Guid.NewGuid():N}",
            };
            foreach (var permission in MemberPermissions)
                role.Permissions.Add(new RolePermission { PermissionKey = permission });

            db.Roles.Add(role);
            db.UserContextMemberships.Add(new UserContextMembership { UserId = userId, ContextKey = contextKey, RoleId = role.Id });
        }

        await db.SaveChangesAsync();
        return userId;
    }
}
