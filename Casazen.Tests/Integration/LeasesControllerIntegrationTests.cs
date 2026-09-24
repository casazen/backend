using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Casazen.Tests.Integration;

public class LeasesControllerIntegrationTests : IClassFixture<LeaseFlowWebApplicationFactory>
{
    private const string LandlordCf = "RSSMRA80A01H501Z";
    private const string TenantCf = "VRDGLI85B02F205X";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly LeaseFlowWebApplicationFactory _factory;

    public LeasesControllerIntegrationTests(LeaseFlowWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC1_FullFlow_CreateSignOfflineManualRegistrationReceipt_EmitsRequiredEvents()
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
        // LT-04: not signed yet, start 1/9 already passed → deadline 30 days from the start, no stipula.
        Assert.Equal(JsonValueKind.Null, afterCreate.GetProperty("stipulaDate").ValueKind);
        Assert.Equal(new DateTime(2026, 10, 1), afterCreate.GetProperty("registrationDeadline").GetDateTime().Date);

        // LT-02: offline signature, the default path. The final contract comes from the approved template (not a BOZZA).
        var contract = await client.GetAsync($"/api/leases/{leaseId}/contract.pdf");
        Assert.Equal(HttpStatusCode.OK, contract.StatusCode);
        Assert.Equal("application/pdf", contract.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("BOZZA", Encoding.ASCII.GetString(await contract.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);

        var upload = await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var signed = await GetLease(client, leaseId);
        Assert.Equal("Signed", signed.GetProperty("status").GetString());
        // LT-04: stipula 20/8 declared by the landlord, before the start 1/9: deadline 20/8 + 30 = 19/9.
        Assert.Equal(new DateTime(2026, 8, 20), signed.GetProperty("stipulaDate").GetDateTime().Date);
        Assert.Equal(new DateTime(2026, 9, 19), signed.GetProperty("registrationDeadline").GetDateTime().Date);
        // The storage key stays on the server (A7-17): the client only learns that the signed PDF exists.
        Assert.True(signed.GetProperty("hasSignedPdf").GetBoolean());
        Assert.False(signed.TryGetProperty("signedPdfStoragePath", out _));
        Assert.Equal(0, _factory.ESignProvider.Calls);

        // LT-01: the landlord registers on the official channel, then declares number, date and receipt.
        var declared = await DeclareManualAsync(client, leaseId);
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);

        var registered = await GetLease(client, leaseId);
        Assert.Equal("Registered", registered.GetProperty("status").GetString());
        AssertEventSequence(registered, [
            "Created",
            "AllPartiesSigned",
            "RegistrationConfirmed",
        ]);

        var receipt = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("application/pdf", receipt.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ReceiptPdf, await receipt.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, _factory.RegistrationProvider.CallsFor(leaseId));
    }

    [Fact]
    public async Task GetContractForSignature_TemplateNotApproved_Returns422NoFinalPdfAndLeaseStaysDraft()
    {
        // LT-03 (A7-03), LT-02: RegimeOrdinario keeps the committed default (no approved template) in this factory:
        // no final contract to sign, only the BOZZA preview.
        var owner = UniqueOwner("template-422");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var created = await ReadJson(await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id, "RegimeOrdinario")));
        var leaseId = created.GetProperty("id").GetGuid();

        var contract = await client.GetAsync($"/api/leases/{leaseId}/contract.pdf");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, contract.StatusCode);
        Assert.NotEqual("application/pdf", contract.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(contract);
        Assert.Equal("contract_template_not_approved", problem.GetProperty("code").GetString());
        var lease = await GetLease(client, leaseId);
        Assert.Equal("Draft", lease.GetProperty("status").GetString());
        AssertEventSequence(lease, ["Created"]);
    }

    [Fact]
    public async Task GetContractPreview_TemplateNotApproved_ReturnsPdfMarkedBozza()
    {
        var owner = UniqueOwner("template-preview");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var created = await ReadJson(await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id, "RegimeOrdinario")));
        var leaseId = created.GetProperty("id").GetGuid();

        var preview = await client.GetAsync($"/api/leases/{leaseId}/contract/preview");

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("application/pdf", preview.Content.Headers.ContentType?.MediaType);
        var text = Encoding.ASCII.GetString(await preview.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains("(BOZZA - template non approvato)", text, StringComparison.Ordinal);
        Assert.Contains("4 anni", text, StringComparison.Ordinal);
        Assert.Contains("Mario Rossi", text, StringComparison.Ordinal);
        var lease = await GetLease(client, leaseId);
        Assert.Equal("Draft", lease.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetContractPreview_LeaseOfAnotherOrg_Returns404()
    {
        var ownerA = UniqueOwner("preview-a");
        var ownerB = UniqueOwner("preview-b");
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);
        using var clientA = LandlordClient(ownerA);
        var created = await ReadJson(await clientA.PostAsJsonAsync("/api/leases", CreateBody(property.Id)));
        var leaseId = created.GetProperty("id").GetGuid();

        using var clientB = LandlordClient(ownerB);
        var preview = await clientB.GetAsync($"/api/leases/{leaseId}/contract/preview");

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
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

        var contract = await clientB.GetAsync($"/api/leases/{leaseId}/contract.pdf");
        Assert.Equal(HttpStatusCode.NotFound, contract.StatusCode);

        var sign = await LeaseSigningTestClient.UploadSignedContractAsync(clientB, leaseId);
        Assert.Equal(HttpStatusCode.NotFound, sign.StatusCode);
    }

    [Fact]
    public async Task AC8_Receipt_BeforeRegistered_Returns404ReceiptNotAvailable()
    {
        var owner = UniqueOwner("receipt-early");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);

        var response = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("rli_receipt_not_available", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC8_Receipt_WhenRegistered_ReturnsTheDeclaredPdf()
    {
        var owner = UniqueOwner("receipt-ok");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToRegisteredAsync(client, property.Id);

        var response = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ReceiptPdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
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

    [PostgresFact]
    public async Task TriggerRegistration_ProviderFlagOff_Returns404AndProviderNeverCalled()
    {
        // LT-01 (A7-01): with Features:RliProvider off (default) nothing reaches a provider; the manual path works.
        var owner = UniqueOwner("flag-off");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);

        var submit = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration",
            new { tosVersion = "2026-08-rli-delega-bozza", attestationAccepted = true });

        Assert.Equal(HttpStatusCode.NotFound, submit.StatusCode);
        Assert.Equal("not_found", (await ReadJson(submit)).GetProperty("code").GetString());
        var lease = await GetLease(client, leaseId);
        Assert.Equal("Signed", lease.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, lease.GetProperty("registration").ValueKind);
        var checklist = await GetChecklistAsync(client, leaseId);
        Assert.False(checklist.GetProperty("providerFilingAvailable").GetBoolean());
        Assert.DoesNotContain(ChecklistKeys(checklist), key => key == RliChecklistKeys.DelegaCaptured);

        (await DeclareManualAsync(client, leaseId)).EnsureSuccessStatusCode();

        Assert.Equal("Registered", (await GetLease(client, leaseId)).GetProperty("status").GetString());
        Assert.Equal(0, _factory.RegistrationProvider.CallsFor(leaseId));
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_WithPdfReceipt_LeaseRegisteredWithDeclaredDetails()
    {
        var owner = UniqueOwner("manual-ok");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);
        var registrationDate = DateTime.UtcNow.Date.AddDays(-2);

        var response = await DeclareManualAsync(client, leaseId, "  24091234567890123-000001 ", registrationDate);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertNoPartyPiiNorInternalFields(body);
        var registration = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
        Assert.Equal("Registered", registration.GetProperty("status").GetString());
        Assert.Equal("Manual", registration.GetProperty("channel").GetString());
        Assert.Equal("24091234567890123-000001", registration.GetProperty("registrationCode").GetString());
        Assert.Equal(registrationDate, registration.GetProperty("registrationDate").GetDateTime().ToUniversalTime());
        Assert.True(registration.GetProperty("hasReceipt").GetBoolean());

        var lease = await GetLease(client, leaseId);
        Assert.Equal("Registered", lease.GetProperty("status").GetString());
        var checklist = await GetChecklistAsync(client, leaseId);
        var registeredItem = ChecklistItem(checklist, RliChecklistKeys.RliRegistered);
        Assert.True(registeredItem.GetProperty("done").GetBoolean());
        Assert.False(registeredItem.GetProperty("failed").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.LeaseRegistrations.AsNoTracking().SingleAsync(r => r.LeaseContractId == leaseId);
        Assert.Equal(owner, stored.DeclaredByUserId);
        Assert.StartsWith("leases/", stored.ReceiptStoragePath);
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        Assert.True(await storage.ExistsAsync(StorageBucket.Private, stored.ReceiptStoragePath!));
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_ReceiptNotAPdf_Returns422AndLeaseStaysSigned()
    {
        var owner = UniqueOwner("manual-not-pdf");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);

        var response = await DeclareManualAsync(
            client, leaseId, receipt: Encoding.ASCII.GetBytes("[RECEIPT PLACEHOLDER]"), fileName: "ricevuta.pdf");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rli_receipt_invalid", (await ReadJson(response)).GetProperty("code").GetString());
        var lease = await GetLease(client, leaseId);
        Assert.Equal("Signed", lease.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, lease.GetProperty("registration").ValueKind);
        Assert.Empty(StoredReceiptsOf(leaseId));
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_DateInTheFuture_Returns422()
    {
        var owner = UniqueOwner("manual-future");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);

        var response = await DeclareManualAsync(client, leaseId, registrationDate: DateTime.UtcNow.Date.AddDays(3));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rli_registration_date_in_future", (await ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal("Signed", (await GetLease(client, leaseId)).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_LeaseNotSigned_Returns422()
    {
        var owner = UniqueOwner("manual-draft");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = (await ReadJson(await client.PostAsJsonAsync("/api/leases", CreateBody(property.Id))))
            .GetProperty("id").GetGuid();

        var response = await DeclareManualAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rli_lease_not_signed", (await ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal("Draft", (await GetLease(client, leaseId)).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_AlreadyRegistered_Returns409AndKeepsTheFirstDeclaration()
    {
        var owner = UniqueOwner("manual-twice");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToRegisteredAsync(client, property.Id);

        var second = await DeclareManualAsync(client, leaseId, "ALTRO-CODICE");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("rli_already_registered", (await ReadJson(second)).GetProperty("code").GetString());
        var registration = (await GetLease(client, leaseId)).GetProperty("registration");
        Assert.Equal(DefaultRegistrationCode, registration.GetProperty("registrationCode").GetString());
        Assert.Single(StoredReceiptsOf(leaseId));
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_MissingFields_Returns400()
    {
        var owner = UniqueOwner("manual-missing");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id);
        using var form = new MultipartFormDataContent { { new StringContent("ABC"), "registrationCode" } };

        var response = await client.PostAsync($"/api/leases/{leaseId}/registration/manual", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Signed", (await GetLease(client, leaseId)).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_LeaseOfAnotherOrg_Returns404AndChangesNothing()
    {
        var ownerA = UniqueOwner("manual-org-a");
        var ownerB = UniqueOwner("manual-org-b");
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);
        using var clientA = LandlordClient(ownerA);
        var leaseId = await DriveToSignedAsync(clientA, property.Id);

        using var clientB = LandlordClient(ownerB);
        var response = await DeclareManualAsync(clientB, leaseId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Signed", (await GetLease(clientA, leaseId)).GetProperty("status").GetString());
        Assert.Empty(StoredReceiptsOf(leaseId));
    }

    [PostgresFact]
    public async Task DeclareManualRegistration_ColleagueOfSameOrgNotOwner_Returns403()
    {
        var owner = UniqueOwner("manual-owner");
        var colleague = UniqueOwner("manual-colleague");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);
        using var ownerClient = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(ownerClient, property.Id);

        using var colleagueClient = LandlordClient(colleague);
        var response = await DeclareManualAsync(colleagueClient, leaseId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Signed", (await GetLease(ownerClient, leaseId)).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task GetReceipt_OnlyWithAuthorization_OwnerGetsItOthersDoNot()
    {
        // FD-07: the receipt lives in the private bucket and is served only to a caller who may read the lease.
        var owner = UniqueOwner("receipt-auth");
        var colleague = UniqueOwner("receipt-colleague");
        var otherOrgOwner = UniqueOwner("receipt-other-org");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);
        await _factory.SeedOrgForOwnerAsync(otherOrgOwner);
        using var ownerClient = LandlordClient(owner);
        var leaseId = await DriveToRegisteredAsync(ownerClient, property.Id);
        var path = $"/api/leases/{leaseId}/registration/receipt";

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        using var otherOrgClient = LandlordClient(otherOrgOwner);
        var otherOrg = await otherOrgClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, otherOrg.StatusCode);
        Assert.DoesNotContain("%PDF", await otherOrg.Content.ReadAsStringAsync());

        using var colleagueClient = LandlordClient(colleague);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleagueClient.GetAsync(path)).StatusCode);

        using var noLeaseRole = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        Assert.Equal(HttpStatusCode.Forbidden, (await noLeaseRole.GetAsync(path)).StatusCode);

        var ok = await ownerClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(ReceiptPdf, await ok.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsRliAndESignProvidersOff()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var features = await ReadJson(response);
        Assert.False(features.GetProperty("rliProvider").GetBoolean());
        Assert.False(features.GetProperty("eSignProvider").GetBoolean());
    }

    private static void AssertNoPartyPiiNorInternalFields(string json)
    {
        foreach (var secret in new[] { LandlordCf, TenantCf, "mario@example.com", "giulia@example.com", "auth0|lease-", "/signed/", "signed-contract/", "stub-session-", "fake-session-", "\"secret-checklist\"" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        foreach (var field in new[] { "ownerId", "orgId", "safetyChecklistJson", "fiscalCode", "contactEmail", "citizenship", "payload", "signedPdfStoragePath", "externalSigningSessionId", "externalRegistrationId", "receiptStoragePath", "declaredByUserId", "stipulaDeclaredByUserId", "dataRetentionUntil" })
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

    private const string DefaultRegistrationCode = "24091234567890123-000001";

    /// <summary>A minimal file with the PDF signature: the API checks the content, not the declared type.</summary>
    private static readonly byte[] ReceiptPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% ricevuta RLI di test\n%%EOF\n");

    private static async Task<HttpResponseMessage> DeclareManualAsync(
        HttpClient client,
        Guid leaseId,
        string registrationCode = DefaultRegistrationCode,
        DateTime? registrationDate = null,
        byte[]? receipt = null,
        string fileName = "ricevuta-rli.pdf")
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(registrationCode), "registrationCode" },
            {
                new StringContent((registrationDate ?? DateTime.UtcNow.Date.AddDays(-1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                "registrationDate"
            },
        };
        var file = new ByteArrayContent(receipt ?? ReceiptPdf);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "receipt", fileName);
        return await client.PostAsync($"/api/leases/{leaseId}/registration/manual", form);
    }

    private static async Task<JsonElement> GetChecklistAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}/rli/checklist");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static IEnumerable<string?> ChecklistKeys(JsonElement checklist) =>
        checklist.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();

    private static JsonElement ChecklistItem(JsonElement checklist, string key) =>
        checklist.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    /// <summary>Receipt files of the lease in the private bucket of the test storage.</summary>
    private string[] StoredReceiptsOf(Guid leaseId)
    {
        // Only the registration folder: the signed contract of the lease lives next to it (LT-02).
        var folder = Directory.EnumerateDirectories(_factory.StorageRoot, leaseId.ToString(), SearchOption.AllDirectories)
            .Select(d => Path.Combine(d, "registration"))
            .FirstOrDefault(Directory.Exists);
        return folder is null ? [] : Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
    }

    private HttpClient LandlordClient(string ownerId)
        => _factory.CreateAuthenticatedClient(ownerId, "LongTermLandlord");

    private static string UniqueOwner(string suffix) => $"auth0|lease-{suffix}-{Guid.NewGuid():N}";

    private static object CreateBody(Guid propertyId, string fiscalRegime = "CedolareSecca") => new
    {
        propertyId,
        fiscalRegime,
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

    private static async Task<Guid> DriveToSignedAsync(HttpClient client, Guid propertyId)
    {
        var created = await ReadJson(await client.PostAsJsonAsync("/api/leases", CreateBody(propertyId)));
        var leaseId = created.GetProperty("id").GetGuid();
        (await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId)).EnsureSuccessStatusCode();

        var signed = await GetLease(client, leaseId);
        Assert.Equal("Signed", signed.GetProperty("status").GetString());
        // LT-04: stipula 20/8 declared with the signed contract, before the start 1/9: deadline 19/9.
        Assert.Equal(new DateTime(2026, 9, 19), signed.GetProperty("registrationDeadline").GetDateTime().Date);
        return leaseId;
    }

    private static async Task<Guid> DriveToRegisteredAsync(HttpClient client, Guid propertyId)
    {
        var leaseId = await DriveToSignedAsync(client, propertyId);
        (await DeclareManualAsync(client, leaseId)).EnsureSuccessStatusCode();
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
