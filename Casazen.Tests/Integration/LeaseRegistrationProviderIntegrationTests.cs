using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Fakes;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-01 (A7-01, A7-21): RLI filing through a provider, with <c>Features:RliProvider</c> on and a configured (fake)
/// provider, on real PostgreSQL. A submission is "in progress", never "registered", until the provider returns the
/// receipt; a provider failure leaves a consistent state (registration Failed, lease Signed, no Pending reservation)
/// from which both a retry and the manual declaration work.
/// </summary>
public class LeaseRegistrationProviderIntegrationTests(LeaseProviderFlowWebApplicationFactory factory)
    : IClassFixture<LeaseProviderFlowWebApplicationFactory>
{
    private const string TosVersion = "2026-08-rli-delega-bozza";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private FakeLeaseRegistrationProvider Provider => factory.RegistrationProvider;

    [PostgresFact]
    public async Task TriggerRegistration_ProviderFailsMidway_FailedStateAndRetryAccepted()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-fails");
        Provider.FailNextSubmission(leaseId);

        var failed = await SubmitAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("rli_provider_failed", (await ReadJson(failed)).GetProperty("code").GetString());
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Signed", lease.GetProperty("status").GetString());
        var registration = lease.GetProperty("registration");
        Assert.Equal("Failed", registration.GetProperty("status").GetString());
        Assert.Equal(RliRegistrationFailureCodes.ProviderError, registration.GetProperty("failureCode").GetString());
        Assert.False(registration.GetProperty("hasReceipt").GetBoolean());
        await AssertNoPendingReservationAsync(leaseId);
        Assert.Contains(EventTypes(lease), e => e == "RegistrationFailed");

        // The checklist reports the failure instead of a tick.
        var item = await ChecklistItemAsync(client, leaseId, RliChecklistKeys.RliRegistered);
        Assert.False(item.GetProperty("done").GetBoolean());
        Assert.True(item.GetProperty("failed").GetBoolean());

        // Retry: not blocked by "already submitted" (A7-21).
        var retry = await SubmitAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var afterRetry = await GetLeaseAsync(client, leaseId);
        Assert.Equal("SentToProvider", afterRetry.GetProperty("status").GetString());
        Assert.Equal("SentToProvider", afterRetry.GetProperty("registration").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, afterRetry.GetProperty("registration").GetProperty("failureCode").ValueKind);
        Assert.Equal(2, Provider.CallsFor(leaseId));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.LeaseRegistrations.CountAsync(r => r.LeaseContractId == leaseId));
        Assert.Equal(2, await db.LeaseRegistrationAuthorizations.CountAsync(a => a.LeaseContractId == leaseId));
    }

    [PostgresFact]
    public async Task TriggerRegistration_ProviderFailed_ManualDeclarationStillPossible()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-then-manual");
        Provider.FailNextSubmission(leaseId);
        Assert.Equal(HttpStatusCode.BadGateway, (await SubmitAsync(client, leaseId)).StatusCode);

        var manual = await DeclareManualAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Registered", lease.GetProperty("status").GetString());
        Assert.Equal("Manual", lease.GetProperty("registration").GetProperty("channel").GetString());
        Assert.True((await ChecklistItemAsync(client, leaseId, RliChecklistKeys.RliRegistered)).GetProperty("done").GetBoolean());
    }

    [PostgresFact]
    public async Task TriggerRegistration_Accepted_IsInProgressNeverRegistered()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-accepted");

        var submitted = await SubmitAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("SentToProvider", lease.GetProperty("status").GetString());
        Assert.False((await ChecklistItemAsync(client, leaseId, RliChecklistKeys.RliRegistered)).GetProperty("done").GetBoolean());
        Assert.True((await ChecklistItemAsync(client, leaseId, RliChecklistKeys.DelegaCaptured)).GetProperty("done").GetBoolean());
        var receipt = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        // While the provider works on it the landlord cannot declare a second registration.
        var manual = await DeclareManualAsync(client, leaseId);
        Assert.Equal(HttpStatusCode.Conflict, manual.StatusCode);
        Assert.Equal("rli_registration_in_progress", (await ReadJson(manual)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task TriggerRegistration_Twice_SecondReturns409InProgress()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-twice");
        Assert.Equal(HttpStatusCode.Accepted, (await SubmitAsync(client, leaseId)).StatusCode);

        var second = await SubmitAsync(client, leaseId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("rli_registration_in_progress", (await ReadJson(second)).GetProperty("code").GetString());
        Assert.Equal(1, Provider.CallsFor(leaseId));
    }

    [PostgresFact]
    public async Task TriggerRegistration_ConcurrentRequests_OnlyOneReachesTheProvider()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-race");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SubmitAsync(client, leaseId)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Accepted),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, Provider.CallsFor(leaseId));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.LeaseRegistrationAuthorizations.CountAsync(a => a.LeaseContractId == leaseId));
    }

    [PostgresFact]
    public async Task TriggerRegistration_WithoutDelega_Returns422AndRecordsNothing()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-no-delega");

        var response = await client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration", new { tosVersion = TosVersion, attestationAccepted = false });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rli_delega_required", (await ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal(0, Provider.CallsFor(leaseId));
        var registration = await client.GetAsync($"/api/leases/{leaseId}/registration");
        Assert.Equal(HttpStatusCode.NotFound, registration.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.LeaseRegistrationAuthorizations.CountAsync(a => a.LeaseContractId == leaseId));
    }

    [PostgresFact]
    public async Task TriggerRegistration_FlagOnButProviderNotConfigured_Returns409AndNothingReachesIt()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-unconfigured");
        Provider.IsConfigured = false;
        try
        {
            var response = await SubmitAsync(client, leaseId);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("rli_provider_unavailable", (await ReadJson(response)).GetProperty("code").GetString());
            Assert.False((await GetChecklistAsync(client, leaseId)).GetProperty("providerFilingAvailable").GetBoolean());
        }
        finally
        {
            Provider.IsConfigured = true;
        }

        Assert.Equal(0, Provider.CallsFor(leaseId));
        Assert.Equal("Signed", (await GetLeaseAsync(client, leaseId)).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task PollingJob_ProviderReturnsReceipt_ReceiptStoredAndLeaseRegistered()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-confirmed");
        Assert.Equal(HttpStatusCode.Accepted, (await SubmitAsync(client, leaseId)).StatusCode);
        Provider.SetStatus(leaseId, new ProviderRegistrationStatus(ProviderRegistrationState.Registered));

        await RunPollingJobAsync();

        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Registered", lease.GetProperty("status").GetString());
        var registration = lease.GetProperty("registration");
        Assert.Equal("Registered", registration.GetProperty("status").GetString());
        Assert.Equal("Provider", registration.GetProperty("channel").GetString());
        Assert.True(registration.GetProperty("hasReceipt").GetBoolean());
        var receipt = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal(FakeLeaseRegistrationProvider.ReceiptPdf, await receipt.Content.ReadAsByteArrayAsync());
        Assert.True((await ChecklistItemAsync(client, leaseId, RliChecklistKeys.RliRegistered)).GetProperty("done").GetBoolean());
    }

    [PostgresFact]
    public async Task PollingJob_ProviderRejects_RegistrationFailedAndLeaseSigned()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-rejected");
        Assert.Equal(HttpStatusCode.Accepted, (await SubmitAsync(client, leaseId)).StatusCode);
        Provider.SetStatus(leaseId, new ProviderRegistrationStatus(ProviderRegistrationState.Failed));

        await RunPollingJobAsync();

        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Signed", lease.GetProperty("status").GetString());
        Assert.Equal(RliRegistrationFailureCodes.ProviderRejected,
            lease.GetProperty("registration").GetProperty("failureCode").GetString());
        var item = await ChecklistItemAsync(client, leaseId, RliChecklistKeys.RliRegistered);
        Assert.False(item.GetProperty("done").GetBoolean());
        Assert.True(item.GetProperty("failed").GetBoolean());
    }

    [PostgresFact]
    public async Task PollingJob_ReservationLeftPendingByACrash_FailedWithUnknownOutcome()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-stale");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // The state a crash between the reservation and the provider outcome leaves behind.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lease = await db.LeaseContracts.SingleAsync(l => l.Id == leaseId);
            lease.Status = LeaseStatus.RegistrationPending;
            db.LeaseRegistrations.Add(new LeaseRegistration
            {
                LeaseContractId = leaseId,
                Status = RegistrationStatus.Pending,
                Channel = RegistrationChannel.Provider,
                RequestedAt = DateTime.UtcNow - RliRegistrationService.StaleSubmissionAfter - TimeSpan.FromMinutes(1),
            });
            await db.SaveChangesAsync();
        }

        await RunPollingJobAsync();

        var after = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Signed", after.GetProperty("status").GetString());
        Assert.Equal(RliRegistrationFailureCodes.OutcomeUnknown,
            after.GetProperty("registration").GetProperty("failureCode").GetString());
        await AssertNoPendingReservationAsync(leaseId);
        Assert.Equal(0, Provider.CallsFor(leaseId));
    }

    [PostgresFact]
    public async Task GetById_AfterProviderSubmission_TimelineWithoutPayloadsNorProviderIds()
    {
        var (client, leaseId) = await SignedLeaseAsync("provider-detail");
        Assert.Equal(HttpStatusCode.Accepted, (await SubmitAsync(client, leaseId)).StatusCode);

        var response = await client.GetAsync($"/api/leases/{leaseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        // RegistrationAuthorized carries the ToS version as payload in the database: never in the response (A7-17).
        Assert.DoesNotContain(TosVersion, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeLeaseRegistrationProvider.ExternalIdFor(leaseId), json, StringComparison.Ordinal);
        foreach (var field in new[] { "payload", "externalRegistrationId", "receiptStoragePath", "declaredByUserId" })
            Assert.DoesNotContain($"\"{field}\":", json, StringComparison.OrdinalIgnoreCase);
        var lease = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        Assert.Contains(EventTypes(lease), e => e == "RegistrationAuthorized");
        Assert.Contains(EventTypes(lease), e => e == "RegistrationSubmitted");
    }

    private async Task<(HttpClient Client, Guid LeaseId)> SignedLeaseAsync(string suffix)
    {
        var owner = $"auth0|lease-{suffix}-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var created = await ReadJson(await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = "CedolareSecca",
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205X", citizenship = "IT", contactEmail = "giulia@example.com" },
            },
        }));
        var leaseId = created.GetProperty("id").GetGuid();
        // LT-02: offline signature (the e-sign provider flag stays off in this factory).
        (await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId)).EnsureSuccessStatusCode();

        Assert.Equal("Signed", (await GetLeaseAsync(client, leaseId)).GetProperty("status").GetString());
        return (client, leaseId);
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid leaseId) =>
        client.PostAsJsonAsync(
            $"/api/leases/{leaseId}/registration", new { tosVersion = TosVersion, attestationAccepted = true });

    private static async Task<HttpResponseMessage> DeclareManualAsync(HttpClient client, Guid leaseId)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("24091234567890123-000002"), "registrationCode" },
            { new StringContent(TimeProvider.System.TodayInRome().AddDays(-1).ToString("yyyy-MM-dd")), "registrationDate" },
            { new ByteArrayContent(FakeLeaseRegistrationProvider.ReceiptPdf), "receipt", "ricevuta.pdf" },
        };
        return await client.PostAsync($"/api/leases/{leaseId}/registration/manual", form);
    }

    private async Task RunPollingJobAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LeaseRegistrationStatusPollingJob>().ExecuteAsync();
    }

    private async Task AssertNoPendingReservationAsync(Guid leaseId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.LeaseRegistrations.AnyAsync(
            r => r.LeaseContractId == leaseId && r.Status == RegistrationStatus.Pending));
    }

    private static async Task<JsonElement> GetLeaseAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static async Task<JsonElement> GetChecklistAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}/rli/checklist");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ChecklistItemAsync(HttpClient client, Guid leaseId, string key) =>
        (await GetChecklistAsync(client, leaseId)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == key);

    private static IEnumerable<string?> EventTypes(JsonElement lease) =>
        lease.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);
}
