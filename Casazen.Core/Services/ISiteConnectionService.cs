namespace Casazen.Core.Services;

/// <summary>
/// The connection of a site to the org (S022, PLG-BE-CONNECT, site integration plan §10, NF-201, NF-203, NF-204): the WordPress plugin presses «Connetti a
/// CasaZen», a billing administrator of the org confirms the site on the console (<see cref="AuthorizeAsync"/>, a one-time code bound to the org, the
/// account, the site and a PKCE challenge), and the plugin exchanges the code with its verifier for a credential of its own (<see cref="ExchangeAsync"/>),
/// which the scheme <c>SiteToken</c> checks at every call (<see cref="AuthenticateAsync"/>). The console lists and revokes the connections; the plugin
/// revokes its own when it disconnects.
/// </summary>
/// <remarks>
/// <para>Services never check roles: the web layer decides that the caller is a billing administrator of <c>orgId</c> (policy <c>RequireOrgBillingAdmin</c>
/// and the IDOR check of the console) or holds the credential (policy <c>SiteConnection</c>).</para>
/// <para>Nothing secret leaves this service but the answers of creation: the code in <see cref="SiteConnectionAuthorization.RedirectUrl"/> and the credential in
/// <see cref="SiteConnectionGrant.AccessToken"/>. The database holds their SHA-256 only, and the logs hold the org, the connection and the host of the site.</para>
/// </remarks>
public interface ISiteConnectionService
{
    /// <summary>
    /// A one-time code for the site <paramref name="siteUrl"/> to connect to <paramref name="orgId"/>, confirmed by <paramref name="userId"/>: 32 random bytes,
    /// five minutes, stored as its hash with the challenge. The answer is the address WordPress is sent back to (<paramref name="returnUrl"/> with the code
    /// and <paramref name="state"/> added). <c>null</c> when the request breaks any rule of <c>SiteConnectionRules</c>: nothing is stored then. The codes
    /// expired for more than a day are deleted on the way.
    /// </summary>
    Task<SiteConnectionAuthorization?> AuthorizeAsync(
        Guid orgId,
        string userId,
        string? siteUrl,
        string? returnUrl,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a code for a credential: the code is spent first, in one conditional update (of two requests with one code only one goes on), then its
    /// expiry, its site and the PKCE proof of <paramref name="codeVerifier"/> are checked (constant time). The active connections of the same org and site
    /// are revoked and the new one is created. <c>null</c> for every failure, whatever the reason: the caller gives one answer.
    /// </summary>
    Task<SiteConnectionGrant?> ExchangeAsync(
        string? code,
        string? codeVerifier,
        string? siteUrl,
        CancellationToken cancellationToken = default);

    /// <summary>The active connections of <paramref name="orgId"/>, the newest first.</summary>
    Task<IReadOnlyList<SiteConnectionSummary>> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the connection <paramref name="connectionId"/> of <paramref name="orgId"/> (the console): true when it is a connection of the org (revoked now, or
    /// already), false when it is not one of the org.
    /// </summary>
    Task<bool> RevokeAsync(Guid orgId, Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the connection <paramref name="connectionId"/> (the plugin disconnects itself; the id is the one of its own credential). False when there is no
    /// such connection.
    /// </summary>
    Task<bool> RevokeAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The connection a credential opens: an active one of an active org, found by the hash of <paramref name="credential"/>; <c>null</c> for anything else
    /// (malformed, unknown, revoked). Records the use at most once every five minutes.
    /// </summary>
    Task<SiteConnectionPrincipal?> AuthenticateAsync(string? credential, CancellationToken cancellationToken = default);
}

/// <summary>A code just created.</summary>
/// <param name="RedirectUrl">The return address with <c>casazen_code</c> and <c>state</c> added: where the console sends the browser back.</param>
/// <param name="SiteUrl">The normalized origin of the site, the one the console shows in full.</param>
/// <param name="ExpiresAt">When the code stops opening (UTC).</param>
public sealed record SiteConnectionAuthorization(string RedirectUrl, string SiteUrl, DateTime ExpiresAt);

/// <summary>The org a credential acts on, as the plugin shows it.</summary>
public sealed record SiteConnectionOrg(Guid Id, string Slug, string DisplayName);

/// <summary>A credential just created: shown once.</summary>
/// <param name="AccessToken"><c>czs_</c> and 43 characters; sent as <c>Authorization: Bearer</c> to <c>api/site/v1/*</c>.</param>
/// <param name="ConnectionId">The connection the credential is.</param>
/// <param name="Org">The org it acts on.</param>
public sealed record SiteConnectionGrant(string AccessToken, Guid ConnectionId, SiteConnectionOrg Org);

/// <summary>An active connection of the org, as the console lists it.</summary>
public sealed record SiteConnectionSummary(Guid Id, string SiteUrl, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>What a valid credential proves: the connection and its org (never a person).</summary>
public sealed record SiteConnectionPrincipal(Guid ConnectionId, Guid OrgId);
