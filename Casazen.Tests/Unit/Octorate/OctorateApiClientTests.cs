using System.Net;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Core.Octorate;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the Octorate client (E02/F01/US01/T02): every call acts for the org of <see cref="IOctorateOrgScope"/>, with that org's
/// token and bucket and never another's, and no call is made for no org (ADR-023-R4, R15); the rate limit is per org (ADR-023-R8, R14).
/// The HTTP answer is a stub: what is proven is which token and which bucket a call uses, not Octorate.
/// </summary>
public class OctorateApiClientTests
{
    private static readonly Guid OrgA = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");
    private static readonly Guid OrgB = Guid.Parse("7d2c4e91-58b3-4f0a-a6e2-9c1d0b3e4f02");

    [Fact]
    public async Task OctorateClient_OrgScope_NeverUsesAnotherOrgsToken_Test()
    {
        var h = new Harness();

        using (h.Scope.Use(OrgA))
            await h.Client.GetConfigurationAsync(CancellationToken.None);
        using (h.Scope.Use(OrgB))
            await h.Client.GetPropertyAsync("4711", CancellationToken.None);

        // Each call asked the token of its own org and sent it, and took a call from its own org's bucket only.
        Assert.Equal(new[] { OrgA, OrgB }, h.Tokens.RequestedOrgs);
        Assert.Equal(new[] { "Bearer " + FakeTokens.TokenOf(OrgA), "Bearer " + FakeTokens.TokenOf(OrgB) }, h.Handler.Authorizations);
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute - 1, h.Limiter.AvailableCalls(OrgA));
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute - 1, h.Limiter.AvailableCalls(OrgB));

        // No org (a job that forgot Use): refused before any token is read and before anything leaves.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.GetConfigurationAsync(CancellationToken.None));
        Assert.Equal(2, h.Tokens.RequestedOrgs.Count);
        Assert.Equal(2, h.Handler.Authorizations.Count);

        // Inside an authenticated request of org A the scope is org A, and it cannot be switched to org B.
        var requestScope = new OctorateOrgScope(new RequestTenant(OrgA));
        Assert.Equal(OrgA, requestScope.OrgId);
        Assert.Throws<InvalidOperationException>(() => requestScope.Use(OrgB));
        using (requestScope.Use(OrgA))
            Assert.Equal(OrgA, requestScope.OrgId);
    }

    [Fact]
    public async Task OctorateConnector_RateLimit_EnforcedPerOrg_Test()
    {
        var h = new Harness();

        using (h.Scope.Use(OrgA))
        {
            for (var i = 0; i < OctorateRateLimiter.RequestsPerMinute; i++)
                await h.Client.GetConfigurationAsync(CancellationToken.None);

            // The 101st call of the minute is refused by CasaZen itself: it never reaches Octorate.
            var refused = await Assert.ThrowsAsync<OctorateRateLimitException>(
                () => h.Client.GetConfigurationAsync(CancellationToken.None));
            Assert.Null(refused.OctorateStatusCode);
            Assert.Equal("octorate_rate_limited", refused.Code);
        }

        Assert.Equal(OctorateRateLimiter.RequestsPerMinute, h.Handler.Authorizations.Count);

        // Org B, in the same minute, has its own bucket: unaffected.
        using (h.Scope.Use(OrgB))
            await h.Client.GetConfigurationAsync(CancellationToken.None);

        Assert.Equal(OctorateRateLimiter.RequestsPerMinute + 1, h.Handler.Authorizations.Count);
        Assert.Equal(0, h.Limiter.AvailableCalls(OrgA));
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute - 1, h.Limiter.AvailableCalls(OrgB));
    }

    [Fact]
    public async Task OctorateCheckIn_PayloadHasNoDocumentImage_Test()
    {
        // AC-NF41-4 at compile time: the guest sent to Octorate is made of data fields only, no member can hold an image, a stream or bytes.
        var dataTypes = new[] { typeof(string), typeof(int), typeof(bool), typeof(bool?), typeof(decimal?), typeof(DateOnly?) };
        Assert.All(typeof(OctorateCheckInGuestUpsert).GetProperties(), property =>
        {
            Assert.Contains(property.PropertyType, dataTypes);
            Assert.DoesNotMatch("(?i)image|photo|picture|scan|file|stream|byte|base64", property.Name);
        });

        // A guest whose document photos are on file: what leaves carries the document data and none of the photos.
        var photos = new DocumentImage
        {
            FrontImageUrl = "https://storage.test/documents/front-0001.jpg",
            BackImageUrl = "https://storage.test/documents/back-0001.jpg",
        };
        var guest = new StayGuest
        {
            Position = 0,
            Type = StayGuestType.SingleGuest,
            FirstName = "Ospite",
            LastName = "Di Prova",
            Gender = Gender.Male,
            DateOfBirth = new DateTime(1980, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            BornInItaly = true,
            BirthComuneName = "Como",
            CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
            CitizenshipName = "ITALIA",
            ResidenceInItaly = true,
            ResidenceComuneName = "Como",
            DocumentType = GuestDocumentType.Passport,
            DocumentNumber = "YA0000001",
            DocumentIssuePlaceName = "Como",
            DocumentExpiresOn = new DateOnly(2031, 3, 1),
        };
        var upsert = OctorateCheckInMapper.Map(guest, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), draft: false, null, null);
        var h = new Harness();

        using (h.Scope.Use(OrgA))
            await h.Client.UpsertCheckInGuestsAsync("4711", "7001", [upsert], CancellationToken.None);

        var body = Assert.Single(h.Handler.Bodies);
        Assert.Contains("\"documentCode\":\"YA0000001\"", body, StringComparison.Ordinal);
        Assert.Contains("\"documentType\":\"PASOR\"", body, StringComparison.Ordinal);

        // The dates travel in the forms of the Octorate schema: a day as yyyy-MM-dd, the expiry of the document (a date-time there) at midnight.
        Assert.Contains("\"birthDate\":\"1980-07-01\"", body, StringComparison.Ordinal);
        Assert.Contains("\"documentExpire\":\"2031-03-01T00:00:00\"", body, StringComparison.Ordinal);

        Assert.DoesNotContain(photos.FrontImageUrl, body, StringComparison.Ordinal);
        Assert.DoesNotContain(photos.BackImageUrl!, body, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)image|photo|base64|data:", body);
    }

    [Fact]
    public async Task OctorateClient_PoliceAccount_IsAskedForByNameAndTheCardKeepsItsAddress_Test()
    {
        var h = new Harness();
        h.Handler.Body = """{"id":"4711","policeAccount":{"codice":"TEST","wsCode":"TEST","valid":true,"autorun":true,"lastCron":"2026-11-05T22:00:00Z"}}""";

        OctoratePoliceAccount account;
        using (h.Scope.Use(OrgA))
        {
            account = await h.Client.GetPoliceAccountAsync("4711", CancellationToken.None);
            await h.Client.GetPropertyAsync("4711", CancellationToken.None);
        }

        // The police account is asked for by name (Q-002), so it does arrive; the card read for the import is read as before, without fields.
        Assert.Equal(
            new[]
            {
                "https://api.octorate.test/connect/rest/v1/accommodation/4711?fields=policeAccount",
                "https://api.octorate.test/connect/rest/v1/accommodation/4711",
            },
            h.Handler.Addresses.Select(address => address.AbsoluteUri).ToArray());
        Assert.Equal<(bool?, bool?)>((true, true), (account.Valid, account.Autorun));
        Assert.Equal<DateTime?>(new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc), account.LastCron);
    }

    /// <summary>
    /// Proof (trust, E10/F01/US03/T01, AC-NF107-3): what only means «CasaZen cannot tell» is never an exception and never a verdict. The account is
    /// read as <c>Readable</c> with <c>valid</c> true or false; the field absent (an accommodation created through the API has none), without
    /// <c>valid</c>, a read Octorate refused and an accommodation it does not know are <c>Readable</c> false. A failure worth a retry keeps its typed
    /// error, so a caller never takes it for a verdict.
    /// </summary>
    [Theory]
    [InlineData("""{"id":"4711","policeAccount":{"codice":"TEST","valid":true,"autorun":false,"lastCron":"2026-11-05T22:00:00Z"}}""", HttpStatusCode.OK, true, true)]
    [InlineData("""{"id":"4711","policeAccount":{"valid":false,"autorun":true}}""", HttpStatusCode.OK, true, false)]
    [InlineData("""{"id":"4711","name":"Casa creata via API"}""", HttpStatusCode.OK, false, null)]
    [InlineData("""{"id":"4711","policeAccount":{"autorun":true,"lastCron":"2026-11-05T22:00:00Z"}}""", HttpStatusCode.OK, false, null)]
    [InlineData("""{"type":"ApiResourceNotFound"}""", HttpStatusCode.NotFound, false, null)]
    [InlineData("""{"type":"ApiSecurityException"}""", HttpStatusCode.Forbidden, false, null)]
    public async Task OctorateClient_PoliceAccountStatus_ReadsValid_AndCannotTellIsNeverAVerdict_Test(
        string body,
        HttpStatusCode status,
        bool readable,
        bool? valid)
    {
        var h = new Harness();
        h.Handler.Body = body;
        h.Handler.StatusCode = status;

        OctoratePoliceAccountStatus account;
        using (h.Scope.Use(OrgA))
            account = await h.Client.GetPoliceAccountStatusAsync("4711", CancellationToken.None);

        Assert.Equal(readable, account.Readable);
        Assert.Equal(valid, account.Valid);
        if (!readable)
        {
            Assert.Null(account.Autorun);
            Assert.Null(account.LastCron);
        }

        // The same read as the police account of the sender: asked for by name, nothing else of the accommodation.
        Assert.Equal(
            "https://api.octorate.test/connect/rest/v1/accommodation/4711?fields=policeAccount",
            Assert.Single(h.Handler.Addresses).AbsoluteUri);
    }

    [Fact]
    public async Task OctorateClient_PoliceAccountStatus_KeepsTheMembersOfAReadableAccount_AndRetryableFailuresKeepTheirTypedError_Test()
    {
        var h = new Harness();
        h.Handler.Body = """{"id":"4711","policeAccount":{"codice":"TEST","wsCode":"TEST","valid":true,"autorun":false,"lastCron":"2026-11-05T22:00:00Z"}}""";

        using (h.Scope.Use(OrgA))
        {
            var readable = await h.Client.GetPoliceAccountStatusAsync("4711", CancellationToken.None);
            Assert.Equal(new OctoratePoliceAccountStatus(true, true, false, new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc)), readable);

            // A failure of Octorate is not «cannot tell»: the typed error reaches the caller, which decides nothing.
            h.Handler.StatusCode = HttpStatusCode.InternalServerError;
            h.Handler.Body = """{"message":"boom"}""";
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetPoliceAccountStatusAsync("4711", CancellationToken.None));
        }

        // No org, no call: not an answer of «cannot tell» either.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.GetPoliceAccountStatusAsync("4711", CancellationToken.None));
    }

    private sealed class Harness
    {
        public Harness()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
                .Build();
            Limiter = new OctorateRateLimiter(clock);
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(
                new HttpClient(Handler),
                Tokens,
                Limiter,
                Scope,
                configuration,
                clock,
                NullLogger<OctorateApiClient>.Instance);
        }

        public FakeTokens Tokens { get; } = new();

        public RecordingHandler Handler { get; } = new();

        public OctorateRateLimiter Limiter { get; }

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }
    }

    /// <summary>A token per org, and the orgs whose token was asked.</summary>
    private sealed class FakeTokens : IOctorateTokenService
    {
        public List<Guid> RequestedOrgs { get; } = [];

        public static string TokenOf(Guid orgId) => "token-" + orgId.ToString("N");

        public Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct)
        {
            RequestedOrgs.Add(orgId);
            return Task.FromResult(TokenOf(orgId));
        }

        public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No token is refused in these tests.");

        public Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(Guid orgId, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetPartnerAccessTokenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");

        public Task<string> RefreshRejectedPartnerTokenAsync(string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");
    }

    /// <summary>Answers every call with one JSON object (configuration and accommodation both read what they need), and keeps the tokens sent and the addresses called.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Authorizations { get; } = [];

        /// <summary>The body of every call that had one.</summary>
        public List<string> Bodies { get; } = [];

        /// <summary>The addresses called, in order.</summary>
        public List<Uri> Addresses { get; } = [];

        /// <summary>What every call is answered with.</summary>
        public string Body { get; set; } = """{"id":"4711","name":"Casa Prova","environment":"SANDBOX","quotaLimit":180,"quotaStatus":57}""";

        /// <summary>The status of the answer of every call.</summary>
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "(none)");
            Addresses.Add(request.RequestUri!);
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>The tenant of an authenticated request of one org.</summary>
    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
