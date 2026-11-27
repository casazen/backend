using Casazen.Core.Services;

namespace Casazen.Web.DTOs.SiteConnections;

/// <summary>
/// Body of <c>POST /api/orgs/{orgId}/site-connections/authorize</c> (S022, site integration plan §10.3): what the plugin asked for, as the console page got
/// it. No attribute: every rule is <c>SiteConnectionRules</c>, and any violation is the one answer 400 <c>site_connection_invalid_request</c>.
/// </summary>
public sealed class SiteConnectionAuthorizeRequest
{
    /// <summary>The origin of the site (<c>https://www.example.com</c>).</summary>
    public string? SiteUrl { get; set; }

    /// <summary>Where the browser goes back to on the site (same origin): the code and the state are added to it.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>The state of the plugin, 1 to 128 characters of <c>[A-Za-z0-9_-]</c>, given back unchanged.</summary>
    public string? State { get; set; }

    /// <summary>The PKCE challenge: 43 characters of base64url of the SHA-256 of the verifier.</summary>
    public string? CodeChallenge { get; set; }

    /// <summary><c>S256</c>, the only method.</summary>
    public string? CodeChallengeMethod { get; set; }
}

/// <summary>The answer of the authorization: where the console sends the browser, the site it confirmed and until when the code opens.</summary>
public sealed record SiteConnectionAuthorizeResponse(string RedirectUrl, string SiteUrl, DateTime ExpiresAt)
{
    public static SiteConnectionAuthorizeResponse From(SiteConnectionAuthorization authorization) =>
        new(authorization.RedirectUrl, authorization.SiteUrl, authorization.ExpiresAt);
}

/// <summary>An active connected site of <c>GET /api/orgs/{orgId}/site-connections</c>.</summary>
public sealed record SiteConnectionDto(Guid Id, string SiteUrl, DateTime CreatedAt, DateTime? LastUsedAt)
{
    public static SiteConnectionDto From(SiteConnectionSummary summary) =>
        new(summary.Id, summary.SiteUrl, summary.CreatedAt, summary.LastUsedAt);
}

/// <summary>
/// Body of <c>POST /api/public/site-connections/token</c> (S022): the code the console gave, the verifier of its challenge and the origin of the site. Any
/// failure is the one answer 400 <c>site_connection_invalid_grant</c>.
/// </summary>
public sealed class SiteConnectionTokenRequest
{
    public string? Code { get; set; }

    /// <summary>43 to 128 characters of <c>[A-Za-z0-9-._~]</c>.</summary>
    public string? CodeVerifier { get; set; }

    public string? SiteUrl { get; set; }
}

/// <summary>The org a credential acts on: the same object in the answer of the exchange and in the account of the plugin (S023).</summary>
public sealed record SiteConnectionOrgDto(Guid Id, string Slug, string DisplayName)
{
    public static SiteConnectionOrgDto From(SiteConnectionOrg org) => new(org.Id, org.Slug, org.DisplayName);
}

/// <summary>The answer of the exchange: the credential, shown this once, its connection and its org.</summary>
public sealed record SiteConnectionTokenResponse(string AccessToken, Guid ConnectionId, SiteConnectionOrgDto Org)
{
    public static SiteConnectionTokenResponse From(SiteConnectionGrant grant) =>
        new(grant.AccessToken, grant.ConnectionId, SiteConnectionOrgDto.From(grant.Org));
}
