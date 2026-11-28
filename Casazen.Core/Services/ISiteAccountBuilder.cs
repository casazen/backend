namespace Casazen.Core.Services;

/// <summary>
/// Which address a connected plugin uses to reach the org's embeddable pages (S023, PLG-BE-SITEAPI, site integration plan §10.3): the agency's own domain when the
/// org is served on it now, otherwise the address of CasaZen, where the slug of the org says who a widget is for.
/// </summary>
public enum SitePublicAddressKind
{
    CustomDomain,

    Casazen,
}

/// <summary>
/// What a connected plugin reads of its org (S023, site integration plan §10.3): who it is, whether the embeddable pages are on, the address the plugin must use
/// and the domain settings exactly as the console gets them.
/// </summary>
/// <param name="ConnectionId">The connection of the credential that asked.</param>
/// <param name="Org">The org of the credential, with the name the plugin shows.</param>
/// <param name="EmbedEnabled">What <c>resolve-host</c> answers for the org's host (<see cref="HostSurfaceRules"/>): the embeddable pages are on.</param>
/// <param name="PublicAddress">The origin (<c>https://host</c>, no path, no slash at the end) where <c>/embed/v1/manifest.json</c> and <c>resolve-host</c> answer for the org.</param>
/// <param name="PublicAddressKind">Whether <paramref name="PublicAddress"/> is the org's own domain or the address of CasaZen.</param>
/// <param name="Domain">The domain settings of the org: the one <see cref="IOrgDomainService"/> gives the console.</param>
public sealed record SiteAccount(
    Guid ConnectionId,
    SiteConnectionOrg Org,
    bool EmbedEnabled,
    string PublicAddress,
    SitePublicAddressKind PublicAddressKind,
    OrgDomainConfig Domain);

public enum SiteAccountOutcome
{
    Success,

    NotFound,

    PublicAddressNotConfigured,
}

public sealed record SiteAccountResult(SiteAccountOutcome Outcome, SiteAccount? Account);

/// <summary>
/// Builds the <c>account</c> of a connected plugin once, for the four endpoints that answer it (<c>GET account</c>, <c>PUT domain</c>, <c>POST domain/verify</c>,
/// <c>PUT domain/surfaces</c>, S023). The org is the credential's, never an argument of the caller's choosing.
/// </summary>
public interface ISiteAccountBuilder
{
    Task<SiteAccountResult> BuildAsync(
        SiteConnectionPrincipal site,
        OrgDomainConfig? domain = null,
        CancellationToken cancellationToken = default);
}
