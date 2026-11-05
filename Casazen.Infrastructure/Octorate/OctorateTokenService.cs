using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctorateTokenService"/> on <see cref="OctorateConnection"/> (ADR-023-R1, R2, R12, R13). The tokens are encrypted by the
/// EF value converter of their columns (<c>EncryptedColumns</c>, purpose <c>Casazen.Octorate.OAuth</c>): this class reads and writes the
/// clear value through EF and never encrypts by hand.
/// </summary>
/// <remarks>
/// <para><b>Refresh.</b> <c>POST {Octorate:ApiBaseUrl}/rest/v1/identity/refresh</c> with the application credentials
/// (<c>Octorate:ClientId</c>, <c>Octorate:ClientSecret</c>) and the refresh token, through an <see cref="HttpClient"/> of its own (the
/// API client takes its token from here). It answers a new access token and no new refresh token. A new access token invalidates the
/// previous one when that was never used, so the refresh is single-flight per connection: it runs under the PostgreSQL advisory lock
/// <see cref="PostgresAdvisoryLocks.Scope.OctorateTokenRefresh"/> of the connection, reads the connection again inside the lock
/// (another instance may have refreshed it already) and saves the new token before the lock is released.</para>
/// <para><b>Refusal.</b> A refresh token Octorate refuses (400, 401 or 403: <c>errRefreshTokenInvalid</c>, <c>invalid_grant</c>) puts the
/// connection in <see cref="OctorateConnectionStatus.Disconnected"/> and forgets the tokens, with no retry: the host connects again (the
/// "Reconnect" To do is raised from this status). A failure of Octorate (5xx, timeout) changes nothing and is thrown, for a later retry.</para>
/// <para><b>Audit.</b> Every token handed out writes the structured line <c>OctorateTokenAccessed</c> with the org and the connection id,
/// never the token nor an answer of Octorate.</para>
/// </remarks>
public sealed class OctorateTokenService(
    AppDbContext db,
    ITenantContext tenantContext,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<OctorateTokenService> logger) : IOctorateTokenService
{
    /// <summary>Name of the <see cref="HttpClient"/> of the refresh call.</summary>
    public const string HttpClientName = "Octorate.Identity";

    /// <summary>Configuration key of the application's client id (<c>Octorate__ClientId</c>).</summary>
    public const string ClientIdKey = "Octorate:ClientId";

    /// <summary>Configuration key of the application's client secret (<c>Octorate__ClientSecret</c>, never committed).</summary>
    public const string ClientSecretKey = "Octorate:ClientSecret";

    /// <summary>A token that expires within this window is refreshed before it is handed out (ADR-023-R2).</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    private const string RefreshPath = "/rest/v1/identity/refresh";

    private static readonly EventId TokenAccessedEvent = new(52_001, "OctorateTokenAccessed");

    private OctorateEnvironment Environment => OctorateEnvironments.Of(hostEnvironment);

    public async Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct)
    {
        EnsureCallerOrg(orgId);
        var connection = await FindAsync(orgId, ct);
        if (connection is not { Status: OctorateConnectionStatus.Connected })
            throw OctorateAuthException.NotConnected();

        if (connection.EncryptedAccessToken is { Length: > 0 } token && !ExpiresSoon(connection))
        {
            LogAccess(orgId, connection.Id);
            return token;
        }

        return await RefreshAsync(orgId, rejectedAccessToken: null, ct);
    }

    public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(rejectedAccessToken);
        EnsureCallerOrg(orgId);
        return RefreshAsync(orgId, rejectedAccessToken, ct);
    }

    public async Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        EnsureCallerOrg(orgId);
        if (string.IsNullOrEmpty(tokens.AccessToken) && string.IsNullOrEmpty(tokens.RefreshToken))
            throw new ArgumentException("Octorate answered no token to save.", nameof(tokens));

        // The refresh token first: it arrives once, so it is in the database before anything else can fail.
        var connection = await SaveRefreshTokenAsync(orgId, tokens.RefreshToken, ct);

        if (!string.IsNullOrEmpty(tokens.AccessToken))
        {
            connection.EncryptedAccessToken = tokens.AccessToken;
            connection.TokenExpiresAt = tokens.ExpiresAt;
        }

        // Usable once a refresh token is stored: the access token alone would die with its expiry.
        if (!string.IsNullOrEmpty(connection.EncryptedRefreshToken))
            connection.Status = OctorateConnectionStatus.Connected;

        connection.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Octorate tokens saved for org {OrgId}, connection {ConnectionId} ({Environment}, {Status})",
            orgId, connection.Id, connection.Environment, connection.Status);
    }

    public async Task DisconnectAsync(Guid orgId, CancellationToken ct)
    {
        EnsureCallerOrg(orgId);
        var connection = await FindAsync(orgId, ct);
        if (connection is null)
            return;

        // The access token goes; the refresh token stays encrypted and unused while disconnected, because Octorate gives it only with
        // the first authorization: the reconnection (the host sent to the consent page again) reuses it. A refusal of Octorate
        // forgets it (DisconnectRefusedAsync).
        connection.EncryptedAccessToken = null;
        connection.TokenExpiresAt = null;
        connection.Status = OctorateConnectionStatus.Disconnected;
        connection.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Octorate connection {ConnectionId} of org {OrgId} disconnected, access token forgotten", connection.Id, orgId);
    }

    /// <summary>
    /// The refresh, under the advisory lock of the connection. <paramref name="rejectedAccessToken"/>: the token Octorate refused (a
    /// forced refresh), or <c>null</c> for one that expires soon.
    /// </summary>
    private async Task<string> RefreshAsync(Guid orgId, string? rejectedAccessToken, CancellationToken ct)
    {
        var connection = await FindAsync(orgId, ct) ?? throw OctorateAuthException.NotConnected();

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OctorateTokenRefresh, connection.Id.ToString("N")));

        // Inside the lock: what another instance may have written while this one waited.
        await db.Entry(connection).ReloadAsync(ct);
        if (connection.Status != OctorateConnectionStatus.Connected)
            throw OctorateAuthException.NotConnected();

        if (connection.EncryptedAccessToken is { Length: > 0 } current
            && (rejectedAccessToken is null ? !ExpiresSoon(connection) : !SameToken(current, rejectedAccessToken)))
        {
            // Refreshed by another caller meanwhile: its token is the one to use, nothing is written.
            LogAccess(orgId, connection.Id);
            return current;
        }

        if (connection.EncryptedRefreshToken is not { Length: > 0 } refreshToken)
        {
            await DisconnectRefusedAsync(connection, transaction, octorateStatusCode: null, errorType: "no_refresh_token", ct);
            throw OctorateAuthException.NotConnected();
        }

        var outcome = await CallRefreshAsync(refreshToken, ct);
        if (outcome.Tokens is not { } tokens)
        {
            await DisconnectRefusedAsync(connection, transaction, outcome.StatusCode, outcome.ErrorType, ct);
            throw OctorateAuthException.Refused(outcome.StatusCode);
        }

        // A success with no access token is an answer that cannot be used, not a refusal: nothing changes, a later call retries.
        if (tokens.AccessToken is not { Length: > 0 } accessToken)
            throw new OctorateServerException(outcome.StatusCode);

        connection.EncryptedAccessToken = accessToken;
        connection.TokenExpiresAt = tokens.ExpiresAt;
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            connection.EncryptedRefreshToken = tokens.RefreshToken;
        connection.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Octorate access token refreshed for org {OrgId}, connection {ConnectionId}, valid until {ExpiresAt:O}",
            orgId, connection.Id, tokens.ExpiresAt);
        LogAccess(orgId, connection.Id);
        return accessToken;
    }

    /// <summary>
    /// The refresh call. A refusal (400, 401, 403) comes back as an outcome without tokens; a failure of Octorate (5xx, 429, timeout,
    /// network, an answer that cannot be read) is thrown as <see cref="OctorateServerException"/> and changes nothing.
    /// </summary>
    private async Task<RefreshOutcome> CallRefreshAsync(string refreshToken, CancellationToken ct)
    {
        var clientId = configuration[ClientIdKey];
        var clientSecret = configuration[ClientSecretKey];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                $"{ClientIdKey} and {ClientSecretKey} are not configured (Octorate__ClientId, Octorate__ClientSecret): no token can be refreshed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OctorateHttp.Address(OctorateHttp.ApiBaseUrl(configuration), RefreshPath))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
            }),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OctorateServerException(null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The 30-second timeout of the client.
            throw new OctorateServerException(null, ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await OctorateHttp.ReadBodyAsync(response, ct);
            if (response.IsSuccessStatusCode)
                return new RefreshOutcome(OctorateHttp.ReadTokens(body, Now(), status), status, null);

            if (status is 400 or 401 or 403)
                return new RefreshOutcome(null, status, OctorateHttp.ReadError(body).Type);

            throw new OctorateServerException(status);
        }
    }

    /// <summary>The refresh token is gone or refused: the connection is disconnected for good (ADR-023-R12), with no retry.</summary>
    private async Task DisconnectRefusedAsync(
        OctorateConnection connection,
        IDbContextTransaction? transaction,
        int? octorateStatusCode,
        string? errorType,
        CancellationToken ct)
    {
        Forget(connection);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogWarning(
            "Octorate refused the refresh of connection {ConnectionId} of org {OrgId} (HTTP {StatusCode}, {ErrorType}): disconnected, the host has to connect again",
            connection.Id, connection.OrgId, octorateStatusCode, errorType ?? "unknown");
    }

    /// <summary>
    /// The connection of the org in this environment with <paramref name="refreshToken"/> stored and saved (when given): created when the
    /// org has none. Two first saves at the same time: the second loses the race on the unique index and writes into the first's row.
    /// </summary>
    private async Task<OctorateConnection> SaveRefreshTokenAsync(Guid orgId, string? refreshToken, CancellationToken ct)
    {
        var connection = await FindAsync(orgId, ct);
        if (connection is not null)
        {
            if (!string.IsNullOrEmpty(refreshToken))
            {
                connection.EncryptedRefreshToken = refreshToken;
                connection.UpdatedAt = Now();
                await db.SaveChangesAsync(ct);
            }

            return connection;
        }

        var now = Now();
        connection = new OctorateConnection
        {
            OrgId = orgId,
            Environment = Environment,
            Status = OctorateConnectionStatus.Disconnected,
            EncryptedRefreshToken = string.IsNullOrEmpty(refreshToken) ? null : refreshToken,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OctorateConnections.Add(connection);
        try
        {
            await db.SaveChangesAsync(ct);
            return connection;
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OctorateConnection.OrgEnvironmentIndexName))
        {
            db.Entry(connection).State = EntityState.Detached;
            var winner = await FindAsync(orgId, ct)
                ?? throw new InvalidOperationException("The Octorate connection that won the race is not readable.", ex);
            if (!string.IsNullOrEmpty(refreshToken))
            {
                winner.EncryptedRefreshToken = refreshToken;
                winner.UpdatedAt = Now();
                await db.SaveChangesAsync(ct);
            }

            return winner;
        }
    }

    private Task<OctorateConnection?> FindAsync(Guid orgId, CancellationToken ct)
    {
        var environment = Environment;
        return db.OctorateConnections.FirstOrDefaultAsync(c => c.OrgId == orgId && c.Environment == environment, ct);
    }

    /// <summary>
    /// Inside an authenticated request the org is the request's: another org's tokens are refused (fail closed, also when the request
    /// has no org). Jobs and webhook handlers run without the filter and name the org they work for.
    /// </summary>
    private void EnsureCallerOrg(Guid orgId)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("An Octorate connection belongs to an organization.", nameof(orgId));

        if (tenantContext.FilterEnabled && tenantContext.OrgId != orgId)
        {
            throw new UnauthorizedAccessException(
                "The Octorate connection of another organization is not reachable from this request.");
        }
    }

    private void Forget(OctorateConnection connection)
    {
        connection.EncryptedAccessToken = null;
        connection.EncryptedRefreshToken = null;
        connection.TokenExpiresAt = null;
        connection.Status = OctorateConnectionStatus.Disconnected;
        connection.UpdatedAt = Now();
    }

    private bool ExpiresSoon(OctorateConnection connection) =>
        connection.TokenExpiresAt is not { } expiresAt || expiresAt <= Now() + RefreshWindow;

    private static bool SameToken(string stored, string rejected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(rejected));

    private void LogAccess(Guid orgId, Guid connectionId) =>
        logger.LogInformation(
            TokenAccessedEvent, "OctorateTokenAccessed org {OrgId} connection {ConnectionId}", orgId, connectionId);

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>What the refresh call answered: the tokens, or a refusal with Octorate's status and error type.</summary>
    private sealed record RefreshOutcome(OctorateTokenResponse? Tokens, int? StatusCode, string? ErrorType);
}
