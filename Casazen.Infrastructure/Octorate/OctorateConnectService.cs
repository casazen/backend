using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctorateConnectService"/> (ADR-023-R1, R2, R6, R13; section 9.1): the consent address with its state, the server-to-server
/// exchange of the code (<c>POST {ApiBaseUrl}/rest/v1/identity/token</c>, form: <c>client_id</c>, <c>client_secret</c>,
/// <c>redirect_uri</c>, <c>grant_type=code</c>, <c>code</c>), the tokens stored encrypted at once (the refresh token arrives only with the
/// first authorization), the environment check right after the exchange, and the status of the connection.
/// </summary>
/// <remarks>Never logs or returns the code, the state or a token: the log lines carry the org, the environment and counts.</remarks>
public sealed class OctorateConnectService(
    AppDbContext db,
    OctorateOAuthStateService states,
    IOctorateTokenService tokens,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IHttpClientFactory httpClientFactory,
    IOptions<OctorateOptions> options,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<OctorateConnectService> logger) : IOctorateConnectService
{
    /// <summary>What is left of the quota, as a share of it, under which the status warns.</summary>
    public const double QuotaWarningShare = 0.10;

    private const string TokenPath = "/rest/v1/identity/token";

    private OctorateEnvironment Environment => OctorateEnvironments.Of(hostEnvironment);

    public async Task<string> StartAsync(Guid orgId, string userId, CancellationToken ct)
    {
        var settings = Settings();
        var state = await states.CreateAsync(orgId, userId, settings.RedirectUri, ct);
        var separator = settings.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        logger.LogInformation("Octorate consent started for org {OrgId} ({Environment})", orgId, Environment);
        return $"{settings.AuthorizationEndpoint}{separator}client_id={Uri.EscapeDataString(settings.ClientId)}"
               + $"&redirect_uri={Uri.EscapeDataString(settings.RedirectUri)}&state={Uri.EscapeDataString(state)}";
    }

    public async Task<OctorateConnectResult> CompleteAsync(Guid orgId, string userId, string code, string state, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var settings = Settings();

        var environment = await states.ConsumeAsync(state, orgId, userId, settings.RedirectUri, ct);
        if (environment != Environment)
            throw new OctorateWrongEnvironmentException(null);

        var exchanged = await ExchangeAsync(settings, code, ct);
        await tokens.SaveTokensAsync(orgId, exchanged, ct);

        // The refresh token arrives only with the first authorization: a reconnection reuses the stored one. With neither, the
        // connection could not outlive its access token, so it is not opened.
        var connection = await db.OctorateConnections.FirstOrDefaultAsync(c => c.OrgId == orgId && c.Environment == environment, ct);
        if (connection is not { Status: OctorateConnectionStatus.Connected })
        {
            logger.LogWarning("Octorate connection of org {OrgId}: the exchange gave no refresh token and none is stored", orgId);
            throw new OctorateExchangeFailedException(null);
        }

        // Right after the exchange (NF-28): credentials of the other environment leave the connection disconnected, never silently on.
        // An environment that cannot be verified (Octorate refused or failed the call) is not taken on trust either.
        try
        {
            OctorateConfiguration configuration;
            using (orgScope.Use(orgId))
                configuration = await client.GetConfigurationAsync(ct);
            OctorateEnvironments.EnsureMatches(environment, configuration);
        }
        catch (OctorateException ex)
        {
            await tokens.DisconnectAsync(orgId, ct);
            logger.LogWarning(
                "Octorate connection of org {OrgId}: the {Environment} environment of the credentials not verified ({Code}), left disconnected",
                orgId, environment, ex.Code);
            throw;
        }

        OctorateProperty[] properties;
        using (orgScope.Use(orgId))
            properties = await client.GetPropertiesAsync(ct);

        var now = Now();
        connection.OctorateAccountId = AccountIdOf(properties);
        connection.LastSyncedAt = now;
        connection.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Octorate connection of org {OrgId} completed ({Environment}): the consent covers {Count} accommodations",
            orgId, environment, properties.Length);
        return new OctorateConnectResult(connection.Status, connection.Environment, properties.Length);
    }

    public async Task<OctorateConnectionState> GetStatusAsync(Guid orgId, CancellationToken ct)
    {
        var environment = Environment;
        var connection = await FindAsync(orgId, environment, ct);
        if (connection is null)
            return new OctorateConnectionState(OctorateConnectionStatus.Disconnected, environment, null, null, null, null, false);

        int? quotaLimit = null;
        int? quotaRemaining = null;
        if (connection.Status == OctorateConnectionStatus.Connected)
        {
            try
            {
                using (orgScope.Use(orgId))
                {
                    var configuration = await client.GetConfigurationAsync(ct);
                    quotaLimit = configuration.QuotaLimit;
                    quotaRemaining = configuration.QuotaStatus;
                }
            }
            catch (OctorateException ex)
            {
                // The status still answers: the quota is simply not known now.
                logger.LogInformation("Octorate quota of org {OrgId} not read ({Code})", orgId, ex.Code);
            }

            // Reading the quota may have refreshed the token, or found the refresh token refused and disconnected the org.
            connection = await FindAsync(orgId, environment, ct) ?? connection;
        }

        var warning = quotaLimit is > 0 && quotaRemaining is { } left && left <= quotaLimit.Value * QuotaWarningShare;
        return new OctorateConnectionState(
            connection.Status,
            connection.Environment,
            connection.OctorateAccountId,
            connection.LastSyncedAt,
            quotaLimit,
            quotaRemaining,
            warning);
    }

    public Task DisconnectAsync(Guid orgId, CancellationToken ct) => tokens.DisconnectAsync(orgId, ct);

    /// <summary>
    /// Which account or network the consent covers, for display only: the network of the accommodations when they all share one, the
    /// accommodation when the consent covers exactly one, otherwise none.
    /// </summary>
    internal static string? AccountIdOf(IReadOnlyList<OctorateProperty> properties)
    {
        var networks = properties.Select(p => p.NetworkId).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        var value = networks.Count == 1 && properties.All(p => p.NetworkId == networks[0])
            ? networks[0]
            : properties.Count == 1 ? properties[0].Id : null;
        return value is null || value.Length <= OctorateConnection.AccountIdMaxLength ? value : value[..OctorateConnection.AccountIdMaxLength];
    }

    /// <summary>The code exchange, server to server. Any failure is <see cref="OctorateExchangeFailedException"/> (502).</summary>
    private async Task<OctorateTokenResponse> ExchangeAsync(ConnectSettings settings, string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OctorateHttp.Address(settings.ApiBaseUrl, TokenPath))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["redirect_uri"] = settings.RedirectUri,
                ["grant_type"] = "code",
                ["code"] = code.Trim(),
            }),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(OctorateTokenService.HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OctorateExchangeFailedException(null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OctorateExchangeFailedException(null, ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await OctorateHttp.ReadBodyAsync(response, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorType = OctorateHttp.ReadError(body).Type;
                logger.LogWarning("Octorate code exchange refused: HTTP {StatusCode} ({ErrorType})", status, errorType ?? "no type");
                if (string.Equals(errorType, OctorateWrongEnvironmentException.OctorateErrorType, StringComparison.OrdinalIgnoreCase))
                    throw new OctorateWrongEnvironmentException(status);

                throw new OctorateExchangeFailedException(status);
            }

            OctorateTokenResponse exchanged;
            try
            {
                exchanged = OctorateHttp.ReadTokens(body, Now(), status);
            }
            catch (OctorateServerException ex)
            {
                throw new OctorateExchangeFailedException(status, ex);
            }

            if (string.IsNullOrEmpty(exchanged.AccessToken))
            {
                logger.LogWarning("Octorate code exchange answered HTTP {StatusCode} without an access token", status);
                throw new OctorateExchangeFailedException(status);
            }

            return exchanged;
        }
    }

    private Task<OctorateConnection?> FindAsync(Guid orgId, OctorateEnvironment environment, CancellationToken ct) =>
        db.OctorateConnections.AsNoTracking().FirstOrDefaultAsync(c => c.OrgId == orgId && c.Environment == environment, ct);

    /// <summary>The settings of the consent, all required: without them no consent can start or complete.</summary>
    /// <exception cref="InvalidOperationException">A setting is missing (startup validation covers the environments with the flag on).</exception>
    private ConnectSettings Settings()
    {
        var value = options.Value;
        var clientId = value.ClientId?.Trim();
        var clientSecret = value.ClientSecret?.Trim();
        var endpoint = value.AuthorizationEndpoint?.Trim();
        var redirect = value.RedirectUri?.Trim();
        var apiBaseUrl = value.ApiBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)
            || !IsHttps(endpoint) || !IsHttps(redirect) || !IsHttps(apiBaseUrl))
        {
            throw new InvalidOperationException(
                "The Octorate consent is not configured: Octorate__ClientId, Octorate__ClientSecret, Octorate__ApiBaseUrl, "
                + "Octorate__AuthorizationEndpoint and Octorate__RedirectUri are required (docs/runbooks/deploy-checklist.md).");
        }

        return new ConnectSettings(clientId, clientSecret, endpoint!, redirect!, apiBaseUrl!);
    }

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record ConnectSettings(string ClientId, string ClientSecret, string AuthorizationEndpoint, string RedirectUri, string ApiBaseUrl);
}
