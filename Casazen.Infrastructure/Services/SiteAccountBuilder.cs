using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public sealed class SiteAccountBuilder(
    AppDbContext db,
    IOrgDomainService orgDomain,
    PublicOrgSiteUrls siteUrls,
    IOptions<PublicSiteOptions> publicSite,
    IFeatureFlags featureFlags) : ISiteAccountBuilder
{
    public async Task<SiteAccountResult> BuildAsync(
        SiteConnectionPrincipal site,
        OrgDomainConfig? domain = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);

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
