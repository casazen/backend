using System.Net;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CF-FIX2: the real <see cref="ConcordatoFacileApiClient"/> reads the bodies of the ConcordatoFacile status API v0.2, using the examples
/// of its specification. No test had ever deserialized them (the tests of the services simulate the client), and the client read a bare
/// array where the API sends an object, and a boolean where it sends <c>null</c>: with the real key every status read would have failed.
/// </summary>
public class ConcordatoFacileApiClientTests
{
    private const string LeaseId = "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11";
    private const string PracticeId = "P-0123456789abcdef";

    /// <summary>The example of <c>GET /v1/leases/{leaseId}/practices</c> in §4 of the specification, as it is.</summary>
    private const string SpecLeasePractices = """
        {
          "leaseId": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
          "practices": [
            {
              "practiceId": "P-0123456789abcdef",
              "status": "AwaitingUser",
              "statusChangedAt": "2026-11-03T10:15:00Z",
              "submittedAt": "2026-11-02T18:40:12Z",
              "comune": "Monza",
              "comuneDiffersFromLink": false,
              "closeReason": null
            }
          ]
        }
        """;

    [Fact]
    public async Task GetPracticesByLeaseId_SpecExample_IsReadAsAnObjectWithItsPractices()
    {
        var handler = new StubHandler(SpecLeasePractices);

        var practices = await Client(handler).GetPracticesByLeaseIdAsync(Guid.Parse(LeaseId));

        Assert.Equal($"/v1/leases/{LeaseId}/practices", Assert.Single(handler.Requests).AbsolutePath);
        var practice = Assert.Single(practices);
        Assert.Equal(PracticeId, practice.PracticeId);
        Assert.Equal(CfPracticeStatus.AwaitingUser, practice.Status);
        Assert.Equal(new DateTime(2026, 11, 3, 10, 15, 0, DateTimeKind.Utc), practice.StatusChangedAt);
        Assert.Equal<DateTime?>(new DateTime(2026, 11, 2, 18, 40, 12, DateTimeKind.Utc), practice.SubmittedAt);
        Assert.Equal("Monza", practice.Comune);
        Assert.False(practice.ComuneDiffersFromLink);
        Assert.Null(practice.CloseReason);
    }

    /// <summary>A lease with no practices (or that CF does not know, or is not CasaZen's) is a 200 with an empty list, not an error.</summary>
    [Theory]
    [InlineData("""{ "leaseId": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11", "practices": [] }""")]
    [InlineData("""{ "leaseId": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11" }""")]
    public async Task GetPracticesByLeaseId_NoPractices_IsAnEmptyList(string body)
    {
        var practices = await Client(new StubHandler(body)).GetPracticesByLeaseIdAsync(Guid.Parse(LeaseId));

        Assert.Empty(practices);
    }

    /// <summary>
    /// <c>comuneDiffersFromLink</c> is null when the link named no comune or the practice is not linked: it must not make the read fail.
    /// </summary>
    [Fact]
    public async Task GetPracticesByLeaseId_ComuneDiffersFromLinkNull_IsRead()
    {
        var handler = new StubHandler("""
            {
              "leaseId": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
              "practices": [
                {
                  "practiceId": "P-0123456789abcdef",
                  "status": "Received",
                  "statusChangedAt": "2026-11-02T18:40:15Z",
                  "submittedAt": "2026-11-02T18:40:12Z",
                  "comune": "Reggio nell'Emilia",
                  "comuneDiffersFromLink": null,
                  "closeReason": null
                }
              ]
            }
            """);

        var practice = Assert.Single(await Client(handler).GetPracticesByLeaseIdAsync(Guid.Parse(LeaseId)));

        Assert.Null(practice.ComuneDiffersFromLink);
        Assert.Equal("Reggio nell'Emilia", practice.Comune);
        Assert.Equal(CfPracticeStatus.Received, practice.Status);
    }

    /// <summary>Every status of §5 of the specification is known; one added later is read as Unknown, never as an error.</summary>
    [Theory]
    [InlineData("Received", CfPracticeStatus.Received)]
    [InlineData("InProgress", CfPracticeStatus.InProgress)]
    [InlineData("AwaitingUser", CfPracticeStatus.AwaitingUser)]
    [InlineData("DraftSent", CfPracticeStatus.DraftSent)]
    [InlineData("SignedReceived", CfPracticeStatus.SignedReceived)]
    [InlineData("SentToAssociation", CfPracticeStatus.SentToAssociation)]
    [InlineData("Attested", CfPracticeStatus.Attested)]
    [InlineData("Registered", CfPracticeStatus.Registered)]
    [InlineData("ClosedWithoutAttestation", CfPracticeStatus.ClosedWithoutAttestation)]
    [InlineData("Suspended", CfPracticeStatus.Suspended)]
    [InlineData("SomethingAddedLater", CfPracticeStatus.Unknown)]
    public async Task GetPracticesByLeaseId_Status_IsTheOneOfTheSpecification_OrUnknown(string status, CfPracticeStatus expected)
    {
        var handler = new StubHandler(
            $$"""{ "leaseId": "{{LeaseId}}", "practices": [ { "practiceId": "{{PracticeId}}", "status": "{{status}}", "statusChangedAt": "2026-11-03T10:15:00Z" } ] }""");

        var practice = Assert.Single(await Client(handler).GetPracticesByLeaseIdAsync(Guid.Parse(LeaseId)));

        Assert.Equal(expected, practice.Status);
    }

    /// <summary>The four close reasons of §5 of the specification come through as they are.</summary>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("withdrawn")]
    [InlineData("not_compliant")]
    [InlineData("other")]
    public async Task GetPracticesByLeaseId_CloseReason_IsKeptAsReceived(string closeReason)
    {
        var handler = new StubHandler(
            $$"""{ "leaseId": "{{LeaseId}}", "practices": [ { "practiceId": "{{PracticeId}}", "status": "ClosedWithoutAttestation", "statusChangedAt": "2026-11-03T10:15:00Z", "closeReason": "{{closeReason}}" } ] }""");

        var practice = Assert.Single(await Client(handler).GetPracticesByLeaseIdAsync(Guid.Parse(LeaseId)));

        Assert.Equal(CfPracticeStatus.ClosedWithoutAttestation, practice.Status);
        Assert.Equal(closeReason, practice.CloseReason);
    }

    /// <summary>
    /// <c>GET /v1/practices/{practiceId}</c> is the same object plus <c>leaseId</c>, which is null for a practice that CF has received
    /// without a link to a contract: the lookup of the upload page must answer "pick the contract", not fail.
    /// </summary>
    [Fact]
    public async Task GetPractice_NotLinkedToALease_HasNoLeaseId()
    {
        var handler = new StubHandler("""
            {
              "practiceId": "P-0123456789abcdef",
              "status": "Received",
              "statusChangedAt": "2026-11-02T18:40:15Z",
              "submittedAt": "2026-11-02T18:40:12Z",
              "comune": "Monza",
              "comuneDiffersFromLink": null,
              "closeReason": null,
              "leaseId": null
            }
            """);

        var practice = await Client(handler).GetPracticeAsync(PracticeId);

        Assert.Equal($"/v1/practices/{PracticeId}", Assert.Single(handler.Requests).AbsolutePath);
        Assert.NotNull(practice);
        Assert.Equal(PracticeId, practice.PracticeId);
        Assert.Null(practice.LeaseId);
        Assert.Null(practice.ComuneDiffersFromLink);
    }

    [Fact]
    public async Task GetPractice_LinkedToALease_HasItsLeaseId()
    {
        var handler = new StubHandler(
            $$"""{ "practiceId": "{{PracticeId}}", "status": "InProgress", "statusChangedAt": "2026-11-03T10:15:00Z", "comuneDiffersFromLink": true, "leaseId": "{{LeaseId}}" }""");

        var practice = await Client(handler).GetPracticeAsync(PracticeId);

        Assert.NotNull(practice);
        Assert.Equal(Guid.Parse(LeaseId), practice.LeaseId);
        Assert.True(practice.ComuneDiffersFromLink);
    }

    /// <summary>404 <c>practice_not_found</c> (and a number CF does not accept) is "CF does not know it", not an error.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "practice_not_found")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_practice_id")]
    public async Task GetPractice_UnknownToCf_IsNull(HttpStatusCode status, string code)
    {
        var handler = new StubHandler($$"""{ "type": "about:blank", "title": "Not Found", "status": {{(int)status}}, "code": "{{code}}" }""", status);

        Assert.Null(await Client(handler).GetPracticeAsync(PracticeId));
    }

    /// <summary>A key CF rejects (401) is an error that the caller keeps as stale: it is never read as "no practices".</summary>
    [Fact]
    public async Task GetPracticesByLeaseId_KeyRejected_Throws_ItIsNotAnEmptyList()
    {
        var handler = new StubHandler(
            """{ "type": "about:blank", "title": "Unauthorized", "status": 401, "code": "unauthorized" }""",
            HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).GetPracticesByLeaseIdAsync(Guid.Parse(LeaseId)));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("unauthorized", ex.Message);
    }

    private static ConcordatoFacileApiClient Client(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://stato.concordatofacile.it") },
            NullLogger<ConcordatoFacileApiClient>.Instance);

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
