using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Supplier invite and self-serve registration (SU-01: A4-03, A4-04, A4-21, A4-24). The invite token is bound to the
/// invited email and comune, accepted once before its expiry by the signed-in account of that email; self-serve is
/// limited to the configured pilot comuni; the anonymous endpoint is rate limited.
/// </summary>
public class SupplierRegistrationIntegrationTests(SupplierRegistrationIntegrationTests.PilotComuniFactory factory)
    : IClassFixture<SupplierRegistrationIntegrationTests.PilotComuniFactory>
{
    private const string PilotCode = "H501";
    private const string PilotName = "Roma";

    // ─── Invite ─────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Register_AdminInviteAcceptedBySignedInInvitedAccount_LinksUserUsesInviteAndKeepsCategories()
    {
        var email = $"invited-{Guid.NewGuid():N}@test.com";
        using (var admin = factory.CreateAuthenticatedClient(roles: "Admin"))
        {
            var created = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new
            {
                email,
                comuneCode = PilotCode,
                categories = new[] { ServiceCategories.Cleaning },
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var token = TokenFromInviteEmail(email);

        using var anonymous = factory.CreateClient();
        var lookup = await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token });
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var preview = await lookup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, preview.GetProperty("email").GetString());
        Assert.Equal(PilotCode, preview.GetProperty("comuneCode").GetString());
        Assert.Equal(PilotName, preview.GetProperty("comuneName").GetString());

        // A brand-new Auth0 account that never called /api/users/me: the registration creates and links the user.
        var userId = $"auth0|invited-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, email: email.ToUpperInvariant());

        var response = await RegisterAsync(client, email, PilotCode, token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var orgId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal(orgId, user.SupplierOrgId);
            var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
            Assert.Equal(email, profile.Email);
            Assert.Equal($"[\"{PilotCode}\"]", profile.ComuniJson);
            Assert.Contains(ServiceCategories.Cleaning, profile.CategoriesJson);
            var invite = await db.SupplierInviteRecords.SingleAsync(i => i.Email == email);
            Assert.True(invite.IsUsed);
            Assert.Equal(SupplierInviteTokens.Hash(token), invite.TokenHash);
            Assert.NotEqual(token, invite.TokenHash);
        }
    }

    [PostgresFact]
    public async Task Register_InviteWithDifferentAccountEmail_Returns422AndKeepsInviteUnused()
    {
        var (inviteId, token, email) = await SeedInviteAsync();
        var userId = $"auth0|other-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, email: $"other-{Guid.NewGuid():N}@test.com");

        // The form carries the invited email, but the signed-in account is another one.
        var response = await RegisterAsync(client, email, PilotCode, token);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_invite_email_mismatch");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False((await db.SupplierInviteRecords.SingleAsync(i => i.Id == inviteId)).IsUsed);
            Assert.False(await db.SupplierProfiles.AnyAsync(p => p.Email == email));
            Assert.Null((await db.Users.SingleAsync(u => u.Id == userId)).SupplierOrgId);
        }
    }

    [PostgresFact]
    public async Task Register_InviteForAnotherComune_Returns422ComuneMismatch()
    {
        var (_, token, email) = await SeedInviteAsync();
        using var client = factory.CreateAuthenticatedClient($"auth0|comune-{Guid.NewGuid():N}", email: email);

        var response = await RegisterAsync(client, email, "F205", token);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_invite_comune_mismatch");
    }

    [PostgresFact]
    public async Task Register_InviteAlreadyUsed_Returns422InviteUsed()
    {
        var (_, token, email) = await SeedInviteAsync();
        using var first = factory.CreateAuthenticatedClient($"auth0|first-{Guid.NewGuid():N}", email: email);
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(first, email, PilotCode, token)).StatusCode);

        // A second account with the same email cannot reuse the token.
        using var second = factory.CreateAuthenticatedClient($"auth0|second-{Guid.NewGuid():N}", email: email);
        var reused = await RegisterAsync(second, email, PilotCode, token);

        await AssertProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "supplier_invite_used");
        using var anonymous = factory.CreateClient();
        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token }),
            HttpStatusCode.UnprocessableEntity,
            "supplier_invite_used");
    }

    [PostgresFact]
    public async Task Register_SameInviteAcceptedConcurrently_CreatesOneSupplierOrg()
    {
        var (_, token, email) = await SeedInviteAsync();
        using var first = factory.CreateAuthenticatedClient($"auth0|race-a-{Guid.NewGuid():N}", email: email);
        using var second = factory.CreateAuthenticatedClient($"auth0|race-b-{Guid.NewGuid():N}", email: email);

        var responses = await Task.WhenAll(
            RegisterAsync(first, email, PilotCode, token),
            RegisterAsync(second, email, PilotCode, token));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.SupplierProfiles.CountAsync(p => p.Email == email));
    }

    [PostgresFact]
    public async Task Register_ExpiredInvite_Returns422InviteExpired()
    {
        var (_, token, email) = await SeedInviteAsync(expiresAt: DateTime.UtcNow.AddMinutes(-1));
        using var client = factory.CreateAuthenticatedClient($"auth0|expired-{Guid.NewGuid():N}", email: email);

        var response = await RegisterAsync(client, email, PilotCode, token);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_invite_expired");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SupplierProfiles.AnyAsync(p => p.Email == email));
    }

    [PostgresFact]
    public async Task Register_MalformedOrTruncatedToken_Returns422InviteInvalidNever500()
    {
        var (_, token, email) = await SeedInviteAsync();
        using var client = factory.CreateAuthenticatedClient($"auth0|malformed-{Guid.NewGuid():N}", email: email);
        using var anonymous = factory.CreateClient();

        string[] malformed =
        [
            "abc",
            token[..40],
            token[..63],
            Guid.NewGuid().ToString(),
            new string('z', 64),
            "../../etc/passwd",
            new string('0', 64),
        ];

        foreach (var value in malformed)
        {
            await AssertProblemAsync(
                await RegisterAsync(client, email, PilotCode, value),
                HttpStatusCode.UnprocessableEntity,
                "supplier_invite_invalid");
            await AssertProblemAsync(
                await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token = value }),
                HttpStatusCode.UnprocessableEntity,
                "supplier_invite_invalid");
        }
    }

    [PostgresFact]
    public async Task Register_InviteWithoutSignIn_Returns422LoginRequired()
    {
        var (inviteId, token, email) = await SeedInviteAsync();
        using var anonymous = factory.CreateClient();

        var response = await RegisterAsync(anonymous, email, PilotCode, token);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_invite_login_required");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False((await db.SupplierInviteRecords.SingleAsync(i => i.Id == inviteId)).IsUsed);
    }

    // ─── Self-serve ─────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task RegistrationOptions_PilotComuniConfigured_ReturnsSelfServeEnabledWithComuni()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync("/api/suppliers/registration-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("selfServeEnabled").GetBoolean());
        var comune = Assert.Single(body.GetProperty("pilotComuni").EnumerateArray());
        Assert.Equal(PilotCode, comune.GetProperty("code").GetString());
        Assert.Equal(PilotName, comune.GetProperty("name").GetString());
    }

    [PostgresFact]
    public async Task Register_AnonymousSelfServeInPilotComune_Returns201WithPendingProfile()
    {
        var email = $"self-{Guid.NewGuid():N}@test.com";
        using var anonymous = factory.CreateClient();

        var response = await RegisterAsync(anonymous, email, " h501 ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/supplier/activation", body.GetProperty("authRedirectUrl").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.SupplierProfiles.SingleAsync(p => p.Email == email);
            Assert.Equal($"[\"{PilotCode}\"]", profile.ComuniJson);
            Assert.Equal(Core.Entities.Enums.SupplierStatus.Pending, profile.Status);
        }
    }

    [PostgresFact]
    public async Task Register_SelfServeOutsidePilotComuni_Returns422ComuneNotPilot()
    {
        var email = $"outside-{Guid.NewGuid():N}@test.com";
        using var anonymous = factory.CreateClient();

        var response = await RegisterAsync(anonymous, email, "F205");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_comune_not_pilot");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SupplierProfiles.AnyAsync(p => p.Email == email));
    }

    [PostgresFact]
    public async Task Register_SignedInSelfServeWithAnotherEmail_Returns422WithoutCreatingSupplierOrg()
    {
        var attackerId = $"auth0|attacker-{Guid.NewGuid():N}";
        var victimEmail = $"victim-{Guid.NewGuid():N}@test.com";
        using var client = factory.CreateAuthenticatedClient(attackerId, email: $"attacker-{Guid.NewGuid():N}@test.com");

        var response = await RegisterAsync(client, victimEmail, PilotCode);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_account_email_mismatch");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.SupplierProfiles.AnyAsync(p => p.Email == victimEmail));
            Assert.Null((await db.Users.SingleAsync(u => u.Id == attackerId)).SupplierOrgId);
        }
    }

    [PostgresFact]
    public async Task Register_SignedInSelfServeWithOwnEmail_LinksUserToCreatedSupplierOrg()
    {
        var userId = $"auth0|self-signed-{Guid.NewGuid():N}";
        var email = $"self-signed-{Guid.NewGuid():N}@test.com";
        using var client = factory.CreateAuthenticatedClient(userId, email: email);

        var response = await RegisterAsync(client, email, PilotCode);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var orgId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(orgId, (await db.Users.SingleAsync(u => u.Id == userId)).SupplierOrgId);
    }

    [PostgresFact]
    public async Task Register_SignedInWithoutEmailClaim_Returns422AccountEmailMissing()
    {
        using var client = factory.CreateAuthenticatedClient($"auth0|no-email-{Guid.NewGuid():N}");

        var response = await RegisterAsync(client, $"no-email-{Guid.NewGuid():N}@test.com", PilotCode);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_account_email_missing");
    }

    // ─── Rate limit ─────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Register_OverThePublicRegistrationLimit_Returns429RateLimited()
    {
        await using var limited = new RateLimitedFactory();
        using var anonymous = limited.CreateClient();

        for (var i = 0; i < RateLimitedFactory.PermitLimit; i++)
        {
            var allowed = await RegisterAsync(anonymous, $"limit-{i}-{Guid.NewGuid():N}@test.com", PilotCode);
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        var limitedResponse = await RegisterAsync(anonymous, $"limit-x-{Guid.NewGuid():N}@test.com", PilotCode);

        await AssertProblemAsync(limitedResponse, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.NotNull(limitedResponse.Headers.RetryAfter);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string email, string comuneCode, string? inviteToken = null) =>
        client.PostAsJsonAsync("/api/suppliers/register", new
        {
            email,
            legalName = "Pulizie Test Srl",
            phone = "+39 06 123456",
            comuneCode,
            inviteToken,
        });

    private async Task<(Guid InviteId, string Token, string Email)> SeedInviteAsync(DateTime? expiresAt = null)
    {
        var token = SupplierInviteTokens.Generate();
        var email = $"invite-{Guid.NewGuid():N}@test.com";
        var invite = new SupplierInviteRecord
        {
            Email = email,
            TokenHash = SupplierInviteTokens.Hash(token),
            ComuneCode = PilotCode,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SupplierInviteRecords.Add(invite);
            await db.SaveChangesAsync();
        }

        return (invite.Id, token, email);
    }

    private string TokenFromInviteEmail(string email)
    {
        var (_, content, _) = Assert.Single(factory.Emails.Queued, e => e.To == email);
        var match = Regex.Match(content.HtmlBody, "/register\\?inviteToken=([0-9a-f]{64})\"");
        Assert.True(match.Success, "The invite email has no web app registration link.");
        return match.Groups[1].Value;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    /// <summary>One pilot comune (H501 = Roma) and a recording email queue.</summary>
    public class PilotComuniFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings()));
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
            });
        }

        protected virtual Dictionary<string, string?> Settings() => new()
        {
            ["Suppliers:PilotComuni:0:Code"] = PilotCode,
            ["Suppliers:PilotComuni:0:Name"] = PilotName,
        };
    }

    /// <summary>Same settings with a public registration limit of <see cref="PermitLimit"/> per client and window.</summary>
    private sealed class RateLimitedFactory : PilotComuniFactory
    {
        public const int PermitLimit = 2;

        protected override Dictionary<string, string?> Settings()
        {
            var settings = base.Settings();
            settings["RateLimiting:PublicRegistration:PermitLimit"] = PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return settings;
        }
    }
}
