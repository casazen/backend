using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E40/F01/US02/T01 (NF-141) on real PostgreSQL, what the unit tests cannot show (on the in-memory provider the advisory lock is not taken): two registrations of two
/// hosts at the same moment, each on its own context and connection as two instances of the API are, read-modify-write the same SPA application of the shared Auth0 tenant
/// one after the other. Without <c>PostgresAdvisoryLocks.Scope.Auth0HostRegistration</c> both would read the same lists and the second write would take the first host away.
/// The fake Auth0 answers a read with the application as it is when the call arrives and then takes its time, so two registrations that did not take turns could not both survive.
/// </summary>
public class Auth0HostRegistrationPostgresTests
{
    private const string TenantDomain = "tenant.eu.auth0.com";
    private const string SpaClientId = "SpaClientOfTheTestEnvironment01";
    private const string PlatformOrigin = "https://app.platform.example";
    private const string HostA = "prenota.agenzia-a.example";
    private const string HostB = "prenota.agenzia-b.example";

    [PostgresFact]
    public async Task TwoRegistrationsOnTwoConnections_TakeTurnsThroughTheAdvisoryLock_NeitherWritesTheOthersListsAway()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("auth0lock");
        var auth0 = new SlowAuth0(PlatformOrigin, TimeSpan.FromMilliseconds(250));

        // Each registration has its own context, connection and gate (the gate is the turn inside ONE process): only the lock of the database is between them.
        async Task<Auth0HostRegistrationResult> RegisterAsync(string host)
        {
            await using var db = database.CreateContext();
            var options = Options.Create(new Auth0HostSyncOptions { ClientId = SpaClientId });
            var registrar = new Auth0HostRegistrar(Tokens(), Factory(auth0), options, db, NullLogger<Auth0HostRegistrar>.Instance, new Auth0HostRegistrationGate());
            return await registrar.RegisterAsync(host);
        }

        var results = await Task.WhenAll(RegisterAsync(HostA), RegisterAsync(HostB));

        Assert.Equal([Auth0HostRegistrationResult.Registered, Auth0HostRegistrationResult.Registered], results);
        foreach (var list in auth0.Lists())
        {
            Assert.Equal(PlatformOrigin, list[0]);
            Assert.Equal([$"https://{HostA}", $"https://{HostB}"], list.Skip(1).Order(StringComparer.Ordinal));
        }
    }

    private static IAuth0ManagementTokenProvider Tokens()
    {
        var provider = new Mock<IAuth0ManagementTokenProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Domain).Returns(TenantDomain);
        provider.Setup(p => p.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("mgmt-token");
        return provider.Object;
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    /// <summary>The Management API of the SPA application: a read answers the three lists as they are on arrival, a write replaces each list it carries; every call takes its time.</summary>
    private sealed class SlowAuth0(string platformOrigin, TimeSpan delay) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private string[] _callbacks = [platformOrigin];
        private string[] _logoutUrls = [platformOrigin];
        private string[] _webOrigins = [platformOrigin];

        public IReadOnlyList<IReadOnlyList<string>> Lists()
        {
            lock (_gate)
                return [_callbacks, _logoutUrls, _webOrigins];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string answer;
            if (request.Method == HttpMethod.Patch)
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
                await Task.Delay(delay, cancellationToken);
                lock (_gate)
                {
                    _callbacks = Strings(body, "callbacks") ?? _callbacks;
                    _logoutUrls = Strings(body, "allowed_logout_urls") ?? _logoutUrls;
                    _webOrigins = Strings(body, "web_origins") ?? _webOrigins;
                    answer = Application();
                }
            }
            else
            {
                lock (_gate)
                    answer = Application();

                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }

        private string Application() => JsonSerializer.Serialize(new
        {
            client_id = SpaClientId,
            callbacks = _callbacks,
            allowed_logout_urls = _logoutUrls,
            web_origins = _webOrigins,
        });

        private static string[]? Strings(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out var array))
                return null;

            return [.. array.EnumerateArray().Select(e => e.GetString()!)];
        }
    }
}
