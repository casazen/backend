namespace Casazen.Infrastructure.Email;

/// <summary>
/// Paths of the public booking site in the web app (frontend routes <c>/book/:orgSlug/…</c>), the single place where the
/// backend writes them: the canonical URLs, the sitemaps and the crawler pages of the booking sites (BK-15) use this
/// class only, so they cannot disagree with each other. The absolute URL is always <c>App:PublicSiteBaseUrl</c> + path
/// (<see cref="PublicSiteLinks.PublicPage"/>), never a domain written here (decision D3).
/// </summary>
public static class PublicSitePaths
{
    /// <summary>Sitemap index of the booking sites: one entry per org that has something to index (BK-15).</summary>
    public const string OrgSitemapIndex = "/sitemap-book.xml";

    /// <summary>Sitemap of the supplier showcases (one entry per active supplier with a slug).</summary>
    public const string SupplierSitemap = "/sitemap-supplier.xml";

    /// <summary>
    /// Public showcase of a supplier (<c>/fornitori/{slug}</c>, SU-13): a page of the web app's own host, never of an org's
    /// booking site. Indexable: it is in <see cref="SupplierSitemap"/> and has a crawler page.
    /// </summary>
    public static string SupplierShowcase(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return $"/fornitori/{Uri.EscapeDataString(slug)}";
    }

    /// <summary>Landing page of an org's booking site (<c>/book/{slug}</c>).</summary>
    public static string Org(string orgSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        return $"/book/{Uri.EscapeDataString(orgSlug)}";
    }

    /// <summary>Public page of a property: <c>/book/{orgSlug}/property/{slug or id}</c> (the id only when it has no slug).</summary>
    public static string Property(string orgSlug, string propertySlugOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertySlugOrId);
        return $"{Org(orgSlug)}/property/{Uri.EscapeDataString(propertySlugOrId)}";
    }

    /// <summary>Landing page of an org on its own host (subdomain or custom domain): the root.</summary>
    public const string HostLanding = "/";

    /// <summary>Public page of a property on the org's own host: <c>/property/{slug or id}</c> (no <c>/book/{slug}</c> prefix).</summary>
    public static string HostProperty(string propertySlugOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertySlugOrId);
        return $"/property/{Uri.EscapeDataString(propertySlugOrId)}";
    }

    /// <summary>Sitemap of an org's own host: <c>/sitemap.xml</c> of that host.</summary>
    public const string HostSitemap = "/sitemap.xml";

    // ─── Guest area and owner portal on the agency's own domain (E40/F01/US01/T03, NF-142) ─────────────────
    // The same routes, tokens and codes as on the CasaZen domain (PublicSiteLinks), at the root of the agency's host; the host itself comes
    // from the org record (AgencyDomainLinks), never from here.

    /// <summary>The guest's check-in page on the org's own host: <c>/checkin/{token}</c>.</summary>
    public static string HostGuestCheckIn(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"/checkin/{Uri.EscapeDataString(token)}";
    }

    /// <summary>
    /// The guest portal of a stay, the same path on the CasaZen domain and on the org's own host: <c>/s/{token}</c> (E50/F01/US01/T01, NF-152). Only the
    /// random token of the stay link is in it.
    /// </summary>
    public static string HostGuestPortal(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"/s/{Uri.EscapeDataString(token)}";
    }

    /// <summary>«Le mie prenotazioni» on the org's own host: <c>/my-bookings</c>, with <c>?code=</c> when a code is given.</summary>
    public static string HostGuestBookings(string? bookingCode = null) =>
        string.IsNullOrWhiteSpace(bookingCode) ? "/my-bookings" : $"/my-bookings?code={Uri.EscapeDataString(bookingCode)}";

    /// <summary>The page where an owner accepts the invitation of its agency, on the agency's own host.</summary>
    public static string HostOwnerInvitationAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return $"/portale-proprietari/invito?token={Uri.EscapeDataString(inviteToken)}";
    }

    /// <summary>The page where a contact an owner invited accepts it, on the agency's own host.</summary>
    public static string HostOwnerContactAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return $"/portale-proprietari/contatto?token={Uri.EscapeDataString(inviteToken)}";
    }

    /// <summary>A conversation of the owner inbox on the agency's own host: the id only.</summary>
    public static string HostOwnerPortalConversation(Guid conversationId) => $"/portale-proprietari/messaggi/{conversationId:D}";

    /// <summary>The page «Contabilità» of the owner portal on the agency's own host.</summary>
    public const string HostOwnerPortalAccounting = "/portale-proprietari/contabilita";

    /// <summary>
    /// Sitemap of one org (<c>/book/{slug}/sitemap.xml</c>): under the org's own path, so it may only list URLs of that
    /// path, as the sitemap protocol requires.
    /// </summary>
    public static string OrgSitemap(string orgSlug) => $"{Org(orgSlug)}/sitemap.xml";
}
