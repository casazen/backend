using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="ISiteAccountBuilder"/> (S023, PLG-BE-SITEAPI, site integration plan §10.3): the one place that makes the <c>account</c> of a connected plugin, so the four
/// endpoints that answer it cannot disagree. It adds no rule: the domain settings are those of <see cref="IOrgDomainService"/> (the console's), the address of the
/// org's own domain is the one the console reads in <c>publicUrls.customDomainUrl</c> (a custom domain that is verified and paid for, <see cref="PublicOrgSiteUrls"/>),
/// the CasaZen address is <c>App:PublicSiteBaseUrl</c>, and <c>embedEnabled</c> is what <see cref="HostSurfaceRules"/> gives <c>resolve-host</c>.
/// </summary>
public sealed class SiteAccountBuilder(
    AppDbContext db,
    IOrgDomainService orgDomain,
    PublicOrgSiteUrls siteUrls,
    IOptions<PublicSiteOptions> publicSite,
    IFeatureFlags featureFlags) : ISiteAccountBuilder
{
    /// <inheritdoc />
    public async Task<SiteAccountResult> BuildAsync(
        SiteConnectionPrincipal site,
        OrgDomainConfig? domain = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);

        // The org of the credential and no other; one that is no longer active has no account (its credential does not open either).
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == site.OrgId && o.IsActive, cancellationToken);
        if (org is null)
            return new SiteAccountResult(SiteAccountOutcome.NotFound, null);

        // The configuration of another org is never used: the one of the credential is read.
        if (domain is null || domain.OrgId != org.Id)
            domain = await orgDomain.GetDomainConfigAsync(org.Id, cancellationToken);

        if (domain is null)
            return new SiteAccountResult(SiteAccountOutcome.NotFound, null);

        string address;
        SitePublicAddressKind kind;
        if (domain.PublicUrls.CustomDomainUrl is { Length: > 0 } ownDomain)
        {
            // A domain that is served now: verified and paid for, the one resolve-host answers on.
            address = ownDomain;
            kind = SitePublicAddressKind.CustomDomain;
        }
        else if (PublicSiteOptions.TryGetOrigin(publicSite.Value.PublicSiteBaseUrl, out var origin))
        {
            address = origin;
            kind = SitePublicAddressKind.Casazen;
        }
        else
        {
            return new SiteAccountResult(SiteAccountOutcome.PublicAddressNotConfigured, null);
        }

        // The same call that makes the answer of resolve-host, with the mode the org's host is really served in.
        var surfaces = HostSurfaceRules.Evaluate(
            org,
            siteUrls.ServedMode(org),
            featureFlags.IsEnabled(FeatureFlags.WhiteLabelHost),
            featureFlags.IsEnabled(FeatureFlags.SiteEmbed));

        var displayName = string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName;
        return new SiteAccountResult(
            SiteAccountOutcome.Success,
            new SiteAccount(
                site.ConnectionId,
                new SiteConnectionOrg(org.Id, org.Slug, displayName),
                surfaces.EmbedEnabled,
                address,
                kind,
                domain));
    }
}
