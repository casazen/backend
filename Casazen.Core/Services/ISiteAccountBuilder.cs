namespace Casazen.Core.Services;

/// <summary>
/// Which address a connected plugin uses to reach the org's embeddable pages (S023, PLG-BE-SITEAPI, site integration plan §10.3): the agency's own domain when the
/// org is served on it now, otherwise the address of CasaZen, where the slug of the org says who a widget is for.
/// </summary>
public enum SitePublicAddressKind
{
    /// <summary>
    /// The org's own custom domain, verified, paid for and served now (the one <c>resolve-host</c> and the console serve): the manifest, <c>resolve-host</c> and the
    /// widgets answer there, and they need no slug.
    /// </summary>
    CustomDomain,

    /// <summary>The address of CasaZen (<c>App:PublicSiteBaseUrl</c>), also for an org on a platform subdomain: the widgets there need the slug of the org.</summary>
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

    /// <summary>The org of the credential does not exist or is no longer active — the plugin's API answers 404.</summary>
    NotFound,

    /// <summary>
    /// The org is not on a served custom domain and <c>App:PublicSiteBaseUrl</c> is not configured, so there is no address to give (Development and Testing only: elsewhere
    /// the startup fails) — the plugin's API answers 503, never a wrong address.
    /// </summary>
    PublicAddressNotConfigured,
}

public sealed record SiteAccountResult(SiteAccountOutcome Outcome, SiteAccount? Account);

/// <summary>
/// Builds the <c>account</c> of a connected plugin once, for the four endpoints that answer it (<c>GET account</c>, <c>PUT domain</c>, <c>POST domain/verify</c>,
/// <c>PUT domain/surfaces</c>, S023). The org is the credential's, never an argument of the caller's choosing.
/// </summary>
public interface ISiteAccountBuilder
{
    /// <summary>
    /// The account of the org of <paramref name="site"/>. <paramref name="domain"/> is the domain configuration the caller has just read or saved (the answer of
    /// <see cref="IOrgDomainService"/>), so it is not read twice; <c>null</c>, or the configuration of another org, reads it now.
    /// </summary>
    Task<SiteAccountResult> BuildAsync(
        SiteConnectionPrincipal site,
        OrgDomainConfig? domain = null,
        CancellationToken cancellationToken = default);
}
