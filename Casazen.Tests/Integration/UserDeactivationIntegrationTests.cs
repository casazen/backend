using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-03 (A1-04) on real PostgreSQL: a deactivated user is refused with 403 <c>account_inactive</c> on every
/// authenticated request (admin, host and supplier endpoints alike, whatever the roles still in its token); the
/// deactivation blocks the Auth0 account and removes its roles (Auth0 mocked), keeps the DB deactivation when Auth0 is
/// down, refuses self-deactivation and the last active admin with 422; the reactivation gives the roles back.
/// </summary>
public class UserDeactivationIntegrationTests(UserDeactivationIntegrationTests.DeactivationFactory factory)
    : IClassFixture<UserDeactivationIntegrationTests.DeactivationFactory>
{
    private const string AccountInactive = "account_inactive";

    [PostgresTheory]
    [InlineData("Admin", "GET", "/api/admin/stats")]
    [InlineData("Admin", "GET", "/api/users")]
    [InlineData("Admin", "PUT", "/api/orgs/me/plan")]
    [InlineData("PropertyOwner", "GET", "/api/properties")]
    [InlineData("PropertyOwner", "POST", "/api/properties")]
    [InlineData("PropertyOwner", "GET", "/api/me/contexts")]
    [InlineData("Supplier", "GET", "/api/supplier/profile")]
    [InlineData("Supplier", "GET", "/api/supplier/inbox")]
    [InlineData("PropertyOwner", "GET", "/api/users/me")]
    public async Task Request_DeactivatedUserWithRolesInToken_Returns403AccountInactive(string roles, string method, string path)
    {
        var userId = NewUserId("inactive");
        await factory.SeedOrgForOwnerAsync(userId);
        await SetActiveInDbAsync(userId, false);
        using var client = factory.CreateAuthenticatedClient(userId, roles: roles);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { planTier = "Pro" }),
        });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AccountInactive);
    }

    [PostgresFact]
    public async Task Request_SameUserActive_IsNotRefused()
    {
        var userId = NewUserId("active");
        await factory.SeedOrgForOwnerAsync(userId);
        using var client = factory.CreateAuthenticatedClient(userId, roles: "Admin");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Deactivate_ActiveUser_BlocksAuth0RemovesItsRolesAndRefusesItsNextRequest()
    {
        var targetId = NewUserId("target");
        await factory.SeedOrgForOwnerAsync(targetId);
        factory.Auth0
            .Setup(a => a.GetUserRolesAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner, UserRole.Supplier]));
        using var target = factory.CreateAuthenticatedClient(targetId, roles: "PropertyOwner,Supplier");
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/users/me")).StatusCode);
        using var admin = factory.CreateAuthenticatedClient(NewUserId("admin"), roles: "Admin");

        var response = await admin.DeleteAsync($"/api/users/{Uri.EscapeDataString(targetId)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.True(body.GetProperty("auth0Synced").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("auth0SyncError").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));

        factory.Auth0.Verify(a => a.SetBlockedAsync(targetId, true, It.IsAny<CancellationToken>()), Times.Once);
        factory.Auth0.Verify(a => a.RemoveRolesAsync(
            targetId,
            It.Is<IReadOnlyCollection<UserRole>>(r => r.Count == 2 && r.Contains(UserRole.PropertyOwner) && r.Contains(UserRole.Supplier)),
            It.IsAny<CancellationToken>()), Times.Once);

        var stored = await LoadUserAsync(targetId);
        Assert.False(stored.IsActive);
        Assert.Equal(["PropertyOwner", "Supplier"], stored.SuspendedAuth0Roles!.Order());

        // The token still carries its roles: the API refuses it anyway, from the very next request.
        await AssertProblemAsync(await target.GetAsync("/api/users/me"), HttpStatusCode.Forbidden, AccountInactive);
        await AssertProblemAsync(await target.GetAsync("/api/properties"), HttpStatusCode.Forbidden, AccountInactive);
    }

    [PostgresFact]
    public async Task Deactivate_Auth0Unavailable_DeactivatesInDbAndReportsAuth0NotSynced()
    {
        var targetId = NewUserId("auth0-down");
        await factory.SeedOrgForOwnerAsync(targetId);
        var down = Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode);
        factory.Auth0.Setup(a => a.SetBlockedAsync(targetId, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(down);
        factory.Auth0
            .Setup(a => a.GetUserRolesAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0UserRolesResult.Failed(down));
        using var admin = factory.CreateAuthenticatedClient(NewUserId("admin"), roles: "Admin");

        var response = await admin.DeleteAsync($"/api/users/{Uri.EscapeDataString(targetId)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.False(body.GetProperty("auth0Synced").GetBoolean());
        Assert.Equal(Auth0SyncResult.ApiErrorCode, body.GetProperty("auth0SyncError").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));

        var stored = await LoadUserAsync(targetId);
        Assert.False(stored.IsActive);
        Assert.Null(stored.SuspendedAuth0Roles);
        factory.Auth0.Verify(
            a => a.RemoveRolesAsync(targetId, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Enforcement does not depend on Auth0.
        using var target = factory.CreateAuthenticatedClient(targetId, roles: "PropertyOwner");
        await AssertProblemAsync(await target.GetAsync("/api/properties"), HttpStatusCode.Forbidden, AccountInactive);
    }

    [PostgresFact]
    public async Task Deactivate_OwnAccount_Returns422WithoutChanges()
    {
        var adminId = NewUserId("self");
        await SeedUserAsync(adminId, UserRole.Admin);
        using var admin = factory.CreateAuthenticatedClient(adminId, roles: "Admin");

        var response = await admin.DeleteAsync($"/api/users/{Uri.EscapeDataString(adminId)}");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, UserActivationErrors.CannotDeactivateSelf);
        Assert.True((await LoadUserAsync(adminId)).IsActive);
        factory.Auth0.Verify(a => a.SetBlockedAsync(adminId, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task Deactivate_LastActiveAdmin_Returns422WithoutChanges()
    {
        var lastAdminId = NewUserId("last-admin");
        await SeedUserAsync(lastAdminId, UserRole.Admin);
        await DeactivateOtherAdminsAsync(lastAdminId);
        // The caller is an admin through its token only (role assigned in the Auth0 dashboard, not in the DB).
        using var caller = factory.CreateAuthenticatedClient(NewUserId("token-admin"), roles: "Admin");

        var response = await caller.DeleteAsync($"/api/users/{Uri.EscapeDataString(lastAdminId)}");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, UserActivationErrors.LastActiveAdmin);
        Assert.True((await LoadUserAsync(lastAdminId)).IsActive);
        factory.Auth0.Verify(a => a.SetBlockedAsync(lastAdminId, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task Deactivate_TwoAdminsDeactivatingEachOtherAtOnce_OnlyOneSucceeds()
    {
        var firstId = NewUserId("admin-a");
        var secondId = NewUserId("admin-b");
        await SeedUserAsync(firstId, UserRole.Admin);
        await SeedUserAsync(secondId, UserRole.Admin);
        await DeactivateOtherAdminsAsync(firstId, secondId);
        using var first = factory.CreateAuthenticatedClient(firstId, roles: "Admin");
        using var second = factory.CreateAuthenticatedClient(secondId, roles: "Admin");

        var responses = await Task.WhenAll(
            first.DeleteAsync($"/api/users/{Uri.EscapeDataString(secondId)}"),
            second.DeleteAsync($"/api/users/{Uri.EscapeDataString(firstId)}"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Forbidden);
        var stillActive = new[] { (await LoadUserAsync(firstId)).IsActive, (await LoadUserAsync(secondId)).IsActive };
        Assert.Single(stillActive, active => active);
    }

    [PostgresFact]
    public async Task Reactivate_AfterDeactivation_GivesBackTheRemovedRolesThenUnblocks()
    {
        var targetId = NewUserId("reactivate");
        await factory.SeedOrgForOwnerAsync(targetId);
        IReadOnlyList<UserRole> auth0Roles = [UserRole.LongTermLandlord, UserRole.Supplier];
        factory.Auth0
            .Setup(a => a.GetUserRolesAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, auth0Roles));
        var calls = new List<string>();
        factory.Auth0
            .Setup(a => a.AssignRolesAsync(targetId, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyCollection<UserRole>, CancellationToken>((_, roles, _) =>
                calls.Add("assign:" + string.Join(",", roles.Order())))
            .ReturnsAsync(Auth0SyncResult.Synced);
        factory.Auth0
            .Setup(a => a.SetBlockedAsync(targetId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, bool, CancellationToken>((_, blocked, _) => calls.Add(blocked ? "block" : "unblock"))
            .ReturnsAsync(Auth0SyncResult.Synced);
        using var admin = factory.CreateAuthenticatedClient(NewUserId("admin"), roles: "Admin");
        using var target = factory.CreateAuthenticatedClient(targetId, roles: "LongTermLandlord,Supplier");

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/users/{Uri.EscapeDataString(targetId)}")).StatusCode);
        await AssertProblemAsync(await target.GetAsync("/api/users/me"), HttpStatusCode.Forbidden, AccountInactive);

        var response = await admin.PostAsync($"/api/users/{Uri.EscapeDataString(targetId)}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.True(body.GetProperty("auth0Synced").GetBoolean());
        Assert.Equal(
            ["LongTermLandlord", "Supplier"],
            body.GetProperty("rolesRestored").EnumerateArray().Select(e => e.GetString()!).Order());
        // Inverse order of the deactivation: roles back first, then unblock.
        Assert.Equal(["block", $"assign:{UserRole.LongTermLandlord},{UserRole.Supplier}", "unblock"], calls);

        var stored = await LoadUserAsync(targetId);
        Assert.True(stored.IsActive);
        Assert.Null(stored.SuspendedAuth0Roles);
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/users/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Reactivate_Auth0Unavailable_ReactivatesInDbKeepsRolesToRestoreAndReportsNotSynced()
    {
        var targetId = NewUserId("reactivate-down");
        await SeedUserAsync(targetId, UserRole.PropertyOwner, isActive: false, suspendedRoles: ["PropertyOwner"]);
        factory.Auth0
            .Setup(a => a.AssignRolesAsync(targetId, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.RateLimitedCode));
        using var admin = factory.CreateAuthenticatedClient(NewUserId("admin"), roles: "Admin");

        var response = await admin.PostAsync($"/api/users/{Uri.EscapeDataString(targetId)}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.False(body.GetProperty("auth0Synced").GetBoolean());
        Assert.Equal(Auth0SyncResult.RateLimitedCode, body.GetProperty("auth0SyncError").GetString());
        Assert.Empty(body.GetProperty("rolesRestored").EnumerateArray());
        // Still blocked in Auth0 (never half restored), and the roles are kept for the retry.
        factory.Auth0.Verify(a => a.SetBlockedAsync(targetId, false, It.IsAny<CancellationToken>()), Times.Never);
        var stored = await LoadUserAsync(targetId);
        Assert.True(stored.IsActive);
        Assert.Equal(["PropertyOwner"], stored.SuspendedAuth0Roles);
    }

    [PostgresFact]
    public async Task ChangeRole_DeactivatedUser_Returns422UserInactive()
    {
        var targetId = NewUserId("role-inactive");
        await SeedUserAsync(targetId, UserRole.PropertyOwner, isActive: false, suspendedRoles: ["PropertyOwner"]);
        using var admin = factory.CreateAuthenticatedClient(NewUserId("admin"), roles: "Admin");

        var response = await admin.PutAsJsonAsync($"/api/users/{Uri.EscapeDataString(targetId)}/role", new { role = "Admin" });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, UserActivationErrors.UserInactive);
        Assert.Equal(UserRole.PropertyOwner, (await LoadUserAsync(targetId)).Role);
        factory.Auth0.Verify(a => a.AssignRoleAsync(targetId, It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string NewUserId(string label) => $"auth0|pl03-{label}-{Guid.NewGuid():N}";

    private async Task SeedUserAsync(
        string userId,
        UserRole role,
        bool isActive = true,
        List<string>? suspendedRoles = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Test",
            LastName = "User",
            Role = role,
            IsActive = isActive,
            SuspendedAuth0Roles = suspendedRoles,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetActiveInDbAsync(string userId, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Leaves <paramref name="keep"/> as the only active DB admins of this test database.</summary>
    private async Task DeactivateOtherAdminsAsync(params string[] keep)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users
            .Where(u => u.Role == UserRole.Admin && u.IsActive && !keep.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
    }

    private async Task<User> LoadUserAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    /// <summary>Mocked Auth0 Management API: every call succeeds unless a test sets up a failure for its user.</summary>
    public class DeactivationFactory : CasazenWebApplicationFactory
    {
        public Mock<IAuth0ManagementService> Auth0 { get; } = CreateAuth0Mock();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IAuth0ManagementService>(services);
                services.AddSingleton(Auth0.Object);
            });
        }

        private static Mock<IAuth0ManagementService> CreateAuth0Mock()
        {
            var auth0 = new Mock<IAuth0ManagementService>();
            auth0.SetupGet(a => a.IsConfigured).Returns(true);
            auth0
                .Setup(a => a.AssignRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0
                .Setup(a => a.RemoveRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0
                .Setup(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0
                .Setup(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0
                .Setup(a => a.SetBlockedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0
                .Setup(a => a.GetUserRolesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner]));
            auth0.Setup(a => a.GetUserProfileAsync(It.IsAny<string>())).ReturnsAsync((Auth0UserProfile?)null);
            return auth0;
        }
    }
}
