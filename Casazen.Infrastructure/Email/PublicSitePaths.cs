namespace Casazen.Infrastructure.Email;

/// <summary>
/// Paths of the public booking site in the web app (frontend routes <c>/book/:orgSlug/…</c>), the single place where the
/// backend writes them: the canonical URLs, the sitemaps and the crawler pages of the booking sites (BK-15) use this
/// class only, so they cannot disagree with each other. The absolute URL is always <c>App:PublicSiteBaseUrl</c> + path
/// (<see cref="PublicSiteLinks.PublicPage"/>), never a domain written here (decision D3).
/// </summary>
public static class PublicSitePaths
{
    public const string OrgSitemapIndex = "/sitemap-book.xml";

    public const string SupplierSitemap = "/sitemap-supplier.xml";

    public static string SupplierShowcase(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return $"/fornitori/{Uri.EscapeDataString(slug)}";
    }

    public static string Org(string orgSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        return $"/book/{Uri.EscapeDataString(orgSlug)}";
    }

    public static string Property(string orgSlug, string propertySlugOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertySlugOrId);
        return $"{Org(orgSlug)}/property/{Uri.EscapeDataString(propertySlugOrId)}";
    }

    public const string HostLanding = "/";

    public static string HostProperty(string propertySlugOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertySlugOrId);
        return $"/property/{Uri.EscapeDataString(propertySlugOrId)}";
    }

    public const string HostSitemap = "/sitemap.xml";

    // ─── Guest area and owner portal on the agency's own domain (E40/F01/US01/T03, NF-142) ─────────────────
    // The same routes, tokens and codes as on the CasaZen domain (PublicSiteLinks), at the root of the agency's host; the host itself comes
    // from the org record (AgencyDomainLinks), never from here.

    public static string HostGuestCheckIn(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"/checkin/{Uri.EscapeDataString(token)}";
    }

    public static string HostGuestPortal(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"/s/{Uri.EscapeDataString(token)}";
    }

    public static string HostGuestBookings(string? bookingCode = null) =>
        string.IsNullOrWhiteSpace(bookingCode) ? "/my-bookings" : $"/my-bookings?code={Uri.EscapeDataString(bookingCode)}";

    public static string HostOwnerInvitationAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return $"/portale-proprietari/invito?token={Uri.EscapeDataString(inviteToken)}";
    }

    public static string HostOwnerContactAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return $"/portale-proprietari/contatto?token={Uri.EscapeDataString(inviteToken)}";
    }

    public static string HostOwnerPortalConversation(Guid conversationId) => $"/portale-proprietari/messaggi/{conversationId:D}";

    public const string HostOwnerPortalAccounting = "/portale-proprietari/contabilita";

    public static string OrgSitemap(string orgSlug) => $"{Org(orgSlug)}/sitemap.xml";
}
