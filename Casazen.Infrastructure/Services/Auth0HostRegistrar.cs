using System.Net;
using System.Text.RegularExpressions;
using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Auth0Application = Auth0.ManagementApi.Models.Client;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Section <c>Auth0:HostSync</c> (Railway <c>Auth0__HostSync__ClientId</c>, E40/F01/US02/T01, NF-141): the SPA application of <b>this</b> environment whose
/// callback, logout and web-origin lists <see cref="Auth0HostRegistrar"/> changes. Not a secret: it is the client id the web app sends to every browser
/// (<c>VITE_AUTH0_CLIENT_ID</c> of the same environment). Needed only with the flag <c>Auth0HostSync</c> on; without it the registrar answers
/// <see cref="Auth0HostRegistrationResult.NotConfigured"/> and calls nothing, never a startup failure. The Management credentials stay in
/// <c>Auth0:ManagementClientId</c> / <c>Auth0:ManagementClientSecret</c> (<see cref="IAuth0ManagementTokenProvider"/>). Runbook <c>docs/runbooks/auth0.md</c> § 13.
/// </summary>
public sealed class Auth0HostSyncOptions
{
    public const string SectionName = "Auth0:HostSync";

    /// <summary>The client id of the SPA application of the environment (<c>Auth0:HostSync:ClientId</c>); empty: the registration is not configured.</summary>
    public string? ClientId { get; set; }
}

/// <summary>
/// The turn of the registrations of the hosts inside one process (E40/F01/US02/T01): a singleton, so every <see cref="Auth0HostRegistrar"/> of the process shares it. The advisory lock
/// serializes the instances through the database; this keeps the registrations of one instance from queueing on the lock with a pooled connection each (the pool of the EF contexts is
/// small, <c>Database:MaxPoolSize</c>, and the one that holds the lock keeps its connection through the calls to Auth0). Waiting here costs no connection.
/// </summary>
public sealed class Auth0HostRegistrationGate(TimeSpan? maxWait = null)
{
    /// <summary>How long a registration waits for its turn before it gives up with <c>busy</c> (the calls to Auth0 of the one in front last a few seconds).</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly TimeSpan _maxWait = maxWait ?? DefaultMaxWait;

    /// <summary>Waits for the turn; false when it did not come within the maximum wait. A cancellation of the caller is an exception.</summary>
    public Task<bool> EnterAsync(CancellationToken cancellationToken) => _turn.WaitAsync(_maxWait, cancellationToken);

    /// <summary>Gives the turn back; only after <see cref="EnterAsync"/> answered true.</summary>
    public void Leave() => _turn.Release();
}

/// <summary>
/// <see cref="IAuth0HostRegistrar"/> on the Auth0 Management API (E40/F01/US02/T01, NF-141, runbook <c>docs/runbooks/auth0.md</c> § 13): it reads the SPA
/// application of the environment (<c>GET /clients/{id}</c>, the three lists only), writes the three lists back in <b>one</b> <c>PATCH /clients/{id}</c> and
/// reads them again. The Management SDK client is made as <see cref="Auth0ManagementService"/> makes it (the cached client-credentials token of
/// <see cref="IAuth0ManagementTokenProvider"/>, the named HTTP client, a new token once on a 401).
/// </summary>
/// <remarks>
/// <para><b>Append only.</b> The origin of a host, <c>https://{host}</c> (the web app's <c>redirect_uri</c> and logout <c>returnTo</c> are the origin), is added at
/// the end of each list that lacks it; no other entry is moved, changed or removed, so the first callback URL stays the platform origin (auth0.md § 1.2 rule 6).
/// A host that is in the three lists already writes nothing. A removal takes away exactly the strings equal to the origin of the host (no prefix, no
/// wildcard), and never the first callback URL. An application with no callback URL at all is refused: the origin of an agency would become the first.</para>
/// <para><b>One write or none.</b> The three lists travel in the same request, so Auth0 applies all of them or none; the application is read again after the
/// write, and the change is <see cref="Auth0HostRegistrationResult.Rejected"/> unless the read-back has it.</para>
/// <para><b>One at a time.</b> The read, the write and the read-back are serialized across the instances by the advisory lock
/// <see cref="PostgresAdvisoryLocks.Scope.Auth0HostRegistration"/> on the client id: two hosts registered at once would otherwise write each other's lists
/// away. A change made by hand in the Auth0 dashboard during those calls is not covered. The lock is taken on a pooled database connection (a small pool:
/// <c>Database:MaxPoolSize</c>) and held through the calls to Auth0, so inside one process the turn is queued first (<see cref="Auth0HostRegistrationGate"/>): only the
/// registration whose turn it is holds a connection, the others wait with none, and one that waits too long answers <see cref="Auth0HostRegistrationResult.Unavailable"/>
/// (<c>busy</c>) instead of pinning the pool.</para>
/// <para><b>Nothing secret leaves.</b> The Management token, the M2M client id and secret and the bodies of Auth0's errors are never logged, answered or put in
/// an exception text: a log line says the operation, the result and a stable code (an HTTP status, the name of an exception type). The host is not logged
/// either: the callers log the org and the result.</para>
/// </remarks>
public sealed partial class Auth0HostRegistrar(
    IAuth0ManagementTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    IOptions<Auth0HostSyncOptions> options,
    AppDbContext dbContext,
    ILogger<Auth0HostRegistrar> logger,
    Auth0HostRegistrationGate? gate = null) : IAuth0HostRegistrar
{
    /// <summary>The turn of this process: the shared singleton, or (a test) a gate of the registrar's own.</summary>
    private readonly Auth0HostRegistrationGate _gate = gate ?? new Auth0HostRegistrationGate();

    /// <summary>The members of the application the registrar reads: the three lists, nothing else.</summary>
    private const string ListFields = "callbacks,allowed_logout_urls,web_origins";

    public Task<Auth0HostRegistrationResult> RegisterAsync(string host, CancellationToken cancellationToken = default) =>
        ChangeAsync(host, register: true, cancellationToken);

    public Task<Auth0HostRegistrationResult> UnregisterAsync(string host, CancellationToken cancellationToken = default) =>
        ChangeAsync(host, register: false, cancellationToken);

    private async Task<Auth0HostRegistrationResult> ChangeAsync(string host, bool register, CancellationToken cancellationToken)
    {
        var operation = register ? "registration" : "removal";
        if (!TryOriginOf(host, out var origin))
            return Done(operation, Auth0HostRegistrationResult.Rejected, "invalid_host");

        // Auth0:HostSync:ClientId and the Management credentials: without both nothing is called.
        var clientId = ConfiguredClientId();
        if (clientId is null || !tokenProvider.IsConfigured)
            return Done(operation, Auth0HostRegistrationResult.NotConfigured, "not_configured");

        // The turn of this process comes first and costs no connection; one that does not come in time is a busy answer, never a waiter on the database lock.
        if (!await _gate.EnterAsync(cancellationToken))
            return Done(operation, Auth0HostRegistrationResult.Unavailable, "busy");

        try
        {
            return await ChangeInTurnAsync(operation, origin, clientId, register, cancellationToken);
        }
        finally
        {
            _gate.Leave();
        }
    }

    private async Task<Auth0HostRegistrationResult> ChangeInTurnAsync(string operation, string origin, string clientId, bool register, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                dbContext,
                cancellationToken,
                (PostgresAdvisoryLocks.Scope.Auth0HostRegistration, clientId));

            var before = HostLists.Of(await ReadAsync(clientId, cancellationToken));
            if (before.Callbacks.Count == 0)
                return Done(operation, Auth0HostRegistrationResult.Rejected, "no_platform_callback");

            if (!register && string.Equals(before.Callbacks[0], origin, StringComparison.OrdinalIgnoreCase))
                return Done(operation, Auth0HostRegistrationResult.Rejected, "platform_origin");

            var after = register ? before.Adding(origin) : before.Removing(origin);
            if (after.SameAs(before))
            {
                return Done(
                    operation,
                    register ? Auth0HostRegistrationResult.AlreadyRegistered : Auth0HostRegistrationResult.Removed,
                    "unchanged");
            }

            await ExecuteAsync(client => client.Clients.UpdateAsync(clientId, after.ToUpdateRequest(), cancellationToken), cancellationToken);

            var readBack = HostLists.Of(await ReadAsync(clientId, cancellationToken));
            var applied = register
                ? readBack.ContainsEverywhere(origin) && readBack.FirstCallbackIs(before.Callbacks[0])
                : readBack.ContainsNowhere(origin);
            if (!applied)
                return Done(operation, Auth0HostRegistrationResult.Rejected, "read_back_mismatch");

            return Done(
                operation,
                register ? Auth0HostRegistrationResult.Registered : Auth0HostRegistrationResult.Removed,
                "written");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never the exception itself: its text can carry the body of Auth0's answer.
            var (result, code) = Classify(ex);
            return Done(operation, result, code);
        }
    }

    private Task<Auth0Application> ReadAsync(string clientId, CancellationToken cancellationToken) =>
        ExecuteAsync(client => client.Clients.GetAsync(clientId, ListFields, includeFields: true, cancellationToken), cancellationToken);

    /// <summary>Runs <paramref name="call"/> with a fresh client; on HTTP 401 the cached token is dropped and the call runs once more with a new one.</summary>
    private async Task<T> ExecuteAsync<T>(Func<ManagementApiClient, Task<T>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var client = await CreateClientAsync(cancellationToken);
            try
            {
                return await call(client);
            }
            catch (ErrorApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                tokenProvider.Invalidate();
            }
        }
    }

    private async Task<ManagementApiClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        var connection = new HttpClientManagementConnection(
            httpClientFactory.CreateClient(Auth0ManagementTokenProvider.HttpClientName));
        return new ManagementApiClient(token, new Uri($"https://{tokenProvider.Domain}/api/v2"), connection);
    }

    private string? ConfiguredClientId()
    {
        var clientId = options.Value.ClientId?.Trim();
        return string.IsNullOrEmpty(clientId) || !ClientIdShape().IsMatch(clientId) ? null : clientId;
    }

    /// <summary>A refusal of Auth0 is final until something changes (a scope, the application, the URL); anything else is retried later.</summary>
    private static (Auth0HostRegistrationResult Result, string Code) Classify(Exception ex) => ex switch
    {
        Auth0ManagementTokenException token => (Auth0HostRegistrationResult.Unavailable, token.ErrorCode),
        RateLimitApiException => (Auth0HostRegistrationResult.Unavailable, "rate_limited"),
        ErrorApiException api when IsRefusal(api.StatusCode) => (Auth0HostRegistrationResult.Rejected, $"http_{(int)api.StatusCode}"),
        ErrorApiException api => (Auth0HostRegistrationResult.Unavailable, $"http_{(int)api.StatusCode}"),
        _ => (Auth0HostRegistrationResult.Unavailable, ex.GetType().Name),
    };

    private static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.Conflict
            or HttpStatusCode.UnprocessableEntity;

    private Auth0HostRegistrationResult Done(string operation, Auth0HostRegistrationResult result, string code)
    {
        var level = result is Auth0HostRegistrationResult.Rejected or Auth0HostRegistrationResult.Unavailable
            ? LogLevel.Warning
            : LogLevel.Information;
        logger.Log(level, "Auth0 host {Operation}: {Result} ({Code})", operation, result, code);
        return result;
    }

    /// <summary>The origin of a host name: lower case, no scheme, port, path or wildcard, never an IP address.</summary>
    private static bool TryOriginOf(string? host, out string origin)
    {
        origin = string.Empty;
        var name = host?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 253 || IPAddress.TryParse(name, out _) || !HostName().IsMatch(name))
            return false;

        origin = $"https://{name}";
        return true;
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostName();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex ClientIdShape();

    /// <summary>The three lists of the application, as Auth0 keeps them (order included).</summary>
    private sealed record HostLists(IReadOnlyList<string> Callbacks, IReadOnlyList<string> LogoutUrls, IReadOnlyList<string> WebOrigins)
    {
        public static HostLists Of(Auth0Application application) =>
            new(application.Callbacks ?? [], application.AllowedLogoutUrls ?? [], application.WebOrigins ?? []);

        public HostLists Adding(string origin) => new(Add(Callbacks, origin), Add(LogoutUrls, origin), Add(WebOrigins, origin));

        public HostLists Removing(string origin) => new(Remove(Callbacks, origin), Remove(LogoutUrls, origin), Remove(WebOrigins, origin));

        public bool SameAs(HostLists other) =>
            Callbacks.SequenceEqual(other.Callbacks, StringComparer.Ordinal)
            && LogoutUrls.SequenceEqual(other.LogoutUrls, StringComparer.Ordinal)
            && WebOrigins.SequenceEqual(other.WebOrigins, StringComparer.Ordinal);

        public bool ContainsEverywhere(string origin) => Has(Callbacks, origin) && Has(LogoutUrls, origin) && Has(WebOrigins, origin);

        public bool ContainsNowhere(string origin) => !Has(Callbacks, origin) && !Has(LogoutUrls, origin) && !Has(WebOrigins, origin);

        public bool FirstCallbackIs(string url) => Callbacks.Count > 0 && string.Equals(Callbacks[0], url, StringComparison.Ordinal);

        /// <summary>The three lists, whole, in one request: Auth0 replaces each list it receives and leaves every other member of the application alone.</summary>
        public ClientUpdateRequest ToUpdateRequest() => new()
        {
            Callbacks = [.. Callbacks],
            AllowedLogoutUrls = [.. LogoutUrls],
            WebOrigins = [.. WebOrigins],
        };

        private static bool Has(IReadOnlyList<string> list, string origin) =>
            list.Any(entry => string.Equals(entry, origin, StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<string> Add(IReadOnlyList<string> list, string origin) => Has(list, origin) ? list : [.. list, origin];

        private static IReadOnlyList<string> Remove(IReadOnlyList<string> list, string origin) =>
            [.. list.Where(entry => !string.Equals(entry, origin, StringComparison.OrdinalIgnoreCase))];
    }
}
