using Casazen.Core.Services;

namespace Casazen.Web.DTOs.SiteConnections;

public sealed class SiteConnectionAuthorizeRequest
{
    public string? SiteUrl { get; set; }

    public string? ReturnUrl { get; set; }

    public string? State { get; set; }

    public string? CodeChallenge { get; set; }

    public string? CodeChallengeMethod { get; set; }
}

public sealed record SiteConnectionAuthorizeResponse(string RedirectUrl, string SiteUrl, DateTime ExpiresAt)
{
    public static SiteConnectionAuthorizeResponse From(SiteConnectionAuthorization authorization) =>
        new(authorization.RedirectUrl, authorization.SiteUrl, authorization.ExpiresAt);
}

public sealed record SiteConnectionDto(Guid Id, string SiteUrl, DateTime CreatedAt, DateTime? LastUsedAt)
{
    public static SiteConnectionDto From(SiteConnectionSummary summary) =>
        new(summary.Id, summary.SiteUrl, summary.CreatedAt, summary.LastUsedAt);
}

public sealed class SiteConnectionTokenRequest
{
    public string? Code { get; set; }

    public string? CodeVerifier { get; set; }

    public string? SiteUrl { get; set; }
}

public sealed record SiteConnectionOrgDto(Guid Id, string Slug, string DisplayName)
{
    public static SiteConnectionOrgDto From(SiteConnectionOrg org) => new(org.Id, org.Slug, org.DisplayName);
}

public sealed record SiteConnectionTokenResponse(string AccessToken, Guid ConnectionId, SiteConnectionOrgDto Org)
{
    public static SiteConnectionTokenResponse From(SiteConnectionGrant grant) =>
        new(grant.AccessToken, grant.ConnectionId, SiteConnectionOrgDto.From(grant.Org));
}
