using System.Net;
using System.Text;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F01/US01/T02, ADR-023-R5, R13: the partner token of the application (the login that the portal catalog uses). Data rule: the client
/// secret and the token never reach a log line, whatever Octorate answers (a success, a refusal that repeats what was sent, a failure, a
/// network error that names the secret). The same run shows what the cache does: the token is reused while valid and renewed five minutes
/// before it expires. The HTTP answer is a stub: what is proven is what the service logs and keeps, not Octorate.
/// </summary>
public class OctoratePartnerTokenLoggingTests
{
    private const string ClientSecret = "cs-partner-secret-4f9a1c7e2b";
    private const string FirstToken = "PT-first-3c1d9e5a7b";
    private const string SecondToken = "PT-second-8b6f4d2a0c";
    private const int ExpiresInSeconds = 129599;

    [Fact]
    public async Task OctoratePartnerToken_SecretAndToken_NeverLogged_Test()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 3, 30, 0, TimeSpan.Zero));
        var octorate = new LoginEndpoint();
        var logs = new CapturingLogger<OctorateTokenService>();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"partner-token-{Guid.NewGuid()}").Options);
        var tokens = NewService(db, octorate, clock, logs);

        // The first call logs in: the application credentials go to the partner login, and the token comes back.
        octorate.Answer(HttpStatusCode.OK, Login(FirstToken));
        Assert.Equal(FirstToken, await tokens.GetPartnerAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, octorate.Calls);
        Assert.Equal("https://api.octorate.test/connect/rest/v1/identity/apilogin", octorate.LastAddress);
        Assert.Contains("client_id=casazen-test-client", octorate.LastForm, StringComparison.Ordinal);

        // Reused while valid: up to five minutes before it expires, nothing goes to Octorate.
        clock.Advance(TimeSpan.FromSeconds(ExpiresInSeconds - 300 - 1));
        Assert.Equal(FirstToken, await tokens.GetPartnerAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, octorate.Calls);

        // Renewed once it expires within five minutes.
        clock.Advance(TimeSpan.FromSeconds(1));
        octorate.Answer(HttpStatusCode.OK, Login(SecondToken));
        Assert.Equal(SecondToken, await tokens.GetPartnerAccessTokenAsync(CancellationToken.None));
        Assert.Equal(2, octorate.Calls);

        // A refusal that repeats what it was sent, a failure of Octorate, an answer that cannot be used and a network error that names the
        // secret: each is the typed error, none of them puts the secret or a token in its message or in the log.
        var failures = new List<(string Name, Action Script, Type Expected)>
        {
            ("refused", () => octorate.Answer(HttpStatusCode.Forbidden, $$"""{"type":"ApiSecurityException","message":"client_secret {{ClientSecret}} is not valid, token {{SecondToken}}"}"""), typeof(OctorateAuthException)),
            ("server", () => octorate.Answer(HttpStatusCode.InternalServerError, $"<html>{ClientSecret} {SecondToken}</html>"), typeof(OctorateServerException)),
            ("no token", () => octorate.Answer(HttpStatusCode.OK, $$"""{"note":"{{ClientSecret}}"}"""), typeof(OctorateServerException)),
            ("network", () => octorate.Fail(new HttpRequestException($"connection reset, client_secret={ClientSecret}")), typeof(OctorateServerException)),
        };
        foreach (var (name, script, expected) in failures)
        {
            // The cached token has expired, so the next call logs in again, and the login fails.
            clock.Advance(TimeSpan.FromHours(37));
            script();
            var calls = octorate.Calls;

            var error = await Assert.ThrowsAnyAsync<OctorateException>(() => tokens.GetPartnerAccessTokenAsync(CancellationToken.None));
            Assert.True(expected == error.GetType(), $"{name}: the error is {error.GetType().Name}, expected {expected.Name}");
            Assert.Equal(calls + 1, octorate.Calls);
            Assert.DoesNotContain(ClientSecret, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SecondToken, error.Message, StringComparison.Ordinal);
        }

        // Without a client id and secret nothing leaves: the message names the two settings and holds no value.
        var unconfigured = NewService(db, octorate, clock, logs, withCredentials: false);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => unconfigured.GetPartnerAccessTokenAsync(CancellationToken.None));
        Assert.Contains("Octorate:ClientSecret", missing.Message, StringComparison.Ordinal);

        // The proof: the log has the lines of the renewals and of the refusal, and not once the secret or a token.
        Assert.Contains(logs.Entries, entry => entry.Contains("OctoratePartnerTokenRenewed", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry => entry.Contains("refused the partner login", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(ClientSecret, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(FirstToken, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(SecondToken, entry, StringComparison.Ordinal);
        });
    }

    private static string Login(string token) => $$"""{"access_token":"{{token}}","expires_in":{{ExpiresInSeconds}}}""";

    private static OctorateTokenService NewService(
        AppDbContext db,
        LoginEndpoint octorate,
        TimeProvider clock,
        ILogger<OctorateTokenService> logger,
        bool withCredentials = true)
    {
        var settings = new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" };
        if (withCredentials)
        {
            settings["Octorate:ClientId"] = "casazen-test-client";
            settings["Octorate:ClientSecret"] = ClientSecret;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");

        // One cache for the application, as the container gives it: a service made for another request shares the token.
        return new OctorateTokenService(
            db, NullTenantContext.Instance, new StubHttpClientFactory(octorate), configuration, host.Object, clock, logger, SharedCache);
    }

    private static readonly OctoratePartnerTokenCache SharedCache = new();

    /// <summary>The partner login of Octorate: answers what the test says (or fails as it says) and keeps what it was sent.</summary>
    private sealed class LoginEndpoint : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _respond = () => new HttpResponseMessage(HttpStatusCode.OK);

        public int Calls { get; private set; }

        public string LastForm { get; private set; } = string.Empty;

        public string LastAddress { get; private set; } = string.Empty;

        public void Answer(HttpStatusCode status, string body) =>
            _respond = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public void Fail(Exception exception) => _respond = () => throw exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAddress = request.RequestUri!.ToString();
            LastForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            return _respond();
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Keeps every line the service writes, as the sink would: the message, the structured values and the exception with its inner ones.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            Entries.Add($"{logLevel} {eventId.Name} {formatter(state, exception)} | {values} | {exception}");
        }
    }
}
