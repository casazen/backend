using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
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
/// Claim of a supplier profile registered without an account (SU-02: A4-02, A4-23, A1-13). The anonymous registration
/// returns a one-use claim token bound to the registered email; the account signs up and presents it to
/// <c>POST /api/suppliers/claim</c>, which links the account and assigns the Supplier role (additive, FD-14). Without a
/// token only an email verified by Auth0 links, and the supplier endpoints never link a profile by email.
/// </summary>
public class SupplierClaimIntegrationTests(SupplierClaimIntegrationTests.ClaimFactory factory)
    : IClassFixture<SupplierClaimIntegrationTests.ClaimFactory>
{
    private const string PilotCode = "H501";

    [PostgresFact]
    public async Task Claim_ValidTokenWithRegisteredEmail_LinksUserAndAssignsSupplierRole()
    {
        var email = NewEmail("claim-ok");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        var userId = NewUserId("claim-ok");
        // Just signed up: the email is not verified yet, the token proves the registrant.
        using var client = factory.CreateAuthenticatedClient(userId, email: email.ToUpperInvariant(), emailVerified: false);

        var response = await ClaimAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(orgId, body.GetProperty("orgId").GetGuid());
        Assert.True(body.GetProperty("rolesSynced").GetBoolean());
        Assert.Equal("/app/supplier/activation", body.GetProperty("redirectUrl").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal(orgId, user.SupplierOrgId);
            Assert.Equal(orgId, user.OrgId);
            var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
            Assert.Equal(SupplierClaimTokens.Hash(token), profile.ClaimTokenHash);
            Assert.NotEqual(token, profile.ClaimTokenHash);
        }

        factory.Auth0.Verify(a => a.AssignRoleAsync(userId, UserRole.Supplier, It.IsAny<CancellationToken>()), Times.Once);
        factory.Auth0.Verify(a => a.RemoveRoleAsync(userId, It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
        factory.Auth0.Verify(a => a.RemoveRolesAsync(userId, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task Claim_TokenReusedByAnotherAccount_Returns422ClaimUsedWithoutLinking()
    {
        var email = NewEmail("claim-reuse");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        using (var first = factory.CreateAuthenticatedClient(NewUserId("claim-first"), email: email))
            Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(first, token)).StatusCode);

        // Another account of the same email (e.g. a social login) cannot take the profile with the same token.
        var secondId = NewUserId("claim-second");
        using var second = factory.CreateAuthenticatedClient(secondId, email: email, emailVerified: true);
        var reused = await ClaimAsync(second, token);

        await AssertProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "supplier_claim_used");
        await AssertNotLinkedAsync(secondId, orgId);
    }

    [PostgresFact]
    public async Task Claim_TokenWithAnotherAccountEmail_Returns422EmailMismatchWithoutLinking()
    {
        var (orgId, token) = await RegisterAnonymouslyAsync(NewEmail("claim-victim"));
        var otherId = NewUserId("claim-other");
        using var client = factory.CreateAuthenticatedClient(otherId, email: NewEmail("claim-other"), emailVerified: true);

        var response = await ClaimAsync(client, token);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_claim_email_mismatch");
        await AssertNotLinkedAsync(otherId, orgId);
        factory.Auth0.Verify(a => a.AssignRoleAsync(otherId, It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task Claim_RepeatedBySameAccount_ReturnsSameOrgAndRetriesTheRole()
    {
        var email = NewEmail("claim-retry");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        var userId = NewUserId("claim-retry");
        using var client = factory.CreateAuthenticatedClient(userId, email: email);

        var first = await ClaimAsync(client, token);
        var again = await ClaimAsync(client, token);
        var withoutToken = await ClaimAsync(client, null);

        foreach (var response in new[] { first, again, withoutToken })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(orgId, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid());
        }

        factory.Auth0.Verify(a => a.AssignRoleAsync(userId, UserRole.Supplier, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [PostgresFact]
    public async Task Claim_Auth0RoleSyncFails_LinksAndReportsRolesNotSynced()
    {
        var email = NewEmail("claim-nosync");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        var userId = NewUserId("claim-nosync");
        factory.Auth0
            .Setup(a => a.AssignRoleAsync(userId, UserRole.Supplier, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode));
        using var client = factory.CreateAuthenticatedClient(userId, email: email);

        var response = await ClaimAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("rolesSynced").GetBoolean());
        Assert.Equal(Auth0SyncResult.ApiErrorCode, body.GetProperty("rolesSyncError").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(orgId, (await db.Users.SingleAsync(u => u.Id == userId)).SupplierOrgId);
    }

    [PostgresFact]
    public async Task Claim_ExpiredToken_Returns422ClaimExpiredWithoutLinking()
    {
        var email = NewEmail("claim-expired");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
            profile.ClaimTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var userId = NewUserId("claim-expired");
        using var client = factory.CreateAuthenticatedClient(userId, email: email);
        var response = await ClaimAsync(client, token);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_claim_expired");
        await AssertNotLinkedAsync(userId, orgId);
    }

    [PostgresFact]
    public async Task Claim_MalformedOrUnknownToken_Returns422ClaimInvalidNever500()
    {
        var email = NewEmail("claim-malformed");
        var (_, token) = await RegisterAnonymouslyAsync(email);
        using var client = factory.CreateAuthenticatedClient(NewUserId("claim-malformed"), email: email);

        foreach (var value in new[] { "abc", token[..63], Guid.NewGuid().ToString(), new string('z', 64), new string('0', 64) })
            await AssertProblemAsync(await ClaimAsync(client, value), HttpStatusCode.UnprocessableEntity, "supplier_claim_invalid");
    }

    [PostgresFact]
    public async Task Claim_SameTokenClaimedConcurrently_LinksExactlyOneAccount()
    {
        var email = NewEmail("claim-race");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        using var first = factory.CreateAuthenticatedClient(NewUserId("claim-race-a"), email: email);
        using var second = factory.CreateAuthenticatedClient(NewUserId("claim-race-b"), email: email);

        var responses = await Task.WhenAll(ClaimAsync(first, token), ClaimAsync(second, token));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.SupplierOrgId == orgId));
    }

    [PostgresFact]
    public async Task Claim_AccountAlreadyLinkedToAnotherSupplier_Returns409WithoutRelinking()
    {
        var linkedEmail = NewEmail("claim-linked");
        var (linkedOrgId, linkedToken) = await RegisterAnonymouslyAsync(linkedEmail);
        var userId = NewUserId("claim-linked");
        using var client = factory.CreateAuthenticatedClient(userId, email: linkedEmail);
        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(client, linkedToken)).StatusCode);

        // Another anonymous registration (another email: one profile per email since SU-14).
        var (_, otherToken) = await RegisterAnonymouslyAsync(NewEmail("claim-linked-other"));
        var response = await ClaimAsync(client, otherToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "supplier_account_already_linked");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(linkedOrgId, (await db.Users.SingleAsync(u => u.Id == userId)).SupplierOrgId);
    }

    // ─── Without token: verified email only ─────────────────────────────────

    [PostgresFact]
    public async Task Claim_WithoutTokenAndUnverifiedEmail_Returns422EmailUnverifiedWithoutLinking()
    {
        var email = NewEmail("claim-unverified");
        var (orgId, _) = await RegisterAnonymouslyAsync(email);
        var userId = NewUserId("claim-unverified");
        using var client = factory.CreateAuthenticatedClient(userId, email: email, emailVerified: false);

        var response = await ClaimAsync(client, null);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_claim_email_unverified");
        await AssertNotLinkedAsync(userId, orgId);
    }

    [PostgresFact]
    public async Task Claim_WithoutTokenAndVerifiedEmail_LinksTheOnlyUnclaimedProfile()
    {
        var email = NewEmail("claim-verified");
        // A profile registered before SU-02 (no claim token), never linked to an account.
        var orgId = await SeedUnclaimedProfileAsync(email);
        var userId = NewUserId("claim-verified");
        using var client = factory.CreateAuthenticatedClient(userId, email: email, emailVerified: true);

        var response = await ClaimAsync(client, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(orgId, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(orgId, (await db.Users.SingleAsync(u => u.Id == userId)).SupplierOrgId);
    }

    [PostgresFact]
    public async Task Claim_WithoutTokenAndNoUnclaimedProfile_Returns422NotFound()
    {
        using var client = factory.CreateAuthenticatedClient(
            NewUserId("claim-none"), email: NewEmail("claim-none"), emailVerified: true);

        var response = await ClaimAsync(client, null);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_claim_not_found");
    }

    // ─── No automatic link by email (A4-23, A1-13) ──────────────────────────

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplierEndpoint_SupplierRoleWithEmailOfUnclaimedProfile_Returns409WithoutLinkOrDuplicate(bool emailVerified)
    {
        var email = NewEmail($"claim-auto-{emailVerified}");
        var orgId = await SeedUnclaimedProfileAsync(email);
        Guid inviteId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invite = new SupplierInviteRecord
            {
                Email = email,
                TokenHash = SupplierInviteTokens.Hash(SupplierInviteTokens.Generate()),
                ComuneCode = PilotCode,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            };
            db.SupplierInviteRecords.Add(invite);
            await db.SaveChangesAsync();
            inviteId = invite.Id;
        }

        // A Supplier role given by hand to an Auth0 account that only shows the same email.
        var userId = NewUserId($"claim-auto-{emailVerified}");
        using var client = factory.CreateAuthenticatedClient(userId, "Supplier", email, emailVerified);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);

        var response = await client.GetAsync("/api/supplier/profile/activation");

        // Neither linked by email (A4-23) nor given a second profile with the same email (SU-14, A4-22): the account is
        // told to link the existing profile with the claim.
        await AssertProblemAsync(response, HttpStatusCode.Conflict, "supplier_email_taken");
        await AssertNotLinkedAsync(userId, orgId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The invite is accepted only with its token, never consumed by the email.
            Assert.False((await db.SupplierInviteRecords.SingleAsync(i => i.Id == inviteId)).IsUsed);
            Assert.False(await db.Users.AnyAsync(u => u.SupplierOrgId == orgId || u.OrgId == orgId));
            Assert.Equal(1, await db.SupplierProfiles.CountAsync(sp => sp.Email == email));
            Assert.Null((await db.Users.SingleAsync(u => u.Id == userId)).SupplierOrgId);
        }
    }

    // ─── /api/users/me ──────────────────────────────────────────────────────

    [PostgresFact]
    public async Task GetMe_ClaimedSupplier_ReturnsSupplierOrgId()
    {
        var email = NewEmail("claim-me");
        var (orgId, token) = await RegisterAnonymouslyAsync(email);
        var userId = NewUserId("claim-me");
        using var client = factory.CreateAuthenticatedClient(userId, email: email);

        var before = await (await client.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("supplierOrgId").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(client, token)).StatusCode);
        var me = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(orgId, body.GetProperty("supplierOrgId").GetGuid());
        Assert.Equal(orgId, body.GetProperty("orgId").GetGuid());
    }

    [PostgresFact]
    public async Task GetMe_HostWithLinkedSupplierProfile_ReturnsHostOrgAndSupplierOrg()
    {
        var userId = NewUserId("claim-dual");
        var hostOrg = await factory.SeedOrgForOwnerAsync(userId);
        var email = NewEmail("claim-dual");
        var supplierOrgId = await SeedUnclaimedProfileAsync(email);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.Email = email;
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(userId, "PropertyOwner", email, emailVerified: true);
        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(client, null)).StatusCode);

        var body = await (await client.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(hostOrg.Id, body.GetProperty("orgId").GetGuid());
        Assert.Equal(supplierOrgId, body.GetProperty("supplierOrgId").GetGuid());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string NewEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.com";

    private static string NewUserId(string prefix) => $"auth0|{prefix}-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> ClaimAsync(HttpClient client, string? claimToken) =>
        client.PostAsJsonAsync("/api/suppliers/claim", new { claimToken });

    /// <summary>Anonymous self-serve registration; returns the new org and the claim token of the response.</summary>
    private async Task<(Guid OrgId, string Token)> RegisterAnonymouslyAsync(string email)
    {
        using var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/suppliers/register", new
        {
            email,
            legalName = "Pulizie Claim Srl",
            phone = "+39 06 654321",
            comuneCode = PilotCode,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("claimToken").GetString();
        Assert.True(SupplierClaimTokens.TryNormalize(token, out _), "The anonymous registration returned no claim token.");
        Assert.True(body.GetProperty("claimExpiresAt").GetDateTime() > DateTime.UtcNow);
        return (body.GetProperty("orgId").GetGuid(), token!);
    }

    private async Task<Guid> SeedUnclaimedProfileAsync(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore Orfano",
            Slug = $"supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Fornitore Orfano",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore Orfano",
            Phone = "+39 06 000000",
            ComuniJson = $"[\"{PilotCode}\"]",
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task AssertNotLinkedAsync(string userId, Guid supplierOrgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId);
        Assert.NotEqual(supplierOrgId, user?.SupplierOrgId);
        Assert.NotEqual(supplierOrgId, user?.OrgId);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    /// <summary>Pilot comune H501 and a mocked Auth0 Management API (roles assigned, no profile lookup).</summary>
    public class ClaimFactory : SupplierRegistrationIntegrationTests.PilotComuniFactory
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
                .Setup(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0
                .Setup(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Auth0SyncResult.Synced);
            auth0.Setup(a => a.GetUserProfileAsync(It.IsAny<string>())).ReturnsAsync((Auth0UserProfile?)null);
            return auth0;
        }
    }
}
