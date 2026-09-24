using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
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
        // The storage path stays on the server (A7-17): the client only learns that the signed PDF exists.
        Assert.True(signed.GetProperty("hasSignedPdf").GetBoolean());
        Assert.False(signed.TryGetProperty("signedPdfStoragePath", out _));

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

    [Fact]
    public async Task GetRegistration_ColleagueOfSameOrgNotOwner_Returns403ForbiddenProblem()
    {
        var owner = UniqueOwner("reg-owner");
        var colleague = UniqueOwner("reg-colleague");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);

        using var ownerClient = LandlordClient(owner);
        var created = await ReadJson(await ownerClient.PostAsJsonAsync("/api/leases", CreateBody(property.Id)));
        var leaseId = created.GetProperty("id").GetGuid();

        using var colleagueClient = LandlordClient(colleague);
        var response = await colleagueClient.GetAsync($"/api/leases/{leaseId}/registration");

        // Authenticated but not the lease owner: 403 (a 401 would make the frontend log the user out).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("forbidden", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("does not belong", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetRegistration_UnknownLease_Returns404LeaseNotFoundProblem()
    {
        var owner = UniqueOwner("reg-missing");
        await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);

        var response = await client.GetAsync($"/api/leases/{Guid.NewGuid()}/registration");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("lease_not_found", problem.GetProperty("code").GetString());
        Assert.Equal("Contratto di locazione non trovato", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetAll_LeaseWithParties_ReturnsSummaryWithoutPersonalDataNorInternalFields()
    {
        var owner = UniqueOwner("list-pii");
        var property = await _factory.SeedPropertyAsync(owner);
        await SetSafetyChecklistAsync(property.Id);
        using var client = LandlordClient(owner);
        (await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id))).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/leases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertNoPartyPiiNorInternalFields(json);
        Assert.DoesNotContain("\"parties\"", json, StringComparison.OrdinalIgnoreCase);
        var row = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts).EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("partyCount").GetInt32());
        Assert.Equal(property.Name, row.GetProperty("property").GetProperty("name").GetString());
        Assert.Equal("Draft", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetAll_ColleagueOfSameOrgNotOwner_DoesNotSeeTheOwnersLeases()
    {
        var owner = UniqueOwner("list-owner");
        var colleague = UniqueOwner("list-colleague");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);
        using var ownerClient = LandlordClient(owner);
        (await ownerClient.PostAsJsonAsync("/api/leases", CreateBody(property.Id))).EnsureSuccessStatusCode();

        using var colleagueClient = LandlordClient(colleague);
        var response = await colleagueClient.GetAsync("/api/leases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await ReadJson(response)).GetArrayLength());
    }

    [Fact]
    public async Task GetById_Lease_ReturnsMaskedPartiesAndTimelineWithoutPayloads()
    {
        var owner = UniqueOwner("detail-pii");
        var property = await _factory.SeedPropertyAsync(owner);
        await SetSafetyChecklistAsync(property.Id);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);
        (await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = TosVersion, attestationAccepted = true })).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/api/leases/{leaseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertNoPartyPiiNorInternalFields(json);
        // RegistrationAuthorized carries the ToS version as payload in the database: never in the response.
        Assert.DoesNotContain(TosVersion, json, StringComparison.Ordinal);
        var lease = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        var landlord = lease.GetProperty("parties").EnumerateArray().Single(p => p.GetProperty("role").GetString() == "Landlord");
        Assert.Equal("Mario", landlord.GetProperty("firstName").GetString());
        Assert.Equal("************501Z", landlord.GetProperty("fiscalCodeMasked").GetString());
        Assert.Equal("m***@example.com", landlord.GetProperty("contactEmailMasked").GetString());
        Assert.Contains(lease.GetProperty("events").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "RegistrationAuthorized");
        Assert.All(lease.GetProperty("events").EnumerateArray(), e => Assert.False(e.TryGetProperty("payload", out _)));
    }

    [Fact]
    public async Task Create_ValidLease_ReturnsDetailDtoWithoutClearPersonalData()
    {
        var owner = UniqueOwner("create-dto");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);

        var response = await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertNoPartyPiiNorInternalFields(json);
        Assert.Contains("\"eventType\":\"Created\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ColleagueOnPropertyOfAnotherOwner_Returns403AndDoesNotPersist()
    {
        var owner = UniqueOwner("create-owner");
        var colleague = UniqueOwner("create-colleague");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);

        using var colleagueClient = LandlordClient(colleague);
        var response = await colleagueClient.PostAsJsonAsync("/api/leases", CreateBody(property.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoLeasesPersistedAsync(property.Id);
    }

    [Fact]
    public async Task Create_PropertyOfAnotherOrg_Returns404PropertyNotFound()
    {
        var ownerA = UniqueOwner("create-org-a");
        var ownerB = UniqueOwner("create-org-b");
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);

        using var clientB = LandlordClient(ownerB);
        var response = await clientB.PostAsJsonAsync("/api/leases", CreateBody(property.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("property_not_found", (await ReadJson(response)).GetProperty("code").GetString());
        await AssertNoLeasesPersistedAsync(property.Id);
    }

    [Fact]
    public async Task GetById_ColleagueOfSameOrgNotOwner_Returns403ForbiddenProblem()
    {
        var owner = UniqueOwner("detail-owner");
        var colleague = UniqueOwner("detail-colleague");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);
        using var ownerClient = LandlordClient(owner);
        var leaseId = (await ReadJson(await ownerClient.PostAsJsonAsync("/api/leases", CreateBody(property.Id))))
            .GetProperty("id").GetGuid();

        using var colleagueClient = LandlordClient(colleague);
        var response = await colleagueClient.GetAsync($"/api/leases/{leaseId}");

        // Visible in the org but not the caller's: 403, which the frontend shows as an access error, not "not found".
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetById_UnknownLease_Returns404LeaseNotFoundProblem()
    {
        var owner = UniqueOwner("detail-missing");
        await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);

        var response = await client.GetAsync($"/api/leases/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("lease_not_found", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("it-IT", "Contratto firmato da tutte le parti")]
    [InlineData("en", "Contract signed by all parties")]
    public async Task GetRliChecklist_AcceptLanguage_ReturnsLocalizedLabels(string language, string expectedLabel)
    {
        var owner = UniqueOwner("checklist-" + language);
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = (await ReadJson(await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id))))
            .GetProperty("id").GetGuid();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/leases/{leaseId}/rli/checklist");
        request.Headers.AcceptLanguage.ParseAdd(language);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await ReadJson(response)).GetProperty("items").EnumerateArray().ToList();
        var signed = items.Single(i => i.GetProperty("key").GetString() == RliChecklistKeys.ContractSigned);
        Assert.Equal(expectedLabel, signed.GetProperty("label").GetString());
        Assert.All(items, i => Assert.NotEqual(i.GetProperty("key").GetString(), i.GetProperty("label").GetString()));
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void RliChecklistLabels_EveryKey_HasATranslation(string culture)
    {
        var localizer = _factory.Services.GetRequiredService<IStringLocalizer<SharedResources>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            foreach (var key in RliChecklistKeys.All)
            {
                var label = localizer[$"RliChecklist_{key}"];
                Assert.False(label.ResourceNotFound, key);
                Assert.False(string.IsNullOrWhiteSpace(label.Value), key);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static void AssertNoPartyPiiNorInternalFields(string json)
    {
        foreach (var secret in new[] { LandlordCf, TenantCf, "mario@example.com", "giulia@example.com", "auth0|lease-", "/signed/", "stub-session-", "\"secret-checklist\"" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        foreach (var field in new[] { "ownerId", "orgId", "safetyChecklistJson", "fiscalCode", "contactEmail", "citizenship", "payload", "signedPdfStoragePath", "externalSigningSessionId", "externalRegistrationId", "receiptStoragePath", "dataRetentionUntil" })
            Assert.DoesNotContain($"\"{field}\":", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SetSafetyChecklistAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
        property.SafetyChecklistJson = "{\"secret-checklist\":true}";
        await db.SaveChangesAsync();
    }

    private async Task AddUserToOrgOfOwnerAsync(string ownerId, string userId)
    {
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = org.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();
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
