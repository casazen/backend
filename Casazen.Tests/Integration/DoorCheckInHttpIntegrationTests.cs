using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// The web host with the identity verification of the check-in open (<c>Features:CheckInIdentityVerification</c>), the legal texts read from the folder the API ships (the host of the tests runs in the
/// <c>Testing</c> environment, which serves the drafts) and the limits of the door out of the way of a test that is not about them.
/// </summary>
public class CheckInIdentityFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:CheckInIdentityVerification"] = "true",
            ["Legal:ContentPath"] = LegalTextFixtures.ShippedRoot(),
            ["RateLimiting:PublicDoorCheckIn:PermitLimit"] = "1000",
            ["RateLimiting:PublicDoorCheckInPerIp:PermitLimit"] = "1000",
        }));
    }
}

/// <summary>
/// E22/F01/US02 (post-review proofs), over the real pipeline on PostgreSQL (the roles and the contexts come from the migration seed). <c>api/properties/{id}/door-qr</c>: a property of another org is a 404,
/// a property the caller does not reach or may not edit is a 403, nothing is written for a refused call, the flag off answers like a route that does not exist. <c>GET api/public/checkin/{token}/legal-texts</c>
/// serves the version the consent must carry, and <c>POST …/biometric-consent</c> refuses any other with the 422 <c>biometric_consent_version_unknown</c>. Many first requests at once make one door token,
/// and no two properties can share one.
/// </summary>
public class DoorCheckInHttpIntegrationTests(CheckInIdentityFactory factory) : IClassFixture<CheckInIdentityFactory>
{
    private const string ConsentVersion = "checkin-biometric-2026-11";

    /// <summary>
    /// Permission rule (scope): the owner makes, reads and replaces the code of any property of its org; a property of another org is a 404 for both calls; a collaborator «Solo alcuni» (who may not write
    /// properties at all) and an account that reaches only the property it created get a 403 on a property of the org they do not reach, and nothing is written for a refused call; the printed code of the
    /// old token answers like one that never existed.
    /// </summary>
    [PostgresFact]
    public async Task DoorQr_HostEndpoints_AnswerByTheOrgTheReachAndThePermissionOfTheCaller_Test()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        var (otherOwnerId, otherOrgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var legacyId = await AddAccountWithoutTeamAsync(orgId);
        var casaA = HostScopeScenario.NewProperty(orgId, ownerId, "Casa A");
        var casaB = HostScopeScenario.NewProperty(orgId, ownerId, "Casa B");
        var casaOfLegacy = HostScopeScenario.NewProperty(orgId, legacyId, "Casa creata dall'account");
        var casaOfAnotherOrg = HostScopeScenario.NewProperty(otherOrgId, otherOwnerId, "Casa di un altro host");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Properties.AddRange(casaA, casaB, casaOfLegacy, casaOfAnotherOrg);
            var member = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == collaboratorId);
            member.PropertyScope = PropertyScope.Selected;
            db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = collaboratorId, PropertyId = casaA.Id });
            await db.SaveChangesAsync();
        }

        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        using var collaborator = factory.CreateAuthenticatedClient(collaboratorId);
        using var legacy = factory.CreateAuthenticatedClient(legacyId);
        using var anonymous = factory.CreateClient();

        var created = await owner.GetAsync(DoorQr(casaB.Id));
        var again = await owner.GetAsync(DoorQr(casaB.Id));
        var rotated = await owner.PostAsync(DoorQr(casaB.Id, "/rotate"), null);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var firstUrl = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        Assert.Contains("/checkin/porta/", firstUrl, StringComparison.Ordinal);
        var firstToken = firstUrl[(firstUrl.LastIndexOf('/') + 1)..];
        Assert.True(PropertyDoorTokens.IsWellFormed(firstToken));
        Assert.Equal(firstUrl, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var secondUrl = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        Assert.NotEqual(firstUrl, secondUrl);

        // The printed code of the old token answers like a code that never existed: the one 404 of the door.
        await OrgTeamHttp.AssertProblemAsync(
            await anonymous.PostAsJsonAsync($"/api/public/checkin/door/{firstToken}", new { bookingCode = "ABCDE12345", arrivalDate = "2026-11-20" }),
            HttpStatusCode.NotFound,
            "door_checkin_not_found");

        // A property of another org does not exist for the owner: the same 404 for both calls.
        await OrgTeamHttp.AssertProblemAsync(await owner.GetAsync(DoorQr(casaOfAnotherOrg.Id)), HttpStatusCode.NotFound, "property_not_found");
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync(DoorQr(casaOfAnotherOrg.Id, "/rotate"), null), HttpStatusCode.NotFound, "property_not_found");

        // The collaborator «Solo alcuni» may not write properties: 403 on the property it was given and on the one it was not.
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync(DoorQr(casaA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync(DoorQr(casaB.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PostAsync(DoorQr(casaB.Id, "/rotate"), null)).StatusCode);

        // The account with no team reaches the property it created and no other.
        Assert.Equal(HttpStatusCode.OK, (await legacy.GetAsync(DoorQr(casaOfLegacy.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync(DoorQr(casaB.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.PostAsync(DoorQr(casaB.Id, "/rotate"), null)).StatusCode);

        // Without a token the answer is 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(DoorQr(casaB.Id))).StatusCode);

        // Nothing was written for the refused calls: the code of Casa B is the one the owner replaced, the others have none.
        using var verify = factory.Services.CreateScope();
        var read = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await read.Properties.IgnoreQueryFilters().Where(p => p.Id == casaA.Id || p.Id == casaB.Id || p.Id == casaOfAnotherOrg.Id)
            .ToDictionaryAsync(p => p.Id, p => p.DoorToken);
        Assert.Equal(secondUrl[(secondUrl.LastIndexOf('/') + 1)..], tokens[casaB.Id]);
        Assert.Null(tokens[casaA.Id]);
        Assert.Null(tokens[casaOfAnotherOrg.Id]);
    }

    /// <summary>
    /// Data rule: many first requests for the code of a property at once, each with its own connection, end with one token that all of them answer (the conditional update of the first token and the
    /// unique index), and the same code cannot be put on a second property.
    /// </summary>
    [PostgresFact]
    public async Task DoorToken_ManyFirstRequestsAtOnce_MakeOneToken_AndNoTwoPropertiesShareOne_Test()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var property = HostScopeScenario.NewProperty(orgId, ownerId, "Casa senza codice");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Properties.Add(property);
            await db.SaveChangesAsync();
        }

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IDoorCheckInService>().GetOrCreateTokenAsync(orgId, property.Id);
        }));

        var token = Assert.Single(answers.Distinct(StringComparer.Ordinal))!;
        Assert.True(PropertyDoorTokens.IsWellFormed(token));
        using var verify = factory.Services.CreateScope();
        var read = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(token, await read.Properties.IgnoreQueryFilters().Where(p => p.Id == property.Id).Select(p => p.DoorToken).SingleAsync());

        var other = HostScopeScenario.NewProperty(orgId, ownerId, "Altra casa");
        other.DoorToken = token;
        read.Properties.Add(other);
        await Assert.ThrowsAsync<DbUpdateException>(() => read.SaveChangesAsync());
    }

    /// <summary>
    /// Privacy rule: the guest page reads the legal texts of the registry with the version the consent must carry (a draft, flagged, outside production); a consent for any other string, an older
    /// version or the same version in another case is the 422 <c>biometric_consent_version_unknown</c> and records nothing; the served version is recorded with the hash of its text.
    /// </summary>
    [PostgresFact]
    public async Task LegalTexts_ServeTheVersionTheConsentMustCarry_AndAnyOtherIsTheUnknownVersion422_Test()
    {
        var (token, sessionId) = await SeedSessionAsync();
        using var client = factory.CreateClient();

        var texts = await client.GetFromJsonAsync<JsonElement>($"/api/public/checkin/{token}/legal-texts?lang=de");
        Assert.Equal("de", texts.GetProperty("language").GetString());
        var consent = texts.GetProperty("texts").EnumerateArray().Single(t => t.GetProperty("kind").GetString() == "biometric-consent");
        Assert.Equal(ConsentVersion, consent.GetProperty("version").GetString());
        Assert.True(consent.GetProperty("isDraft").GetBoolean());
        Assert.Equal("de", consent.GetProperty("language").GetString());
        Assert.DoesNotContain("<!--", consent.GetProperty("contentHtml").GetString(), StringComparison.Ordinal);

        foreach (var free in new[] { "TEST-biometric-v1", "checkin-biometric-2026-10", ConsentVersion.ToUpperInvariant() })
        {
            var refused = await client.PostAsJsonAsync($"/api/public/checkin/{token}/biometric-consent", new { consentGiven = true, consentVersion = free });
            await OrgTeamHttp.AssertProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "biometric_consent_version_unknown");
        }

        var untouched = await ReadSessionAsync(sessionId);
        Assert.Null(untouched.BiometricConsentAt);
        Assert.Null(untouched.BiometricConsentVersion);
        Assert.Null(untouched.BiometricConsentTextHash);

        var accepted = await client.PostAsJsonAsync($"/api/public/checkin/{token}/biometric-consent", new { consentGiven = true, consentVersion = ConsentVersion });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("consentRecorded").GetBoolean());
        var recorded = await ReadSessionAsync(sessionId);
        Assert.Equal(ConsentVersion, recorded.BiometricConsentVersion);
        Assert.Equal(64, recorded.BiometricConsentTextHash!.Length);
    }

    /// <summary>The flag off: the door, the QR of the host and the legal texts answer like a route that does not exist, before the authentication and the body.</summary>
    [Fact]
    public async Task DoorAndLegalTexts_FlagOff_AnswerLikeARouteThatDoesNotExist_Test()
    {
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:CheckInIdentityVerification"] = "false" })));
        using var client = off.CreateClient();

        var door = await client.PostAsJsonAsync($"/api/public/checkin/door/{PropertyDoorTokens.New()}", new { bookingCode = "ABCDE12345", arrivalDate = "2026-11-20" });
        var qr = await client.GetAsync(DoorQr(Guid.NewGuid()));
        var rotate = await client.PostAsync(DoorQr(Guid.NewGuid(), "/rotate"), null);
        var texts = await client.GetAsync($"/api/public/checkin/{Guid.NewGuid():N}/legal-texts");

        foreach (var response in new[] { door, qr, rotate, texts })
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }

    private static string DoorQr(Guid propertyId, string suffix = "") => $"/api/properties/{propertyId}/door-qr{suffix}";

    /// <summary>A stay of an org on the Agency plan (the face check is on by default for it) with an open check-in link: the token and the id of its session.</summary>
    private async Task<(string Token, Guid SessionId)> SeedSessionAsync()
    {
        var seed = await factory.SeedConfirmedBookingWithTokenAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId);
        var org = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == booking.OrgId);
        org.PlanTier = PlanTierEnum.Agency;
        await db.SaveChangesAsync();

        var token = await new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance).CreateSessionAsync(seed.BookingId, booking.OrgId);
        var sessionId = await db.GuestCheckInSessions.IgnoreQueryFilters().Where(s => s.BookingId == seed.BookingId).Select(s => s.Id).SingleAsync();
        return (token, sessionId);
    }

    private async Task<GuestCheckInSession> ReadSessionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    /// <summary>A host account of the org with no team row (an account from before the team): it reaches the properties it created.</summary>
    private async Task<string> AddAccountWithoutTeamAsync(Guid orgId)
    {
        var userId = $"auth0|door-host-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Account",
            LastName = "Senza team",
            OrgId = orgId,
            Role = UserRole.None,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IUserContextMembershipService>().GrantAsync(userId, [UserRole.PropertyOwner]);
        return userId;
    }
}
