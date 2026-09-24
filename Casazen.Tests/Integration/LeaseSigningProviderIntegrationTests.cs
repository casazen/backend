using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Fakes;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-02 (A7-16, A7-20) on real PostgreSQL with <c>Features:ESignProvider</c> on and a configured fake provider: the
/// signing links are persisted, the webhook needs a valid HMAC signature, a signer event makes the lease
/// PartiallySigned, the all-signed event makes it Signed with the signed PDF in the private bucket, and a replayed
/// event never moves a Registered lease back to Signed.
/// </summary>
public class LeaseSigningProviderIntegrationTests(LeaseESignProviderFlowWebApplicationFactory factory)
    : IClassFixture<LeaseESignProviderFlowWebApplicationFactory>
{
    private const string WebhookSecret = "esign-test-secret";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [PostgresFact]
    public async Task ESignWebhook_WrongSignature_Returns401AndQueuesNothing()
    {
        factory.BackgroundJobClientMock.Invocations.Clear();
        using var client = factory.CreateClient();
        var body = FakeLeaseESignProvider.EventBody(Guid.NewGuid(), "all_signed");

        var wrongSecret = await client.SendAsync(SignedWebhookRequest(body, "another-secret-of-the-attacker"));
        using var missing = new HttpRequestMessage(HttpMethod.Post, "/webhooks/esign") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var noSignature = await client.SendAsync(missing);
        using var notHex = new HttpRequestMessage(HttpMethod.Post, "/webhooks/esign") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        notHex.Headers.Add("X-ESign-Signature", "not-hex!!");
        var invalidHeader = await client.SendAsync(notHex);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal("invalid_signature", (await ReadJson(wrongSecret)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, noSignature.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidHeader.StatusCode);
        factory.BackgroundJobClientMock.Verify(
            c => c.Create(It.Is<Job>(j => j.Type == typeof(ESignWebhookJob)), It.IsAny<IState>()),
            Times.Never);
    }

    [PostgresFact]
    public async Task ESignWebhook_ValidSignature_Returns200AndQueuesTheJob()
    {
        factory.BackgroundJobClientMock.Invocations.Clear();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(SignedWebhookRequest(FakeLeaseESignProvider.EventBody(Guid.NewGuid(), "all_signed"), WebhookSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j => j.Type == typeof(ESignWebhookJob) && j.Method.Name == nameof(ESignWebhookJob.ProcessEventAsync)),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [PostgresFact]
    public async Task ProviderSigning_LinksPersisted_SignerThenAllSigned_LeasePartiallySignedThenSigned()
    {
        var (client, leaseId) = await DraftLeaseAsync("provider-flow");

        var signing = await client.PostAsync($"/api/leases/{leaseId}/signing", null);

        Assert.Equal(HttpStatusCode.OK, signing.StatusCode);
        var initiated = await ReadJson(signing);
        Assert.Equal("AwaitingSignature", initiated.GetProperty("status").GetString());
        var links = initiated.GetProperty("signers").EnumerateArray().Select(s => s.GetProperty("signingUrl").GetString()).ToList();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.StartsWith("https://esign.invalid/", l, StringComparison.Ordinal));

        // A7-16: after a refresh the links are still there.
        var refreshedState = await ReadJson(await client.GetAsync($"/api/leases/{leaseId}/signers"));
        Assert.True(refreshedState.GetProperty("providerSigningAvailable").GetBoolean());
        var refreshed = refreshedState.GetProperty("signers");
        Assert.Equal(links, refreshed.EnumerateArray().Select(s => s.GetProperty("signingUrl").GetString()).ToList());
        Assert.All(refreshed.EnumerateArray(), s =>
        {
            Assert.Equal("Provider", s.GetProperty("method").GetString());
            Assert.Equal("Pending", s.GetProperty("status").GetString());
            Assert.False(s.GetProperty("signingUrlExpired").GetBoolean());
        });

        var tenantId = refreshed.EnumerateArray().Single(s => s.GetProperty("role").GetString() == "Tenant").GetProperty("partyId").GetGuid();
        await RunWebhookJobAsync(FakeLeaseESignProvider.EventBody(leaseId, "signer_signed", tenantId));

        var partial = await GetLeaseAsync(client, leaseId);
        Assert.Equal("PartiallySigned", partial.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("stipulaDate").ValueKind);
        var afterPartial = (await ReadJson(await client.GetAsync($"/api/leases/{leaseId}/signers"))).GetProperty("signers");
        var tenant = afterPartial.EnumerateArray().Single(s => s.GetProperty("partyId").GetGuid() == tenantId);
        Assert.Equal("Signed", tenant.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, tenant.GetProperty("signingUrl").ValueKind);

        var todayBefore = TimeProvider.System.TodayInRome();
        await RunWebhookJobAsync(FakeLeaseESignProvider.EventBody(leaseId, "all_signed"));

        var signed = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Signed", signed.GetProperty("status").GetString());
        Assert.InRange(signed.GetProperty("stipulaDate").GetDateTime().Date, todayBefore, TimeProvider.System.TodayInRome());
        // Start 1/9 before the stipula: deadline 30 days from the start (LT-04).
        Assert.Equal(new DateTime(2026, 10, 1), signed.GetProperty("registrationDeadline").GetDateTime().Date);
        Assert.True(signed.GetProperty("hasSignedPdf").GetBoolean());
        var download = await client.GetAsync($"/api/leases/{leaseId}/signed-document");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(FakeLeaseESignProvider.SignedPdf, await download.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task ESignWebhook_AllSignedReplayedOnARegisteredLease_StaysRegistered()
    {
        var (client, leaseId) = await DraftLeaseAsync("provider-replay");
        (await client.PostAsync($"/api/leases/{leaseId}/signing", null)).EnsureSuccessStatusCode();
        await RunWebhookJobAsync(FakeLeaseESignProvider.EventBody(leaseId, "all_signed"));
        (await DeclareManualRegistrationAsync(client, leaseId)).EnsureSuccessStatusCode();
        var registered = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Registered", registered.GetProperty("status").GetString());

        // A replayed (or forged but validly signed) event after the registration.
        await RunWebhookJobAsync(FakeLeaseESignProvider.EventBody(leaseId, "all_signed"));
        await RunWebhookJobAsync(FakeLeaseESignProvider.EventBody(leaseId, "signer_signed", Guid.NewGuid()));

        var after = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Registered", after.GetProperty("status").GetString());
        Assert.Equal(registered.GetProperty("stipulaDate").GetDateTime(), after.GetProperty("stipulaDate").GetDateTime());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.LeaseEvents.CountAsync(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.AllPartiesSigned));
    }

    [PostgresFact]
    public async Task ESignWebhook_AllSignedOnADraftLease_ChangesNothing()
    {
        // Only a signature in progress (AwaitingSignature, PartiallySigned) accepts provider events.
        var (client, leaseId) = await DraftLeaseAsync("provider-draft");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lease = await db.LeaseContracts.SingleAsync(l => l.Id == leaseId);
            lease.ExternalSigningSessionId = FakeLeaseESignProvider.SessionIdFor(leaseId);
            await db.SaveChangesAsync();
        }

        await RunWebhookJobAsync(FakeLeaseESignProvider.EventBody(leaseId, "all_signed"));

        var lease2 = await GetLeaseAsync(client, leaseId);
        Assert.Equal("Draft", lease2.GetProperty("status").GetString());
        Assert.False(lease2.GetProperty("hasSignedPdf").GetBoolean());
    }

    private async Task RunWebhookJobAsync(string payload)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ESignWebhookJob>().ProcessEventAsync(payload);
    }

    private async Task<(HttpClient Client, Guid LeaseId)> DraftLeaseAsync(string suffix)
    {
        var owner = $"auth0|lease-{suffix}-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var response = await client.PostAsJsonAsync("/api/leases", new
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
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, (await ReadJson(response)).GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> DeclareManualRegistrationAsync(HttpClient client, Guid leaseId)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("24091234567890123-000009"), "registrationCode" },
            { new StringContent(DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "registrationDate" },
            { new ByteArrayContent(FakeLeaseRegistrationProvider.ReceiptPdf), "receipt", "ricevuta.pdf" },
        };
        return await client.PostAsync($"/api/leases/{leaseId}/registration/manual", form);
    }

    private static HttpRequestMessage SignedWebhookRequest(string payload, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/esign")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        request.Headers.Add("X-ESign-Signature", Convert.ToHexString(hash));
        return request;
    }

    private static async Task<JsonElement> GetLeaseAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);
}
