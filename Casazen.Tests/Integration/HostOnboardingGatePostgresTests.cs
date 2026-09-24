using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-02 (A1-05) on the real pipeline and PostgreSQL: the host features (short-rent and long-rent contexts, their write
/// permissions, org billing) open only after the onboarding with the Terms, Privacy notice and DPA of the current
/// versions. Before that the API answers 403 <c>onboarding_required</c>, whatever the JWT roles say; admin and supplier
/// areas are not affected, and the onboarding, <c>/users/me</c> and the legal documents stay reachable.
/// </summary>
public class HostOnboardingGatePostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-v1";
    private const string OnboardingRequired = "onboarding_required";

    private readonly CasazenWebApplicationFactory _factory;

    public HostOnboardingGatePostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewSub() => $"auth0|pl02-{Guid.NewGuid():N}";

    public static TheoryData<string, string> HostWrites => new()
    {
        { "POST", "/api/properties" },
        { "POST", "/api/guests" },
        { "POST", "/api/bookings" },
        { "GET", "/api/properties" },
    };

    [PostgresTheory]
    [MemberData(nameof(HostWrites))]
    public async Task HostEndpoint_NewUserWithJwtHostRoleAndNoConsents_Returns403OnboardingRequired(string method, string path)
    {
        // The Auth0 sign-up with a PropertyOwner role, straight to the API or the app: no onboarding, no consents.
        var sub = NewSub();
        using var client = _factory.CreateAuthenticatedClient(sub, roles: "PropertyOwner");

        var response = method == "GET"
            ? await client.GetAsync(path)
            : await client.PostAsJsonAsync(path, BodyFor(path, Guid.NewGuid()));

        await AssertOnboardingRequiredAsync(response);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Orgs.AnyAsync(o => o.Slug.StartsWith($"org-{sub.Replace("|", "-")}")));
        Assert.Null(await db.Users.AsNoTracking().Where(u => u.Id == sub).Select(u => u.OrgId).SingleOrDefaultAsync());
    }

    [PostgresFact]
    public async Task UsersMe_NewUser_IsCreatedWithRoleNoneAndOnboardingRequired()
    {
        var sub = NewSub();
        using var client = _factory.CreateAuthenticatedClient(sub, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("None", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("onboardingRequired").GetBoolean());
        Assert.False(me.GetProperty("consentsAccepted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("orgId").ValueKind);
    }

    [PostgresFact]
    public async Task HostEndpoints_AfterOnboardingWithCurrentConsents_CreatePropertyGuestAndBooking()
    {
        // No JWT role at all (the Auth0 role sync may lag): the DB memberships of the onboarding open the context.
        var sub = NewSub();
        using var client = _factory.CreateAuthenticatedClient(sub);

        var legalDocs = await client.GetAsync("/api/legal/tos");
        Assert.Equal(HttpStatusCode.OK, legalDocs.StatusCode);
        var onboarding = await client.PostAsJsonAsync("/api/users/onboarding", OnboardingPayload(ConsentVersion));
        Assert.Equal(HttpStatusCode.OK, onboarding.StatusCode);

        var me = await (await client.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(me.GetProperty("onboardingRequired").GetBoolean());
        Assert.True(me.GetProperty("consentsAccepted").GetBoolean());
        Assert.Equal("PropertyOwner", me.GetProperty("role").GetString());

        var property = await client.PostAsJsonAsync("/api/properties", BodyFor("/api/properties", Guid.Empty));
        Assert.Equal(HttpStatusCode.Created, property.StatusCode);
        var propertyId = (await property.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var guest = await client.PostAsJsonAsync("/api/guests", BodyFor("/api/guests", propertyId));
        Assert.Equal(HttpStatusCode.Created, guest.StatusCode);

        var booking = await client.PostAsJsonAsync("/api/bookings", BodyFor("/api/bookings", propertyId));
        Assert.True(booking.StatusCode == HttpStatusCode.Created, await booking.Content.ReadAsStringAsync());

        var list = await client.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [PostgresFact]
    public async Task HostEndpoints_ConsentsOfAnOldVersion_Return403UntilTheCurrentOnesAreAccepted()
    {
        var sub = NewSub();
        await SeedOnboardedHostWithConsentsAsync(sub, version: "2026-01-v0");
        using var client = _factory.CreateAuthenticatedClient(sub, roles: "PropertyOwner");

        await AssertOnboardingRequiredAsync(
            await client.PostAsJsonAsync("/api/properties", BodyFor("/api/properties", Guid.Empty)));
        var me = await (await client.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("onboardingRequired").GetBoolean());
        Assert.False(me.GetProperty("consentsAccepted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, me.GetProperty("onboardingCompletedAt").ValueKind);

        // Re-acceptance through the onboarding (same rental type): the host features open again at once.
        var renewal = await client.PostAsJsonAsync("/api/users/onboarding", OnboardingPayload(ConsentVersion));
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);

        var property = await client.PostAsJsonAsync("/api/properties", BodyFor("/api/properties", Guid.Empty));
        Assert.Equal(HttpStatusCode.Created, property.StatusCode);
    }

    [PostgresFact]
    public async Task HostEndpoints_LegacyPropertyOwnerWithOrgButNoOnboarding_Return403OnboardingRequired()
    {
        // Org auto-provisioned before PL-02, DB role PropertyOwner, no onboarding and no consents: the data stays, the
        // host features wait for the onboarding.
        var sub = NewSub();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var org = NewOrg(sub);
            db.Orgs.Add(org);
            db.Users.Add(NewUser(sub, org.Id, UserRole.PropertyOwner));
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(sub);
        await AssertOnboardingRequiredAsync(await client.GetAsync("/api/properties"));
        await AssertOnboardingRequiredAsync(await client.PostAsJsonAsync("/api/guests", BodyFor("/api/guests", Guid.Empty)));

        using var withJwtRole = _factory.CreateAuthenticatedClient(sub, roles: "PropertyOwner");
        await AssertOnboardingRequiredAsync(await withJwtRole.GetAsync("/api/billing/subscription"));
    }

    [PostgresFact]
    public async Task AdminEndpoints_AdminWithoutHostOnboarding_Return200AndHostEndpoints403()
    {
        var sub = NewSub();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(NewUser(sub, orgId: null, UserRole.Admin));
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(sub, roles: "Admin");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users?page=1&pageSize=5")).StatusCode);
        var contexts = await (await client.GetAsync("/api/me/contexts")).Content.ReadFromJsonAsync<JsonElement>();
        var keys = contexts.GetProperty("contexts").EnumerateArray().Select(c => c.GetProperty("contextKey").GetString()).ToList();
        Assert.Contains("admin", keys);
        Assert.DoesNotContain("short-rent", keys);
        // A host org is a host thing: the admin completes the onboarding first, like any host.
        await AssertOnboardingRequiredAsync(await client.GetAsync("/api/billing/subscription"));
    }

    [PostgresFact]
    public async Task SupplierEndpoints_SupplierWithoutHostOnboarding_Return200()
    {
        var sub = NewSub();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var supplierOrg = NewOrg(sub);
            supplierOrg.OrgType = OrgType.Supplier;
            db.Orgs.Add(supplierOrg);
            var user = NewUser(sub, supplierOrg.Id, UserRole.None);
            user.SupplierOrgId = supplierOrg.Id;
            db.Users.Add(user);
            db.SupplierProfiles.Add(new SupplierProfile
            {
                OrgId = supplierOrg.Id,
                Email = user.Email,
                LegalName = "Fornitore PL-02 Srl",
                Phone = "+39 06 000000",
                ComuniJson = "[\"H501\"]",
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(sub, roles: "Supplier");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/supplier/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/service-requests?view=supplier")).StatusCode);
        await AssertOnboardingRequiredAsync(await client.GetAsync("/api/properties"));
    }

    [PostgresFact]
    public async Task RegisterDevice_UserWithoutOrg_Returns403OnboardingRequiredNot401()
    {
        using var client = _factory.CreateAuthenticatedClient(NewSub(), roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/devices", new
        {
            platform = "ios",
            pushToken = $"ExponentPushToken[{Guid.NewGuid():N}]",
            deviceId = Guid.NewGuid().ToString("N"),
        });

        await AssertOnboardingRequiredAsync(response);
    }

    private static async Task AssertOnboardingRequiredAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode} {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(OnboardingRequired, doc.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("detail").GetString()));
    }

    private async Task SeedOnboardedHostWithConsentsAsync(string sub, string version)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = NewOrg(sub);
        db.Orgs.Add(org);
        var user = NewUser(sub, org.Id, UserRole.PropertyOwner);
        user.RentalType = RentalType.ShortTerm;
        user.OnboardingCompletedAt = DateTime.UtcNow.AddMonths(-3);
        db.Users.Add(user);
        foreach (var type in new[] { ConsentType.Tos, ConsentType.Privacy, ConsentType.Dpa, ConsentType.SubprocessorsAck })
        {
            db.ConsentRecords.Add(new ConsentRecord
            {
                UserId = sub,
                OrgId = org.Id,
                Type = type,
                Version = version,
                RecordedAt = DateTime.UtcNow.AddMonths(-3),
            });
        }

        await db.SaveChangesAsync();
    }

    private static Casazen.Core.Entities.Org NewOrg(string sub) => new()
    {
        Name = $"Org {sub}",
        Slug = $"pl02-{Guid.NewGuid():N}",
        DisplayName = $"Org {sub}",
        ContactEmail = "owner@example.com",
        PlanTier = PlanTier.Starter,
        IsActive = true,
    };

    private static User NewUser(string sub, Guid? orgId, UserRole role) => new()
    {
        Id = sub,
        Email = $"{Guid.NewGuid():N}@example.com",
        FirstName = "Gate",
        LastName = "User",
        OrgId = orgId,
        Role = role,
        IsActive = true,
    };

    private static object OnboardingPayload(string version) => new
    {
        rentalType = "ShortTerm",
        consents = new
        {
            tosAccepted = true,
            tosVersion = version,
            privacyAccepted = true,
            privacyVersion = version,
            dpaAccepted = true,
            dpaVersion = version,
            subprocessorsAcknowledged = true,
            subprocessorsVersion = version,
        },
    };

    private static object BodyFor(string path, Guid propertyId)
    {
        var checkIn = new DateTime(TimeProvider.System.TodayInRome().Year + 1, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        return path switch
        {
            "/api/properties" => new
            {
                name = "Casa del gate",
                address = $"Via Consensi {Guid.NewGuid():N}",
                city = "Roma",
                bedrooms = 2,
                bathrooms = 1,
                maxGuests = 4,
                nightlyRate = 90m,
            },
            "/api/guests" => new { firstName = "Gate", lastName = "Ospite", email = $"ospite.{Guid.NewGuid():N}@example.com" },
            _ => new
            {
                propertyId,
                checkInDate = checkIn.ToString("yyyy-MM-dd"),
                checkOutDate = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
                numberOfGuests = 2,
                guest = new
                {
                    firstName = "Mario",
                    lastName = "Rossi",
                    email = $"mario.{Guid.NewGuid():N}@example.com",
                    phone = "+393331234567",
                    country = "Italia",
                },
            },
        };
    }
}
