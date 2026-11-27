using System.Net;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proof of the refresh of the Octorate token (E02/F01/US01/T01, ADR-023-R12): the tokens are forgotten, and the connection
/// disconnected, only when Octorate says the refresh token itself is not valid. Octorate gives the refresh token once, with the first
/// authorization, so any other refusal of the refresh (credentials of the other environment, a client secret rotated or mistyped) must
/// leave both tokens where they are: a mistake in the configuration does not cost every org its connection, and the next call works as
/// soon as it is put right. The HTTP answer is a stub: what is proven is what the service keeps and forgets, not Octorate.
/// </summary>
public class OctorateTokenRefreshTests
{
    // One provider for the whole class: EF keeps one model per provider.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private static readonly Guid OrgA = Guid.Parse("c3d4e5f6-0718-4293-a4b5-c6d7e8f90a01");
    private const string RefreshToken = "RT-original-5b0c7e9a";
    private const string ExpiredAccessToken = "AT-expired-1f3d5a7c";
    private const string NewAccessToken = "AT-new-9e8d7c6b";

    [Fact]
    public async Task OctorateTokenRefresh_ForgetsTheTokensOnlyWhenTheRefreshTokenIsInvalid_Test()
    {
        var refusals = new (string Name, HttpStatusCode Status, string Body, bool Forgets, string Code)[]
        {
            // Octorate says the refresh token is not valid: the 403 seen in the sandbox, and the OAuth error.
            ("errRefreshTokenInvalid", HttpStatusCode.Forbidden, """{"type":"ApiSecurityException","suggestion":"errRefreshTokenInvalid"}""", true, "octorate_auth_failed"),
            ("invalid_grant", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", true, "octorate_auth_failed"),

            // Not the refresh token: the credentials of the other environment, a client secret rotated or mistyped (the suggestion of the
            // last one is made up: any other reason of an ApiSecurityException).
            ("WrongEnviroment", HttpStatusCode.Forbidden, """{"type":"WrongEnviroment"}""", false, "octorate_wrong_environment"),
            ("invalid_client", HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""", false, "octorate_auth_failed"),
            ("other ApiSecurityException", HttpStatusCode.Forbidden, """{"type":"ApiSecurityException","suggestion":"errOtherReason"}""", false, "octorate_auth_failed"),
        };

        foreach (var (name, status, body, forgets, code) in refusals)
        {
            await using var rig = await Rig.CreateAsync();
            rig.Octorate.Answer(status, body);

            // The refresh is refused with the typed error of its kind.
            var refused = await Assert.ThrowsAnyAsync<OctorateException>(() => rig.Tokens.GetAccessTokenAsync(OrgA, CancellationToken.None));
            Assert.True(code == refused.Code, $"{name}: the error is '{refused.Code}', expected '{code}'");

            var connection = await rig.ConnectionAsync();
            if (forgets)
            {
                // Disconnected, both tokens forgotten, and nobody asks Octorate again: the next call is a plain "not connected".
                Assert.True(
                    connection.Status == OctorateConnectionStatus.Disconnected
                    && connection.EncryptedAccessToken is null
                    && connection.EncryptedRefreshToken is null
                    && connection.TokenExpiresAt is null,
                    $"{name}: the connection should be disconnected and have forgotten its tokens");

                var again = await Assert.ThrowsAsync<OctorateAuthException>(() => rig.Tokens.GetAccessTokenAsync(OrgA, CancellationToken.None));
                Assert.True(again.IsNotConnected && rig.Octorate.Calls == 1, $"{name}: Octorate should not be asked again");
            }
            else
            {
                // Still connected, with both tokens as they were.
                Assert.True(
                    connection.Status == OctorateConnectionStatus.Connected
                    && connection.EncryptedAccessToken == ExpiredAccessToken
                    && connection.EncryptedRefreshToken == RefreshToken,
                    $"{name}: the connection should still be connected with both tokens");

                // The configuration put right, the very next call refreshes with the same refresh token, which Octorate would not give again.
                rig.Octorate.Answer(HttpStatusCode.OK, $$"""{"access_token":"{{NewAccessToken}}","expires_in":129599}""");
                Assert.Equal(NewAccessToken, await rig.Tokens.GetAccessTokenAsync(OrgA, CancellationToken.None));
                Assert.Contains("refresh_token=" + RefreshToken, rig.Octorate.LastForm, StringComparison.Ordinal);
                Assert.Equal(RefreshToken, (await rig.ConnectionAsync()).EncryptedRefreshToken);
            }
        }
    }

    /// <summary>The service over an in-memory database with a connected, expired access token, and the stubbed refresh endpoint.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(AppDbContext db, RefreshEndpoint octorate, OctorateTokenService tokens)
        {
            Db = db;
            Octorate = octorate;
            Tokens = tokens;
        }

        public AppDbContext Db { get; }

        public RefreshEndpoint Octorate { get; }

        public OctorateTokenService Tokens { get; }

        public static async Task<Rig> CreateAsync()
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
            var tenant = new RequestTenant(OrgA);
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect",
                    ["Octorate:ClientId"] = "casazen-test-client",
                    ["Octorate:ClientSecret"] = "casazen-test-secret-" + Guid.NewGuid().ToString("N"),
                })
                .Build();

            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"octorate-refresh-{Guid.NewGuid()}").Options,
                tenant,
                Provider);
            db.OctorateConnections.Add(new OctorateConnection
            {
                OrgId = OrgA,
                Environment = OctorateEnvironment.Test,
                Status = OctorateConnectionStatus.Connected,
                EncryptedAccessToken = ExpiredAccessToken,
                EncryptedRefreshToken = RefreshToken,
                TokenExpiresAt = clock.GetUtcNow().UtcDateTime.AddHours(-1),
            });
            await db.SaveChangesAsync();

            var octorate = new RefreshEndpoint();
            var tokens = new OctorateTokenService(
                db, tenant, new StubHttpClientFactory(octorate), configuration, host.Object, clock, NullLogger<OctorateTokenService>.Instance);
            return new Rig(db, octorate, tokens);
        }

        public Task<OctorateConnection> ConnectionAsync() => Db.OctorateConnections.AsNoTracking().SingleAsync();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>The refresh endpoint of Octorate: answers what the test says and keeps what it was sent.</summary>
    private sealed class RefreshEndpoint : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public int Calls { get; private set; }

        public string LastForm { get; private set; } = string.Empty;

        public void Answer(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>The tenant of an authenticated request of one org.</summary>
    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
