using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class LeasesControllerIntegrationTests : IClassFixture<LeaseFlowWebApplicationFactory>
{
    private const string TosVersion = "2026-08-rli-delega-bozza";
    private const string LandlordCf = "RSSMRA80A01H501Z";
    private const string TenantCf = "VRDGLI85B02F205X";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly LeaseFlowWebApplicationFactory _factory;

    public LeasesControllerIntegrationTests(LeaseFlowWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC1_FullFlow_CreateSignRegisterReceipt_EmitsRequiredEvents()
    {
        var owner = UniqueOwner("flow");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);

        var create = await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadJson(create);
        var leaseId = created.GetProperty("id").GetGuid();
        Assert.Equal("Draft", created.GetProperty("status").GetString());

        var afterCreate = await GetLease(client, leaseId);
        Assert.Equal("Draft", afterCreate.GetProperty("status").GetString());

        var signing = await client.PostAsync($"/api/leases/{leaseId}/signing", null);
        Assert.Equal(HttpStatusCode.OK, signing.StatusCode);
        var signingBody = await ReadJson(signing);
        Assert.Equal("AwaitingSignature", signingBody.GetProperty("status").GetString());
        Assert.True(signingBody.GetProperty("signers").GetArrayLength() >= 2);

        var afterSign = await GetLease(client, leaseId);
        Assert.Equal("AwaitingSignature", afterSign.GetProperty("status").GetString());

        var payload = JsonSerializer.Serialize(new
        {
            externalSessionId = $"stub-session-{leaseId}",
            eventType = "all_signed",
            allSigned = true,
            signedDocumentPath = "/signed/lease.pdf",
        });
        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<ESignWebhookJob>();
            await job.ProcessEventAsync(payload);
        }

        var signed = await GetLease(client, leaseId);
        Assert.Equal("Signed", signed.GetProperty("status").GetString());
        Assert.Equal("/signed/lease.pdf", signed.GetProperty("signedPdfStoragePath").GetString());

        var register = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = true });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<LeaseRegistrationStatusPollingJob>();
            await job.ExecuteAsync();
        }

        var registered = await GetLease(client, leaseId);
        Assert.Equal("Registered", registered.GetProperty("status").GetString());
        AssertEventSequence(registered, [
            "Created",
            "SigningInitiated",
            "AllPartiesSigned",
            "RegistrationSubmitted",
            "RegistrationConfirmed",
        ]);

        var receipt = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("application/pdf", receipt.Content.Headers.ContentType?.MediaType);
        var pdf = await receipt.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(pdf);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..Math.Min(4, pdf.Length)]));
    }

    [Fact]
    public async Task AC2_WithoutLongTermLandlord_Returns403()
    {
        var owner = UniqueOwner("rbac-role");
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync("/api/leases");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AC2_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/leases");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC2_ReadOnlyMembership_CannotCreateSignOrRegister()
    {
        var owner = UniqueOwner("rbac-perm");
        var property = await _factory.SeedPropertyAsync(owner);
        await SeedReadOnlyLongRentMembershipAsync(owner);

        using var client = LandlordClient(owner);
        var get = await client.GetAsync("/api/leases");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var create = await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task AC3_Create_WithOutOfRangeFiscalRegime_Returns400_AndDoesNotPersist()
    {
        var owner = UniqueOwner("invalid-regime");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);

        var response = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = 999,
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = ValidParties(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoLeasesPersistedAsync(property.Id);
    }

    [Fact]
    public async Task AC3_Create_WithOutOfRangePartyRole_Returns400_AndDoesNotPersistInvalidParty()
    {
        var owner = UniqueOwner("invalid-role");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);

        var response = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = "CedolareSecca",
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = ValidParties().Append(new
            {
                role = 999,
                firstName = "Invalid",
                lastName = "Role",
                fiscalCode = "NVLRLE90B02F205X",
                citizenship = "IT",
                contactEmail = "invalid-role@example.com",
            }).ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoLeasesPersistedAsync(property.Id);
    }

    [Fact]
    public async Task AC2_OtherOwner_GetReturns404_MutationsForbid()
    {
        var ownerA = UniqueOwner("owner-a");
        var ownerB = UniqueOwner("owner-b");
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);

        using var clientA = LandlordClient(ownerA);
        var created = await ReadJson(await clientA.PostAsJsonAsync("/api/leases", CreateBody(property.Id)));
        var leaseId = created.GetProperty("id").GetGuid();

        using var clientB = LandlordClient(ownerB);
        var get = await clientB.GetAsync($"/api/leases/{leaseId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var sign = await clientB.PostAsync($"/api/leases/{leaseId}/signing", null);
        Assert.Equal(HttpStatusCode.NotFound, sign.StatusCode);
    }

    [Fact]
    public async Task AC8_Receipt_BeforeRegistered_Returns404()
    {
        var owner = UniqueOwner("receipt-early");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);
        var submitted = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = true });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        var response = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Receipt is not available yet", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AC8_Receipt_WhenRegistered_ReturnsPdf()
    {
        var owner = UniqueOwner("receipt-ok");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToRegisteredAsync(client, property.Id);

        var response = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AC9_SecondRegistration_Returns400AlreadySubmitted()
    {
        var owner = UniqueOwner("double-reg");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);

        var first = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = true });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = true });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("already been submitted", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AC9_RegistrationWithoutDelega_Returns400_AndDoesNotSubmit()
    {
        var owner = UniqueOwner("no-delega");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var registration = await client.GetAsync($"/api/leases/{leaseId}/registration");
        Assert.Equal(HttpStatusCode.NotFound, registration.StatusCode);
    }

    private HttpClient LandlordClient(string ownerId)
        => _factory.CreateAuthenticatedClient(ownerId, "LongTermLandlord");

    private static string UniqueOwner(string suffix) => $"auth0|lease-{suffix}-{Guid.NewGuid():N}";

    private static object CreateBody(Guid propertyId) => new
    {
        propertyId,
        fiscalRegime = "CedolareSecca",
        startDate = "2026-09-01T00:00:00Z",
        endDate = "2030-08-31T00:00:00Z",
        monthlyRent = 1200m,
        parties = ValidParties(),
    };

    private static object[] ValidParties() =>
    [
        new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = LandlordCf, citizenship = "IT", contactEmail = "mario@example.com" },
        new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = TenantCf, citizenship = "IT", contactEmail = "giulia@example.com" },
    ];

    private async Task<Guid> DriveToSignedAsync(HttpClient client, Guid propertyId)
    {
        var created = await ReadJson(await client.PostAsJsonAsync("/api/leases", CreateBody(propertyId)));
        var leaseId = created.GetProperty("id").GetGuid();
        var signing = await client.PostAsync($"/api/leases/{leaseId}/signing", null);
        signing.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Serialize(new
        {
            externalSessionId = $"stub-session-{leaseId}",
            eventType = "all_signed",
            allSigned = true,
            signedDocumentPath = "/signed/lease.pdf",
        });
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ESignWebhookJob>().ProcessEventAsync(payload);
        var signed = await GetLease(client, leaseId);
        Assert.Equal("Signed", signed.GetProperty("status").GetString());
        return leaseId;
    }

    private async Task<Guid> DriveToRegisteredAsync(HttpClient client, Guid propertyId)
    {
        var leaseId = await DriveToSignedAsync(client, propertyId);
        var register = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = true });
        register.EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LeaseRegistrationStatusPollingJob>().ExecuteAsync();
        return leaseId;
    }

    private static async Task<JsonElement> GetLease(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private async Task AssertNoLeasesPersistedAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.LeaseContracts.CountAsync(l => l.PropertyId == propertyId));
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }

    private static void AssertEventSequence(JsonElement lease, string[] required)
    {
        var types = lease.GetProperty("events")
            .EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString())
            .Where(t => t is not null)
            .Cast<string>()
            .ToList();

        var index = 0;
        foreach (var requiredType in required)
        {
            var found = types.FindIndex(index, t => t == requiredType);
            Assert.True(found >= 0, $"Missing event {requiredType} after index {index}. Actual: {string.Join(", ", types)}");
            index = found + 1;
        }
    }

    private async Task SeedReadOnlyLongRentMembershipAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.AppContexts.AnyAsync(c => c.Key == "long-rent"))
        {
            db.AppContexts.Add(new Casazen.Core.Entities.AppContext
            {
                Key = "long-rent",
                DisplayName = "Affitti lungo termine",
            });
        }

        var roleId = Random.Shared.Next(20_000, 1_000_000);
        db.Roles.Add(new Role { Id = roleId, ContextKey = "long-rent", RoleKey = $"lease_read_only_{roleId}" });
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionKey = "lease.read" });
        db.UserContextMemberships.Add(new UserContextMembership
        {
            UserId = userId,
            ContextKey = "long-rent",
            RoleId = roleId,
        });
        await db.SaveChangesAsync();
    }
}
