using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Thrown when no Management API token can be obtained. The message never contains credentials.
/// </summary>
public sealed class Auth0ManagementTokenException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Supplies access tokens for the Auth0 Management API.
/// </summary>
public interface IAuth0ManagementTokenProvider
{
    /// <summary>True when an M2M client (or the deprecated static token) is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Management API tenant domain (e.g. <c>tenant.eu.auth0.com</c>), or null when missing.</summary>
    string? Domain { get; }

    /// <summary>Returns a valid token, fetching a new one via client credentials when the cached one expires.</summary>
    /// <exception cref="Auth0ManagementTokenException">The token endpoint failed or nothing is configured.</exception>
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the cached token (e.g. after the Management API answered 401).</summary>
    void Invalidate();
}

/// <summary>
/// Obtains Management API tokens with the OAuth2 client-credentials grant
/// (<c>Auth0:ManagementClientId</c> / <c>Auth0:ManagementClientSecret</c>) and caches each token
/// until <c>expires_in</c> minus 60 seconds. Registered as a singleton so the cache is process-wide.
/// The legacy static <c>Auth0:ManagementApiToken</c> is still honoured when no M2M client is
/// configured, with a deprecation warning, because such tokens expire and cannot be renewed.
/// </summary>
public sealed class Auth0ManagementTokenProvider(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<Auth0ManagementTokenProvider> logger) : IAuth0ManagementTokenProvider
{
    public const string HttpClientName = "Auth0Management";

    /// <summary>Tokens are renewed this long before Auth0 says they expire.</summary>
    public static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenValidUntil = DateTimeOffset.MinValue;
    private int _legacyWarningLogged;

    public string? Domain
    {
        get
        {
            var domain = configuration["Auth0:ManagementApiDomain"];
            if (string.IsNullOrWhiteSpace(domain))
                domain = configuration["Auth0:Domain"];
            return NormalizeDomain(domain);
        }
    }

    public bool IsConfigured =>
        Domain is not null && (HasClientCredentials || !string.IsNullOrWhiteSpace(LegacyToken));

    private string? ClientId => configuration["Auth0:ManagementClientId"];

    private string? ClientSecret => configuration["Auth0:ManagementClientSecret"];

    private string? LegacyToken => configuration["Auth0:ManagementApiToken"];

    private bool HasClientCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var domain = Domain
            ?? throw new Auth0ManagementTokenException("Auth0 Management API domain is not configured.");

        if (!HasClientCredentials)
        {
            var legacy = LegacyToken;
            if (string.IsNullOrWhiteSpace(legacy))
                throw new Auth0ManagementTokenException("Auth0 Management API credentials are not configured.");

            if (Interlocked.Exchange(ref _legacyWarningLogged, 1) == 0)
            {
                logger.LogWarning(
                    "Auth0 Management API: using the deprecated static Auth0:ManagementApiToken. " +
                    "It expires and cannot be renewed; configure Auth0:ManagementClientId and " +
                    "Auth0:ManagementClientSecret (see docs/runbooks/auth0.md).");
            }

            return legacy;
        }

        if (TryGetCached(out var cached))
            return cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(out cached))
                return cached;

            var (token, expiresIn) = await RequestTokenAsync(domain, cancellationToken);
            var lifetime = expiresIn - ExpirySafetyMargin;
            _cachedToken = token;
            _cachedTokenValidUntil = lifetime > TimeSpan.Zero
                ? timeProvider.GetUtcNow() + lifetime
                : DateTimeOffset.MinValue;

            logger.LogInformation(
                "Auth0 Management API: obtained client-credentials token valid for {ExpiresInSeconds}s",
                (int)expiresIn.TotalSeconds);
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate()
    {
        _cachedToken = null;
        _cachedTokenValidUntil = DateTimeOffset.MinValue;
    }

    private bool TryGetCached(out string token)
    {
        var current = _cachedToken;
        if (current is not null && timeProvider.GetUtcNow() < _cachedTokenValidUntil)
        {
            token = current;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private async Task<(string Token, TimeSpan ExpiresIn)> RequestTokenAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var request = new ClientCredentialsRequest(
            GrantType: "client_credentials",
            ClientId: ClientId!,
            ClientSecret: ClientSecret!,
            Audience: $"https://{domain}/api/v2/");

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.PostAsJsonAsync($"https://{domain}/oauth/token", request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new Auth0ManagementTokenException("Auth0 token endpoint unreachable.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Do not log the body: it may echo request details.
                throw new Auth0ManagementTokenException(
                    $"Auth0 token endpoint returned HTTP {(int)response.StatusCode}.");
            }

            TokenResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new Auth0ManagementTokenException("Auth0 token endpoint returned an invalid payload.", ex);
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                throw new Auth0ManagementTokenException("Auth0 token endpoint returned no access token.");

            return (payload.AccessToken, TimeSpan.FromSeconds(Math.Max(0, payload.ExpiresIn)));
        }
    }

    private static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var trimmed = domain.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["https://".Length..];
        return trimmed.TrimEnd('/');
    }

    private sealed record ClientCredentialsRequest(
        [property: JsonPropertyName("grant_type")] string GrantType,
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_secret")] string ClientSecret,
        [property: JsonPropertyName("audience")] string Audience);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string? TokenType);
}
