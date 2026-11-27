using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// The links CasaZen sends for an org (e-mails, SMS, chat texts, API answers), each one on the agency's own domain or on the CasaZen one
/// (E40/F01/US01/T03, NF-142): one place decides. <see cref="PublicSiteLinks"/> stays what it was, the platform builder and the platform
/// precheck. The rule is <see cref="HostSurfaceRules"/>: the guest links (check-in, booking area, booking confirmation) move only with
/// <c>GuestLinksOnOwnDomain</c>, the owner links only with <c>OwnerLinksOnOwnDomain</c> (the owner sign-in works there, so an owner never lands
/// on «not yet enabled»).
/// </summary>
/// <remarks>
/// The host of a link comes only from the org record (the domain that really serves the site, <see cref="PublicOrgSiteUrls.OwnHost"/>), never
/// from a header or a body, and a link carries the token or code it carried before and nothing else. With <c>WhiteLabelHost</c> off nothing is
/// read. A missing org, an org that is not active (the resolver serves none of its hosts), a read error or an unconfigured platform URL give the
/// platform links, byte for byte the ones built before: never a failure the old code did not have.
/// </remarks>
public sealed class AgencyDomainLinks(
    AppDbContext db,
    PublicSiteLinks platform,
    PublicOrgSiteUrls siteUrls,
    IFeatureFlags featureFlags,
    ILogger<AgencyDomainLinks> logger)
{
    /// <summary>The links of <paramref name="orgId"/>: read once per message, then used for every link of it.</summary>
    public async Task<AgencyLinks> ForOrgAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.WhiteLabelHost) || !platform.IsConfigured)
            return AgencyLinks.Platform(platform);

        Org? org;
        try
        {
            org = await ReadAsync(orgId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The links of org {OrgId} stay on the CasaZen domain: the org could not be read", orgId);
            return AgencyLinks.Platform(platform);
        }

        // The resolver serves no host of an org that is not active (OrgService.GetByVerifiedCustomDomainAsync): its links stay on the CasaZen domain, the one that opens.
        if (org is null || !org.IsActive)
            return AgencyLinks.Platform(platform);

        // The embeddable pages are no link: SiteEmbed plays no part here.
        var surfaces = HostSurfaceRules.Evaluate(org, siteUrls.ServedMode(org), whiteLabelHost: true, siteEmbed: false);
        var host = siteUrls.OwnHost(org);
        return new AgencyLinks(
            platform,
            surfaces.GuestLinksOnOwnDomain ? host : null,
            surfaces.OwnerLinksOnOwnDomain ? host : null);
    }

    /// <summary>
    /// The fields of the rule and of the served host only. The org is the tenant itself, with no tenant filter: an anonymous page, a job and a
    /// host request read it the same way.
    /// </summary>
    private async Task<Org?> ReadAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var row = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new
            {
                o.Slug,
                o.IsActive,
                o.PublicHostMode,
                o.CustomDomain,
                o.DomainVerificationStatus,
                o.Subdomain,
                o.PlanTier,
                o.SubscriptionStatus,
                o.PastDueSince,
                o.GuestAreaOnOwnDomain,
                o.OwnerPortalOnOwnDomain,
                o.UseOwnDomainForLinks,
                o.OwnerLoginHostState,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new Org
            {
                Id = orgId,
                Slug = row.Slug,
                IsActive = row.IsActive,
                PublicHostMode = row.PublicHostMode,
                CustomDomain = row.CustomDomain,
                DomainVerificationStatus = row.DomainVerificationStatus,
                Subdomain = row.Subdomain,
                PlanTier = row.PlanTier,
                SubscriptionStatus = row.SubscriptionStatus,
                PastDueSince = row.PastDueSince,
                GuestAreaOnOwnDomain = row.GuestAreaOnOwnDomain,
                OwnerPortalOnOwnDomain = row.OwnerPortalOnOwnDomain,
                UseOwnDomainForLinks = row.UseOwnDomainForLinks,
                OwnerLoginHostState = row.OwnerLoginHostState,
            };
    }
}

/// <summary>
/// The links of one org (<see cref="AgencyDomainLinks.ForOrgAsync"/>): each one on the agency's domain when its kind moved there, otherwise
/// the platform link of <see cref="PublicSiteLinks"/>, byte for byte. Same tokens and codes as the platform links, no other data.
/// </summary>
public sealed class AgencyLinks
{
    private readonly PublicSiteLinks _platform;

    internal AgencyLinks(PublicSiteLinks platform, string? guestHost, string? ownerHost)
    {
        _platform = platform;
        GuestHost = guestHost;
        OwnerHost = ownerHost;
    }

    /// <summary>The host the guest links are built on, or <c>null</c>: the CasaZen domain.</summary>
    public string? GuestHost { get; }

    /// <summary>The host the owner links are built on, or <c>null</c>: the CasaZen domain.</summary>
    public string? OwnerHost { get; }

    /// <summary>Every link on the CasaZen domain, as before E40 (also the links of a caller built without <see cref="AgencyDomainLinks"/>).</summary>
    public static AgencyLinks Platform(PublicSiteLinks platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return new AgencyLinks(platform, null, null);
    }

    /// <summary>The guest's check-in page (<see cref="PublicSiteLinks.GuestCheckIn"/>).</summary>
    public string GuestCheckIn(string token) =>
        GuestHost is { } host ? OnHost(host, PublicSitePaths.HostGuestCheckIn(token)) : _platform.GuestCheckIn(token);

    /// <summary>«Le mie prenotazioni» (<see cref="PublicSiteLinks.GuestBookings"/>); <paramref name="orgSlug"/> is only for the platform path.</summary>
    public string GuestBookings(string orgSlug, string? bookingCode = null) =>
        GuestHost is { } host ? OnHost(host, PublicSitePaths.HostGuestBookings(bookingCode)) : _platform.GuestBookings(orgSlug, bookingCode);

    /// <summary>The page where an owner accepts the invitation of its agency (<see cref="PublicSiteLinks.OwnerInvitationAccept"/>).</summary>
    public string OwnerInvitationAccept(string inviteToken) =>
        OwnerHost is { } host ? OnHost(host, PublicSitePaths.HostOwnerInvitationAccept(inviteToken)) : _platform.OwnerInvitationAccept(inviteToken);

    /// <summary>The page where a contact an owner invited accepts it (<see cref="PublicSiteLinks.OwnerContactAccept"/>).</summary>
    public string OwnerContactAccept(string inviteToken) =>
        OwnerHost is { } host ? OnHost(host, PublicSitePaths.HostOwnerContactAccept(inviteToken)) : _platform.OwnerContactAccept(inviteToken);

    /// <summary>A conversation of the owner inbox (<see cref="PublicSiteLinks.OwnerPortalConversation"/>).</summary>
    public string OwnerPortalConversation(Guid conversationId) =>
        OwnerHost is { } host ? OnHost(host, PublicSitePaths.HostOwnerPortalConversation(conversationId)) : _platform.OwnerPortalConversation(conversationId);

    /// <summary>The page «Contabilità» of the owner portal (<see cref="PublicSiteLinks.OwnerPortalAccounting"/>).</summary>
    public string OwnerPortalAccounting() =>
        OwnerHost is { } host ? OnHost(host, PublicSitePaths.HostOwnerPortalAccounting) : _platform.OwnerPortalAccounting();

    private static string OnHost(string host, string pathAndQuery) => $"https://{host}{pathAndQuery}";
}

/// <summary>The links of a caller that may have been built without <see cref="AgencyDomainLinks"/> (a test that builds it by hand).</summary>
public static class AgencyDomainLinksExtensions
{
    /// <summary>The links of <paramref name="orgId"/>, or the platform links when there is no builder.</summary>
    public static Task<AgencyLinks> ForOrgOrPlatformAsync(
        this AgencyDomainLinks? builder,
        PublicSiteLinks platform,
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        return builder is null ? Task.FromResult(AgencyLinks.Platform(platform)) : builder.ForOrgAsync(orgId, cancellationToken);
    }
}
