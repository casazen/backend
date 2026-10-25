using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.CfSimulator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CF-SIM: with the real service (<c>ConcordatoFacile:FormMode</c> not set, the default and the only mode of production) the endpoints of the
/// simulated module answer 404 like a route that does not exist, so nothing of it can be reached; nothing is written either.
/// </summary>
public class CfSimulatorRealModeTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Theory]
    [InlineData("GET", "/api/cf-simulator/link?partner=casazen")]
    [InlineData("POST", "/api/cf-simulator/practices")]
    [InlineData("POST", "/api/cf-simulator/practices/P-0123456789abcdef/status")]
    [InlineData("GET", "/api/cf-simulator/practices/P-0123456789abcdef/documents/contract?controlToken=x")]
    public async Task SimulatorEndpoint_RealMode_Returns404(string method, string path)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request(method, path));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }

    [Fact]
    public void StatusClient_RealMode_IsTheHttpClientOfTheRealStatusApi()
    {
        using var scope = factory.Services.CreateScope();

        Assert.IsType<ConcordatoFacileApiClient>(scope.ServiceProvider.GetRequiredService<IConcordatoFacileApiClient>());
    }
}

/// <summary>
/// CF-SIM with the simulated module on: a link signed as ConcordatoFacile describes (HMAC-SHA256 of the lease and the expiry with the link
/// secret) binds a practice to its contract and a link that is not good does not; the state of a practice and its documents answer only to
/// the control token that its creation returned; the status client of the test environment reads the practices back with the shapes of the
/// status API.
/// </summary>
public class CfSimulatorEndpointsTests(CfSimulatorFactory factory) : IClassFixture<CfSimulatorFactory>
{
    [Fact]
    public async Task SignedLink_BindsThePracticeToTheLease_ALinkThatIsNotGoodDoesNot_AndASecondPracticeClosesTheFirst()
    {
        var leaseId = await SeedLeaseAsync();
        using var client = factory.CreateClient();

        var good = await ReadAsync(await client.GetAsync($"/api/cf-simulator/link?{Query(leaseId)}"));
        Assert.True(good.GetProperty("valid").GetBoolean());
        Assert.Equal(leaseId, good.GetProperty("leaseId").GetGuid());
        Assert.Equal("Seveso", good.GetProperty("comune").GetString());

        var tampered = await ReadAsync(await client.GetAsync($"/api/cf-simulator/link?{Query(leaseId, tampered: true)}"));
        Assert.False(tampered.GetProperty("valid").GetBoolean());
        Assert.Equal("signature_invalid", tampered.GetProperty("reason").GetString());
        Assert.True(!tampered.TryGetProperty("leaseId", out var noLease) || noLease.ValueKind == JsonValueKind.Null);
        Assert.Empty(tampered.GetProperty("practices").EnumerateArray());

        var first = await CreateAsync(client, Query(leaseId));
        Assert.True(first.GetProperty("linked").GetBoolean());
        Assert.Equal("Received", first.GetProperty("status").GetString());

        var withoutLease = await CreateAsync(client, Query(leaseId, tampered: true));
        Assert.False(withoutLease.GetProperty("linked").GetBoolean());

        var second = await CreateAsync(client, Query(leaseId));

        // Only the practices born with a good link are the contract's: the second is received, the first is closed as its duplicate.
        var listed = await ReadAsync(await client.GetAsync($"/api/cf-simulator/link?{Query(leaseId)}"));
        var practices = listed.GetProperty("practices").EnumerateArray().ToList();
        Assert.Equal(2, practices.Count);
        Assert.Equal(second.GetProperty("practiceId").GetString(), practices[0].GetProperty("practiceId").GetString());
        Assert.Equal("Received", practices[0].GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("practiceId").GetString(), practices[1].GetProperty("practiceId").GetString());
        Assert.Equal("ClosedWithoutAttestation", practices[1].GetProperty("status").GetString());
        Assert.Equal("duplicate", practices[1].GetProperty("closeReason").GetString());
    }

    [Fact]
    public async Task PracticeStateAndDocuments_AnswerOnlyToTheControlToken()
    {
        var leaseId = await SeedLeaseAsync();
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, Query(leaseId));
        var practiceId = created.GetProperty("practiceId").GetString()!;
        var token = created.GetProperty("controlToken").GetString()!;

        // A wrong token changes nothing and does not say whether the practice exists.
        var refusedStatus = await PostStatusAsync(client, practiceId, "not-the-token", "Attested");
        var refusedDocument = await client.GetAsync($"/api/cf-simulator/practices/{practiceId}/documents/contract?controlToken=not-the-token");
        var noToken = await client.GetAsync($"/api/cf-simulator/practices/{practiceId}/documents/contract");
        Assert.Equal(HttpStatusCode.NotFound, refusedStatus.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refusedDocument.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noToken.StatusCode);
        var unchanged = await ReadAsync(await client.GetAsync($"/api/cf-simulator/link?{Query(leaseId)}"));
        Assert.Equal("Received", unchanged.GetProperty("practices")[0].GetProperty("status").GetString());

        var closed = await ReadAsync(await PostStatusAsync(client, practiceId, token, "ClosedWithoutAttestation", "not_compliant"));
        Assert.Equal("ClosedWithoutAttestation", closed.GetProperty("status").GetString());
        Assert.Equal("not_compliant", closed.GetProperty("closeReason").GetString());

        foreach (var kind in new[] { "contract", "attestation", "receipt" })
        {
            var pdf = await client.GetAsync($"/api/cf-simulator/practices/{practiceId}/documents/{kind}?controlToken={token}");

            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            var bytes = await pdf.Content.ReadAsByteArrayAsync();
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StatusClient_SimulatedMode_ReadsThePracticesOfTheModuleWithTheShapesOfTheStatusApi()
    {
        var leaseId = await SeedLeaseAsync();
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, Query(leaseId));
        var practiceId = created.GetProperty("practiceId").GetString()!;
        await ReadAsync(await PostStatusAsync(client, practiceId, created.GetProperty("controlToken").GetString()!, "AwaitingUser"));

        using var scope = factory.Services.CreateScope();
        var statusClient = scope.ServiceProvider.GetRequiredService<IConcordatoFacileApiClient>();
        Assert.IsType<SimulatedConcordatoFacileApiClient>(statusClient);

        // The list of a contract: its practices, with no lease on them (as the real list is); the municipality did not change.
        var listed = Assert.Single(await statusClient.GetPracticesByLeaseIdAsync(leaseId));
        Assert.Equal(practiceId, listed.PracticeId);
        Assert.Equal(CfPracticeStatus.AwaitingUser, listed.Status);
        Assert.Equal("Seveso", listed.Comune);
        Assert.False(listed.ComuneDiffersFromLink);
        Assert.Null(listed.CloseReason);
        Assert.Null(listed.LeaseId);

        // A practice by its number (in capitals too), with the lease; a number it does not know is null.
        var single = await statusClient.GetPracticeAsync("P-" + practiceId[2..].ToUpperInvariant());
        Assert.Equal(leaseId, single!.LeaseId);
        Assert.Null(await statusClient.GetPracticeAsync("P-0000000000000000"));
        Assert.Empty(await statusClient.GetPracticesByLeaseIdAsync(Guid.NewGuid()));
    }

    /// <summary>A lease on a property of the test database: the simulated documents speak about the house of the lease.</summary>
    private async Task<Guid> SeedLeaseAsync()
    {
        var property = await factory.SeedPropertyAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lease = new LeaseContract { OrgId = property.OrgId, PropertyId = property.Id, CreatedAt = DateTime.UtcNow };
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();
        return lease.Id;
    }

    /// <summary>
    /// The parameters of the link as the specification v0.2 §2 describes them, signed here with the formula of the specification
    /// (not with the code of the application): hex(HMAC-SHA256(secret, leaseId + "." + expiresAt)), expiry 15 minutes ahead.
    /// </summary>
    private static string Query(Guid leaseId, bool tampered = false)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(15).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(CfSimulatorFactory.LinkSecret),
            Encoding.UTF8.GetBytes($"{leaseId}.{expiresAt}"))).ToLowerInvariant();
        if (tampered)
            signature = (signature[0] == '0' ? '1' : '0') + signature[1..];

        return $"partner=casazen&leaseId={leaseId}&expiresAt={Uri.EscapeDataString(expiresAt)}&signature={signature}&comune=Seveso&provincia=MB";
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string query) =>
        await ReadAsync(await client.PostAsync($"/api/cf-simulator/practices?{query}", content: null), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> PostStatusAsync(
        HttpClient client, string practiceId, string controlToken, string status, string? closeReason = null) =>
        client.PostAsJsonAsync($"/api/cf-simulator/practices/{practiceId}/status", new { controlToken, status, closeReason });

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
