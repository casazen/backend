using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-02 (A7-02, A7-16; decision D15) on real PostgreSQL, with <c>Features:ESignProvider</c> at its default (off): the
/// contract is signed offline. The landlord downloads the final contract (approved template only, LT-03), uploads the
/// PDF signed by every party with the stipula date, and only then the lease is Signed with its RLI deadline (LT-04) and
/// the RLI registration can start (LT-01). No call ever reaches an e-signature provider.
/// </summary>
public class LeaseSigningIntegrationTests(LeaseFlowWebApplicationFactory factory) : IClassFixture<LeaseFlowWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [PostgresFact]
    public async Task InitiateSigning_ProviderFlagOff_Returns404AndProviderNeverCalled()
    {
        var (client, leaseId) = await DraftLeaseAsync("flag-off");
        var callsBefore = factory.ESignProvider.Calls;

        var signing = await client.PostAsync($"/api/leases/{leaseId}/signing", null);

        Assert.Equal(HttpStatusCode.NotFound, signing.StatusCode);
        Assert.Equal(callsBefore, factory.ESignProvider.Calls);
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Draft", lease.GetProperty("status").GetString());
        Assert.DoesNotContain(EventTypes(lease), e => e == "SigningInitiated");
    }

    [PostgresFact]
    public async Task UploadSignedContract_WithStipula_LeaseSignedWithStipulaDeadlineAndRliCanStart()
    {
        var (client, leaseId) = await DraftLeaseAsync("offline-ok");
        var callsBefore = factory.ESignProvider.Calls;

        var upload = await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId, "2026-08-20");

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var detail = await ReadJson(upload);
        Assert.Equal("Signed", detail.GetProperty("status").GetString());
        Assert.Equal(new DateTime(2026, 8, 20), detail.GetProperty("stipulaDate").GetDateTime().Date);
        // min(stipula 20/8, start 1/9) + 30 days (LT-04).
        Assert.Equal(new DateTime(2026, 9, 19), detail.GetProperty("registrationDeadline").GetDateTime().Date);
        Assert.True(detail.GetProperty("hasSignedPdf").GetBoolean());
        Assert.Contains(EventTypes(detail), e => e == "AllPartiesSigned");
        Assert.Equal(callsBefore, factory.ESignProvider.Calls);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lease = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == leaseId);
            Assert.StartsWith($"leases/{lease.OrgId}/{leaseId}/signed-contract/", lease.SignedPdfStoragePath, StringComparison.Ordinal);
            Assert.StartsWith("auth0|lease-offline-ok-", lease.StipulaDeclaredByUserId, StringComparison.Ordinal);
            var signers = await db.LeaseSigners.AsNoTracking().Where(s => s.LeaseContractId == leaseId).ToListAsync();
            Assert.Equal(2, signers.Count);
            Assert.All(signers, s =>
            {
                Assert.Equal(LeaseSignatureMethod.Offline, s.Method);
                Assert.Equal(LeaseSignerStatus.Signed, s.Status);
            });
            var signedEvent = await db.LeaseEvents.AsNoTracking()
                .SingleAsync(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.AllPartiesSigned);
            Assert.Equal("offline", signedEvent.Payload);
        }

        var download = await client.GetAsync($"/api/leases/{leaseId}/signed-document");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LeaseSigningTestClient.SignedContractPdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString() ?? string.Empty);

        // The RLI flow starts after the signature (LT-01): the checklist ticks the signature, the manual path is open.
        var checklist = await ReadJson(await client.GetAsync($"/api/leases/{leaseId}/rli/checklist"));
        Assert.True(checklist.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == "contract_signed").GetProperty("done").GetBoolean());
        Assert.Equal(new DateTime(2026, 9, 19), checklist.GetProperty("registrationDeadline").GetDateTime().Date);
    }

    [PostgresFact]
    public async Task UploadSignedContract_TemplateNotApproved_Returns422NoFileAndLeaseStaysDraft()
    {
        // LT-03: RegimeOrdinario has no approved template in this factory: no final contract, so no signature either.
        var (client, leaseId) = await DraftLeaseAsync("offline-template", "RegimeOrdinario");

        var upload = await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Equal("contract_template_not_approved", (await ReadJson(upload)).GetProperty("code").GetString());
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Draft", lease.GetProperty("status").GetString());
        Assert.False(lease.GetProperty("hasSignedPdf").GetBoolean());
        Assert.Empty(StoredSignedContractsOf(leaseId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/api/leases/{leaseId}/contract.pdf")).StatusCode);
        var state = await ReadJson(await client.GetAsync($"/api/leases/{leaseId}/signers"));
        Assert.False(state.GetProperty("contractAvailable").GetBoolean());
        Assert.Equal("contract_template_not_approved", state.GetProperty("contractUnavailableCode").GetString());
    }

    [PostgresFact]
    public async Task UploadSignedContract_NotAPdf_Returns422AndLeaseStaysDraft()
    {
        var (client, leaseId) = await DraftLeaseAsync("offline-not-pdf");

        var upload = await LeaseSigningTestClient.UploadSignedContractAsync(
            client, leaseId, content: Encoding.ASCII.GetBytes("PK\u0003\u0004 not a pdf"), fileName: "contratto.pdf");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Equal("lease_signed_contract_invalid", (await ReadJson(upload)).GetProperty("code").GetString());
        Assert.Equal("Draft", (await GetLeaseAsync(client, leaseId)).GetProperty("status").GetString());
        Assert.Empty(StoredSignedContractsOf(leaseId));
    }

    [PostgresFact]
    public async Task UploadSignedContract_StipulaInTheFuture_Returns422()
    {
        var (client, leaseId) = await DraftLeaseAsync("offline-future");
        var tomorrow = TimeProvider.System.TodayInRome().AddDays(2).ToString("yyyy-MM-dd");

        var upload = await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId, tomorrow);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Equal("lease_stipula_date_in_future", (await ReadJson(upload)).GetProperty("code").GetString());
        Assert.Equal("Draft", (await GetLeaseAsync(client, leaseId)).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task UploadSignedContract_AlreadySigned_Returns409AndKeepsTheFirstSignature()
    {
        var (client, leaseId) = await DraftLeaseAsync("offline-twice");
        (await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId, "2026-08-20")).EnsureSuccessStatusCode();

        var second = await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId, "2026-08-25");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("lease_already_signed", (await ReadJson(second)).GetProperty("code").GetString());
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal(new DateTime(2026, 8, 20), lease.GetProperty("stipulaDate").GetDateTime().Date);
        Assert.Single(StoredSignedContractsOf(leaseId));
        // After the signature the contract to sign is no longer generated.
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/leases/{leaseId}/contract.pdf")).StatusCode);
    }

    [PostgresFact]
    public async Task GetSignedDocument_OnlyForTheOwnerOrTheOrg_OthersDoNotGetIt()
    {
        // FD-07 / TN-3: the signed contract lives in the private bucket and is served only to a caller who may read the lease.
        var owner = UniqueUser("signed-auth");
        var colleague = UniqueUser("signed-colleague");
        var manager = UniqueUser("signed-manager");
        var otherOrgOwner = UniqueUser("signed-other-org");
        var property = await factory.SeedPropertyAsync(owner);
        await AddUserToOrgOfOwnerAsync(owner, colleague);
        await AddUserToOrgOfOwnerAsync(owner, manager);
        await factory.SeedOrgForOwnerAsync(otherOrgOwner);
        using var ownerClient = LandlordClient(owner);
        var leaseId = await CreateLeaseAsync(ownerClient, property.Id);
        (await LeaseSigningTestClient.UploadSignedContractAsync(ownerClient, leaseId)).EnsureSuccessStatusCode();
        var path = $"/api/leases/{leaseId}/signed-document";

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        using var otherOrgClient = LandlordClient(otherOrgOwner);
        var otherOrg = await otherOrgClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, otherOrg.StatusCode);
        Assert.DoesNotContain("%PDF", await otherOrg.Content.ReadAsStringAsync());

        using var colleagueClient = LandlordClient(colleague);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleagueClient.GetAsync(path)).StatusCode);

        using var noLeaseRole = factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        Assert.Equal(HttpStatusCode.Forbidden, (await noLeaseRole.GetAsync(path)).StatusCode);

        using var managerClient = factory.CreateAuthenticatedClient(manager, "LongTermLandlord,PropertyManager");
        var orgWide = await managerClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, orgWide.StatusCode);
        Assert.Equal(LeaseSigningTestClient.SignedContractPdf, await orgWide.Content.ReadAsByteArrayAsync());

        var ok = await ownerClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(LeaseSigningTestClient.SignedContractPdf, await ok.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task GetSignedDocument_BeforeTheUpload_Returns404NotAvailable()
    {
        var (client, leaseId) = await DraftLeaseAsync("signed-early");

        var response = await client.GetAsync($"/api/leases/{leaseId}/signed-document");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("lease_signed_contract_not_available", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task DeclareStipula_LegacySignedLeaseWithoutStipula_SetsStipulaAndDeadlineOnce()
    {
        // A lease signed before CasaZen recorded signatures: Signed, no stipula, deadline "to be determined" (LT-04).
        var (client, leaseId) = await DraftLeaseAsync("legacy");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lease = await db.LeaseContracts.SingleAsync(l => l.Id == leaseId);
            lease.Status = LeaseStatus.Signed;
            await db.SaveChangesAsync();
        }

        var before = await GetLeaseAsync(client, leaseId);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("registrationDeadline").ValueKind);

        var declared = await client.PostAsJsonAsync($"/api/leases/{leaseId}/stipula", new { stipulaDate = "2026-08-10" });

        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        var detail = await ReadJson(declared);
        Assert.Equal("Signed", detail.GetProperty("status").GetString());
        Assert.Equal(new DateTime(2026, 8, 10), detail.GetProperty("stipulaDate").GetDateTime().Date);
        Assert.Equal(new DateTime(2026, 9, 9), detail.GetProperty("registrationDeadline").GetDateTime().Date);
        Assert.Contains(EventTypes(detail), e => e == "StipulaDeclared");

        var again = await client.PostAsJsonAsync($"/api/leases/{leaseId}/stipula", new { stipulaDate = "2026-08-12" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("lease_stipula_already_recorded", (await ReadJson(again)).GetProperty("code").GetString());
        Assert.Equal(new DateTime(2026, 8, 10), (await GetLeaseAsync(client, leaseId)).GetProperty("stipulaDate").GetDateTime().Date);
    }

    [PostgresFact]
    public async Task DeclareStipula_DraftLease_Returns422UseTheSignedContract()
    {
        var (client, leaseId) = await DraftLeaseAsync("stipula-draft");

        var declared = await client.PostAsJsonAsync($"/api/leases/{leaseId}/stipula", new { stipulaDate = "2026-08-10" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, declared.StatusCode);
        Assert.Equal("lease_stipula_lease_not_signed", (await ReadJson(declared)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task GetSigners_Offline_PendingOnPaperThenSignedAfterTheUpload()
    {
        var (client, leaseId) = await DraftLeaseAsync("signers");

        var before = await ReadJson(await client.GetAsync($"/api/leases/{leaseId}/signers"));
        // Flag off: no provider path; the approved CedolareSecca template gives a final contract to sign.
        Assert.False(before.GetProperty("providerSigningAvailable").GetBoolean());
        Assert.True(before.GetProperty("contractAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("contractUnavailableCode").ValueKind);
        var pending = before.GetProperty("signers");
        Assert.Equal(2, pending.GetArrayLength());
        Assert.All(pending.EnumerateArray(), s =>
        {
            Assert.Equal("Offline", s.GetProperty("method").GetString());
            Assert.Equal("Pending", s.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, s.GetProperty("signingUrl").ValueKind);
        });

        (await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId, "2026-08-20")).EnsureSuccessStatusCode();

        var after = await ReadJson(await client.GetAsync($"/api/leases/{leaseId}/signers"));
        Assert.False(after.GetProperty("contractAvailable").GetBoolean());
        Assert.Equal("lease_already_signed", after.GetProperty("contractUnavailableCode").GetString());
        var signed = after.GetProperty("signers");
        Assert.All(signed.EnumerateArray(), s =>
        {
            Assert.Equal("Signed", s.GetProperty("status").GetString());
            Assert.Equal(new DateTime(2026, 8, 20), s.GetProperty("signedAt").GetDateTime().Date);
        });
        Assert.DoesNotContain("RSSMRA80A01H501Z", signed.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("mario@example.com", signed.GetRawText(), StringComparison.Ordinal);
    }

    private async Task<(HttpClient Client, Guid LeaseId)> DraftLeaseAsync(string suffix, string fiscalRegime = "CedolareSecca")
    {
        var owner = UniqueUser(suffix);
        var property = await factory.SeedPropertyAsync(owner);
        var client = LandlordClient(owner);
        return (client, await CreateLeaseAsync(client, property.Id, fiscalRegime));
    }

    private static async Task<Guid> CreateLeaseAsync(HttpClient client, Guid propertyId, string fiscalRegime = "CedolareSecca")
    {
        var response = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId,
            fiscalRegime,
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205X", citizenship = "IT", contactEmail = "giulia@example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetGuid();
    }

    private async Task AddUserToOrgOfOwnerAsync(string ownerId, string userId)
    {
        var org = await factory.SeedOrgForOwnerAsync(ownerId);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = org.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        // A colleague who completed the onboarding for the org (PL-02): the checks are about the lease permissions.
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, org.Id, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }

    /// <summary>Signed contracts of the lease in the private bucket of the test storage.</summary>
    private string[] StoredSignedContractsOf(Guid leaseId)
    {
        var folder = Directory.EnumerateDirectories(factory.StorageRoot, "signed-contract", SearchOption.AllDirectories)
            .FirstOrDefault(d => d.Contains(leaseId.ToString(), StringComparison.Ordinal));
        return folder is null ? [] : Directory.GetFiles(folder);
    }

    private HttpClient LandlordClient(string userId) => factory.CreateAuthenticatedClient(userId, "LongTermLandlord");

    private static string UniqueUser(string suffix) => $"auth0|lease-{suffix}-{Guid.NewGuid():N}";

    private static async Task<JsonElement> GetLeaseAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static IEnumerable<string?> EventTypes(JsonElement lease) =>
        lease.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);
}
