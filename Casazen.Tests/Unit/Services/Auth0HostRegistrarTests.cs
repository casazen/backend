using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RichardSzalay.MockHttp;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E40/F01/US02/T01 (NF-141): the registrar of the agencies' hosts in the shared Auth0 tenant, through the real Management SDK client against a fake handler
/// that keeps the three lists of the SPA application as Auth0 does. The tenant is touched only for the host asked: appended in one write, read back, never
/// a wildcard, never another entry, never the platform origin, and never a credential or an error body of Auth0 in a log.
/// </summary>
public sealed class Auth0HostRegistrarTests : IDisposable
{
    private const string TenantDomain = "tenant.eu.auth0.com";
    private const string ApiBase = "https://tenant.eu.auth0.com/api/v2";
    private const string SpaClientId = "SpaClientOfTheTestEnvironment01";
    private const string PlatformOrigin = "https://app.platform.example";
    private const string LocalOrigin = "http://localhost:5173";
    private const string OtherAgencyOrigin = "https://prenota.altra-agenzia.example";
    private const string Host = "prenota.agenzia-esempio.it";
    private const string HostOrigin = "https://prenota.agenzia-esempio.it";

    private readonly MockHttpMessageHandler _http = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Mock<IAuth0ManagementTokenProvider> _tokenProvider = new();
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // The SPA application of the environment as Auth0 keeps it: the platform origin first (auth0.md § 1.2 rule 6).
    private List<string> _callbacks = [PlatformOrigin, LocalOrigin];
    private List<string> _logoutUrls = [PlatformOrigin, LocalOrigin];
    private List<string> _webOrigins = [PlatformOrigin, LocalOrigin];

    public Auth0HostRegistrarTests()
    {
        _tokenProvider.SetupGet(p => p.IsConfigured).Returns(true);
        _tokenProvider.SetupGet(p => p.Domain).Returns(TenantDomain);
        _tokenProvider.Setup(p => p.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("mgmt-token");
    }

    public void Dispose()
    {
        _db.Dispose();
        _http.Dispose();
    }

    [Fact]
    public async Task Register_Twice_WritesOnce_AndKeepsOrder()
    {
        ServeTheApplication();
        var registrar = Registrar();

        Assert.Equal(Auth0HostRegistrationResult.Registered, await registrar.RegisterAsync(Host));
        Assert.Equal(Auth0HostRegistrationResult.AlreadyRegistered, await registrar.RegisterAsync(Host));

        // One write, carrying the three lists and nothing else of the application.
        var patch = Assert.Single(_requests, r => r.Method == HttpMethod.Patch);
        Assert.EndsWith($"/clients/{SpaClientId}", patch.Path);
        var body = JsonDocument.Parse(patch.Body!).RootElement;
        Assert.Equal(["allowed_logout_urls", "callbacks", "web_origins"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // Appended: every entry that was there stays where it was, and the first callback URL is still the platform origin.
        Assert.Equal([PlatformOrigin, LocalOrigin, HostOrigin], Strings(body.GetProperty("callbacks")));
        Assert.Equal([PlatformOrigin, LocalOrigin, HostOrigin], _callbacks);
        Assert.Equal([PlatformOrigin, LocalOrigin, HostOrigin], _logoutUrls);
        Assert.Equal([PlatformOrigin, LocalOrigin, HostOrigin], _webOrigins);
    }

    [Fact]
    public async Task Register_HostInSomeListsOnly_AddsItWhereItMisses_InTheSameWrite()
    {
        _logoutUrls = [PlatformOrigin, HostOrigin];
        ServeTheApplication();

        Assert.Equal(Auth0HostRegistrationResult.Registered, await Registrar().RegisterAsync(Host));

        Assert.Single(_requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal([PlatformOrigin, LocalOrigin, HostOrigin], _callbacks);
        Assert.Equal([PlatformOrigin, HostOrigin], _logoutUrls);
        Assert.Equal([PlatformOrigin, LocalOrigin, HostOrigin], _webOrigins);
    }

    [Fact]
    public async Task Register_WriteRejected_NoPartialLists()
    {
        ServeTheApplication(patchStatus: HttpStatusCode.BadRequest);

        var result = await Registrar().RegisterAsync(Host);

        Assert.Equal(Auth0HostRegistrationResult.Rejected, result);
        // The three lists travel in the one write Auth0 refused, so none of them changed; nothing else was written after the refusal.
        var patch = Assert.Single(_requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal(3, JsonDocument.Parse(patch.Body!).RootElement.EnumerateObject().Count());
        Assert.Equal([PlatformOrigin, LocalOrigin], _callbacks);
        Assert.Equal([PlatformOrigin, LocalOrigin], _logoutUrls);
        Assert.Equal([PlatformOrigin, LocalOrigin], _webOrigins);
    }

    [Fact]
    public async Task Register_TheReadBackLacksTheHost_IsRejected()
    {
        // Auth0 answers 200 and keeps nothing: only the read-back can tell.
        ServeTheApplication(applyPatch: false);

        Assert.Equal(Auth0HostRegistrationResult.Rejected, await Registrar().RegisterAsync(Host));
        Assert.Equal(2, _requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, Auth0HostRegistrationResult.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, Auth0HostRegistrationResult.Rejected)]
    [InlineData(HttpStatusCode.NotFound, Auth0HostRegistrationResult.Rejected)]
    public async Task Register_ManagementError_IsAnAnswerNeverAnException(HttpStatusCode status, Auth0HostRegistrationResult expected)
    {
        _http.When(HttpMethod.Get, $"{ApiBase}/clients/{SpaClientId}").Respond(async request =>
        {
            await RecordAsync(request);
            return Json(status, new { statusCode = (int)status, error = "Error", message = "boom" });
        });

        Assert.Equal(expected, await Registrar().RegisterAsync(Host));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Unregister_RemovesOnlyThatHost()
    {
        _callbacks = [PlatformOrigin, OtherAgencyOrigin, HostOrigin, HostOrigin + "/portale-proprietari"];
        _logoutUrls = [PlatformOrigin, HostOrigin, OtherAgencyOrigin];
        _webOrigins = [PlatformOrigin, OtherAgencyOrigin, HostOrigin];
        ServeTheApplication();
        var registrar = Registrar();

        Assert.Equal(Auth0HostRegistrationResult.Removed, await registrar.UnregisterAsync(Host));

        // Exactly the strings of the host: another agency's origin and a longer URL that only starts like it stay, in their order.
        Assert.Equal([PlatformOrigin, OtherAgencyOrigin, HostOrigin + "/portale-proprietari"], _callbacks);
        Assert.Equal([PlatformOrigin, OtherAgencyOrigin], _logoutUrls);
        Assert.Equal([PlatformOrigin, OtherAgencyOrigin], _webOrigins);

        // Nothing left to remove: nothing is written.
        Assert.Equal(Auth0HostRegistrationResult.Removed, await registrar.UnregisterAsync(Host));
        Assert.Single(_requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Unregister_ThePlatformOrigin_IsRefused()
    {
        ServeTheApplication();

        Assert.Equal(Auth0HostRegistrationResult.Rejected, await Registrar().UnregisterAsync("app.platform.example"));

        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal([PlatformOrigin, LocalOrigin], _callbacks);
    }

    [Fact]
    public async Task Register_AnApplicationWithoutCallbacks_IsRefused_TheAgencyNeverBecomesTheFirstCallback()
    {
        _callbacks = [];
        ServeTheApplication();

        Assert.Equal(Auth0HostRegistrationResult.Rejected, await Registrar().RegisterAsync(Host));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Patch);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("not a client id", true)]
    [InlineData(SpaClientId, false)]
    public async Task Register_NotConfigured_MakesNoHttpCall(string? clientId, bool managementConfigured)
    {
        _tokenProvider.SetupGet(p => p.IsConfigured).Returns(managementConfigured);
        ServeTheApplication();
        var registrar = Registrar(clientId);

        Assert.Equal(Auth0HostRegistrationResult.NotConfigured, await registrar.RegisterAsync(Host));
        Assert.Equal(Auth0HostRegistrationResult.NotConfigured, await registrar.UnregisterAsync(Host));

        Assert.Empty(_requests);
        _tokenProvider.Verify(p => p.GetTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("*.agenzia-esempio.it")]
    [InlineData("https://prenota.agenzia-esempio.it")]
    [InlineData("prenota.agenzia-esempio.it/portale")]
    [InlineData("prenota.agenzia-esempio.it:8443")]
    [InlineData("203.0.113.7")]
    [InlineData("localhost")]
    [InlineData("")]
    public async Task Register_NotAHostName_IsRejectedWithoutACall(string host)
    {
        ServeTheApplication();

        Assert.Equal(Auth0HostRegistrationResult.Rejected, await Registrar().RegisterAsync(host));
        Assert.Equal(Auth0HostRegistrationResult.Rejected, await Registrar().UnregisterAsync(host));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Register_TwoHostsAtOnceInOneProcess_TakeTurns_NeitherWritesTheOthersListsAway()
    {
        // Two requests of the same process (the retry of two agencies, or the job and a click): they share the one gate, as the singleton of the API does.
        ServeTheApplication(readDelayMilliseconds: 150);
        var gate = new Auth0HostRegistrationGate();
        var first = Registrar(gate: gate);
        var second = Registrar(gate: gate);

        var results = await Task.WhenAll(first.RegisterAsync(Host), second.RegisterAsync("prenota.altra-agenzia.example"));

        // The second one read the application after the first one wrote it: both origins are in the three lists, after the platform's own.
        Assert.Equal([Auth0HostRegistrationResult.Registered, Auth0HostRegistrationResult.Registered], results);
        Assert.Equal(2, _requests.Count(r => r.Method == HttpMethod.Patch));
        foreach (var list in new[] { _callbacks, _logoutUrls, _webOrigins })
        {
            Assert.Equal(PlatformOrigin, list[0]);
            Assert.Contains(HostOrigin, list);
            Assert.Contains(OtherAgencyOrigin, list);
        }
    }

    [Fact]
    public async Task Register_WhenTheTurnDoesNotComeInTime_IsBusy_AndAsksAuth0Nothing()
    {
        ServeTheApplication();
        var gate = new Auth0HostRegistrationGate(TimeSpan.FromMilliseconds(50));
        var log = new CapturingLogger<Auth0HostRegistrar>();
        var registrar = new Auth0HostRegistrar(_tokenProvider.Object, Factory(_http), Options.Create(new Auth0HostSyncOptions { ClientId = SpaClientId }), _db, log, gate);

        // Somebody else has the turn (a slow Auth0): no database connection is taken to wait, the answer is a retry later.
        Assert.True(await gate.EnterAsync(CancellationToken.None));
        var busy = await registrar.RegisterAsync(Host);
        gate.Leave();

        Assert.Equal(Auth0HostRegistrationResult.Unavailable, busy);
        Assert.Empty(_requests);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("busy", StringComparison.Ordinal));

        // The turn is given back: the next call goes through.
        Assert.Equal(Auth0HostRegistrationResult.Registered, await registrar.RegisterAsync(Host));
    }

    [Fact]
    public async Task Failure_LogsAndMessages_ContainNoCredentials()
    {
        const string M2mClientId = "m2m-client-SECRET-ID-4711";
        const string M2mSecret = "m2m-secret-SECRET-VALUE-0815";
        const string IssuedToken = "eyJ-token-SECRET-TOKEN-1234";
        string[] neverSeen = [M2mClientId, M2mSecret, IssuedToken, "SECRET-", "Insufficient scope"];

        // 1. The token comes, and Auth0 refuses the write echoing what it received (the scope update:clients is missing).
        var refusing = new MockHttpMessageHandler();
        refusing.When(HttpMethod.Post, $"https://{TenantDomain}/oauth/token")
            .Respond(_ => Json(HttpStatusCode.OK, new { access_token = IssuedToken, expires_in = 86400, token_type = "Bearer" }));
        refusing.When(HttpMethod.Get, $"{ApiBase}/clients/{SpaClientId}").Respond(_ => Json(HttpStatusCode.OK, Application()));
        refusing.When(HttpMethod.Patch, $"{ApiBase}/clients/{SpaClientId}").Respond(_ => Json(
            HttpStatusCode.Forbidden,
            new { statusCode = 403, error = "Forbidden", message = $"Insufficient scope for {M2mClientId} {M2mSecret} {IssuedToken}", errorCode = "insufficient_scope" }));

        // 2. The token endpoint refuses the credentials, echoing them in its body.
        var noToken = new MockHttpMessageHandler();
        noToken.When(HttpMethod.Post, $"https://{TenantDomain}/oauth/token").Respond(_ => Json(
            HttpStatusCode.Unauthorized,
            new { error = "access_denied", error_description = $"Insufficient scope {M2mClientId} {M2mSecret}" }));

        var logs = new List<string>();
        foreach (var (handler, expected) in new[] { (refusing, Auth0HostRegistrationResult.Rejected), (noToken, Auth0HostRegistrationResult.Unavailable) })
        {
            using (handler)
            {
                var factory = Factory(handler);
                var tokenLog = new CapturingLogger<Auth0ManagementTokenProvider>();
                var provider = new Auth0ManagementTokenProvider(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth0:Domain"] = TenantDomain,
                        ["Auth0:ManagementClientId"] = M2mClientId,
                        ["Auth0:ManagementClientSecret"] = M2mSecret,
                    }).Build(),
                    factory,
                    TimeProvider.System,
                    tokenLog);
                var log = new CapturingLogger<Auth0HostRegistrar>();
                var registrar = new Auth0HostRegistrar(provider, factory, Options.Create(new Auth0HostSyncOptions { ClientId = SpaClientId }), _db, log);

                // Never an exception: the caller gets a code and the log a line, nothing of what Auth0 or the credentials said.
                var result = await registrar.RegisterAsync(Host);

                Assert.Equal(expected, result);
                Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning);
                Assert.All(log.Entries, e => Assert.Null(e.Exception));
                logs.Add(log.AllOutput);
                logs.Add(tokenLog.AllOutput);
            }
        }

        var output = string.Join(Environment.NewLine, logs);
        Assert.All(neverSeen, secret => Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private Auth0HostRegistrar Registrar(string? clientId = SpaClientId, Auth0HostRegistrationGate? gate = null) =>
        new(_tokenProvider.Object, Factory(_http), Options.Create(new Auth0HostSyncOptions { ClientId = clientId }), _db, NullLogger<Auth0HostRegistrar>.Instance, gate);

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    /// <summary>
    /// The application on the fake Management API: a read answers the three lists; a write replaces each list it carries (as Auth0 does), unless it is
    /// refused (<paramref name="patchStatus"/>) or silently dropped (<paramref name="applyPatch"/> false).
    /// </summary>
    private void ServeTheApplication(HttpStatusCode patchStatus = HttpStatusCode.OK, bool applyPatch = true, int readDelayMilliseconds = 0)
    {
        _http.When(HttpMethod.Get, $"{ApiBase}/clients/{SpaClientId}").Respond(async request =>
        {
            await RecordAsync(request);
            var answer = Json(HttpStatusCode.OK, Application());

            // The answer is made before the wait: a read that is slow answers what the application was when it started.
            if (readDelayMilliseconds > 0)
                await Task.Delay(readDelayMilliseconds);

            return answer;
        });
        _http.When(HttpMethod.Patch, $"{ApiBase}/clients/{SpaClientId}").Respond(async request =>
        {
            var body = await RecordAsync(request);
            if (patchStatus != HttpStatusCode.OK)
            {
                return Json(patchStatus, new { statusCode = (int)patchStatus, error = "Bad Request", message = "Payload validation error", errorCode = "invalid_body" });
            }

            if (applyPatch)
            {
                var root = JsonDocument.Parse(body!).RootElement;
                if (root.TryGetProperty("callbacks", out var callbacks))
                    _callbacks = Strings(callbacks);
                if (root.TryGetProperty("allowed_logout_urls", out var logoutUrls))
                    _logoutUrls = Strings(logoutUrls);
                if (root.TryGetProperty("web_origins", out var webOrigins))
                    _webOrigins = Strings(webOrigins);
            }

            return Json(HttpStatusCode.OK, Application());
        });
    }

    private object Application() => new
    {
        client_id = SpaClientId,
        callbacks = _callbacks,
        allowed_logout_urls = _logoutUrls,
        web_origins = _webOrigins,
    };

    private async Task<string?> RecordAsync(HttpRequestMessage request)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
        lock (_requests)
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body));
        return body;
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);
}
