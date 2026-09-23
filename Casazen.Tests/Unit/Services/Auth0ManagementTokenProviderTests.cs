using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RichardSzalay.MockHttp;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class Auth0ManagementTokenProviderTests
{
    private const string Domain = "tenant.eu.auth0.com";
    private const string TokenUrl = "https://tenant.eu.auth0.com/oauth/token";

    [Fact]
    public async Task GetTokenAsync_CalledTwiceBeforeExpiry_RequestsTokenOnce()
    {
        var http = new MockHttpMessageHandler();
        var issued = 0;
        var tokenRequest = http.When(HttpMethod.Post, TokenUrl)
            .Respond(_ => TokenResponse($"tok-{++issued}", expiresIn: 3600));
        var time = new Auth0TestTimeProvider();
        var provider = CreateProvider(http, time, M2MConfig());

        var first = await provider.GetTokenAsync();
        time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.GetTokenAsync();

        Assert.Equal("tok-1", first);
        Assert.Equal("tok-1", second);
        Assert.Equal(1, http.GetMatchCount(tokenRequest));
    }

    [Fact]
    public async Task GetTokenAsync_WithinSafetyMarginOfExpiry_RequestsNewToken()
    {
        var http = new MockHttpMessageHandler();
        var issued = 0;
        var tokenRequest = http.When(HttpMethod.Post, TokenUrl)
            .Respond(_ => TokenResponse($"tok-{++issued}", expiresIn: 3600));
        var time = new Auth0TestTimeProvider();
        var provider = CreateProvider(http, time, M2MConfig());

        var first = await provider.GetTokenAsync();
        // expires_in − 60 s − 1 s: still cached.
        time.Advance(TimeSpan.FromSeconds(3600 - 60 - 1));
        var stillCached = await provider.GetTokenAsync();
        // Inside the last 60 s of validity: renewed.
        time.Advance(TimeSpan.FromSeconds(2));
        var renewed = await provider.GetTokenAsync();

        Assert.Equal("tok-1", first);
        Assert.Equal("tok-1", stillCached);
        Assert.Equal("tok-2", renewed);
        Assert.Equal(2, http.GetMatchCount(tokenRequest));
    }

    [Fact]
    public async Task GetTokenAsync_SendsClientCredentialsGrantForManagementAudience()
    {
        var http = new MockHttpMessageHandler();
        JsonElement? body = null;
        http.When(HttpMethod.Post, TokenUrl).Respond(async request =>
        {
            body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return TokenResponse("tok", expiresIn: 86400);
        });
        var provider = CreateProvider(http, new Auth0TestTimeProvider(), M2MConfig());

        await provider.GetTokenAsync();

        Assert.NotNull(body);
        Assert.Equal("client_credentials", body.Value.GetProperty("grant_type").GetString());
        Assert.Equal("m2m-client", body.Value.GetProperty("client_id").GetString());
        Assert.Equal("m2m-secret", body.Value.GetProperty("client_secret").GetString());
        Assert.Equal($"https://{Domain}/api/v2/", body.Value.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task GetTokenAsync_TokenEndpointFails_ThrowsAndDoesNotCacheFailure()
    {
        var http = new MockHttpMessageHandler();
        var calls = 0;
        http.When(HttpMethod.Post, TokenUrl).Respond(_ =>
            ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"access_denied\"}", Encoding.UTF8, "application/json"),
                }
                : TokenResponse("tok-after-fix", expiresIn: 3600));
        var provider = CreateProvider(http, new Auth0TestTimeProvider(), M2MConfig());

        var ex = await Assert.ThrowsAsync<Auth0ManagementTokenException>(() => provider.GetTokenAsync());
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("m2m-secret", ex.Message);

        Assert.Equal("tok-after-fix", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task Invalidate_AfterTokenCached_ForcesNewTokenRequest()
    {
        var http = new MockHttpMessageHandler();
        var issued = 0;
        http.When(HttpMethod.Post, TokenUrl).Respond(_ => TokenResponse($"tok-{++issued}", expiresIn: 3600));
        var provider = CreateProvider(http, new Auth0TestTimeProvider(), M2MConfig());

        await provider.GetTokenAsync();
        provider.Invalidate();
        var afterInvalidate = await provider.GetTokenAsync();

        Assert.Equal("tok-2", afterInvalidate);
    }

    [Fact]
    public async Task GetTokenAsync_OnlyLegacyStaticToken_ReturnsItAndLogsDeprecationOnce()
    {
        var http = new MockHttpMessageHandler();
        var tokenRequest = http.When(HttpMethod.Post, TokenUrl).Respond(_ => TokenResponse("unused", 3600));
        var logger = new Auth0TestLogger<Auth0ManagementTokenProvider>();
        var provider = CreateProvider(http, new Auth0TestTimeProvider(), new Dictionary<string, string?>
        {
            ["Auth0:Domain"] = Domain,
            ["Auth0:ManagementApiToken"] = "legacy-static-token",
        }, logger);

        Assert.True(provider.IsConfigured);
        Assert.Equal("legacy-static-token", await provider.GetTokenAsync());
        Assert.Equal("legacy-static-token", await provider.GetTokenAsync());

        Assert.Equal(0, http.GetMatchCount(tokenRequest));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("deprecated", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy-static-token", warning.Message);
    }

    [Fact]
    public async Task GetTokenAsync_ClientCredentialsConfigured_PreferredOverLegacyToken()
    {
        var http = new MockHttpMessageHandler();
        http.When(HttpMethod.Post, TokenUrl).Respond(_ => TokenResponse("fresh-m2m", 3600));
        var config = M2MConfig();
        config["Auth0:ManagementApiToken"] = "legacy-static-token";
        var provider = CreateProvider(http, new Auth0TestTimeProvider(), config);

        Assert.Equal("fresh-m2m", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_NothingConfigured_IsNotConfiguredAndThrows()
    {
        var provider = CreateProvider(new MockHttpMessageHandler(), new Auth0TestTimeProvider(), new Dictionary<string, string?>
        {
            ["Auth0:Domain"] = Domain,
        });

        Assert.False(provider.IsConfigured);
        await Assert.ThrowsAsync<Auth0ManagementTokenException>(() => provider.GetTokenAsync());
    }

    [Fact]
    public void Domain_ManagementApiDomainSet_TakesPrecedenceOverLoginDomain()
    {
        var config = M2MConfig();
        config["Auth0:Domain"] = "login.casazen.example";
        config["Auth0:ManagementApiDomain"] = "https://tenant.eu.auth0.com/";
        var provider = CreateProvider(new MockHttpMessageHandler(), new Auth0TestTimeProvider(), config);

        Assert.Equal(Domain, provider.Domain);
    }

    private static Dictionary<string, string?> M2MConfig() => new()
    {
        ["Auth0:Domain"] = Domain,
        ["Auth0:ManagementClientId"] = "m2m-client",
        ["Auth0:ManagementClientSecret"] = "m2m-secret",
    };

    private static Auth0ManagementTokenProvider CreateProvider(
        MockHttpMessageHandler http,
        TimeProvider time,
        Dictionary<string, string?> settings,
        ILogger<Auth0ManagementTokenProvider>? logger = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(http, disposeHandler: false));
        return new Auth0ManagementTokenProvider(
            configuration,
            factory.Object,
            time,
            logger ?? new Auth0TestLogger<Auth0ManagementTokenProvider>());
    }

    private static HttpResponseMessage TokenResponse(string token, int expiresIn) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { access_token = token, expires_in = expiresIn, token_type = "Bearer" }),
                Encoding.UTF8,
                "application/json"),
        };
}

internal sealed class Auth0TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class Auth0TestLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
