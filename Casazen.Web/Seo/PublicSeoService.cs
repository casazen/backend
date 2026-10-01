using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Seo;

/// <inheritdoc cref="IPublicSeoService"/>
/// <remarks>
/// Every URL is <c>App:PublicSiteBaseUrl</c> + a path of <see cref="PublicSitePaths"/> / <see cref="SeoPagePaths"/>, or, for
/// an org that has its own host, that host (<see cref="PublicOrgSiteUrls"/>); decision D3: no domain written here. What is
/// indexable is decided here and nowhere else:
/// <list type="bullet">
/// <item>an org page: the org is active and has a published property (<see cref="PublicListing.IsPublished"/>);</item>
/// <item>a property page: the property is published, otherwise the answer is a real 404;</item>
/// <item>the request came on the web app's own host or on a host that resolves to the same org (an org subdomain or a
/// verified custom domain, BK-16). Any other host, such as a custom domain still waiting for its verification or a
/// preview deployment, gets <c>noindex</c>: <paramref name="requestHost"/> is only compared, never written into a page.</item>
/// </list>
/// The texts follow the culture of the request (Italian by default, which is what the web app asks for); the document says
/// so in its <c>lang</c>.
/// </remarks>
public sealed class PublicSeoService(
    AppDbContext db,
    IOrgService orgService,
    ISeoContentService seoContentService,
    IPublicHostResolver hostResolver,
    PublicSiteLinks links,
    PublicOrgSiteUrls urls,
    IStringLocalizer<SharedResources> localizer) : IPublicSeoService
{
    /// <summary>Brand of the platform pages (<c>og:site_name</c>, publisher of the guides).</summary>
    public const string PlatformName = "CasaZen";

    /// <summary>Most properties an org landing page lists, like the booking site itself.</summary>
    public const int MaxListedProperties = 50;

    private const int MaxImages = 8;
    private const string Currency = "EUR";

    public async Task<SeoPageResult> RenderOrgAsync(string orgSlug, string? requestHost, CancellationToken cancellationToken = default)
    {
        var org = await orgService.GetPublicBySlugAsync(orgSlug, cancellationToken);
        if (org is null)
            return NotFound();

        if (!string.Equals(org.Slug, orgSlug, StringComparison.Ordinal))
            return Moved(PublicSitePaths.Org(org.Slug));

        return await RenderOrgPageAsync(org, requestHost, cancellationToken);
    }

    public async Task<SeoPageResult> RenderHostPageAsync(string host, string path, CancellationToken cancellationToken = default)
    {
        // The host is only compared: a value that is not a host name, a host of nobody (the platform itself, a preview, a
        // custom domain still waiting for its verification) and a domain no longer paid for are all "unknown host".
        var normalizedHost = PublicSiteHosts.Normalize(host);
        var resolved = normalizedHost is null ? null : await hostResolver.ResolveAsync(normalizedHost, cancellationToken);
        if (resolved is null)
            return UnknownHost();

        var org = await orgService.GetPublicBySlugAsync(resolved.Slug, cancellationToken);
        if (org is null || org.Id != resolved.OrgId)
            return UnknownHost();

        if (path == PublicSitePaths.HostLanding)
            return await RenderOrgPageAsync(org, normalizedHost, cancellationToken);

        const string propertyPrefix = "/property/";
        if (path.StartsWith(propertyPrefix, StringComparison.Ordinal))
        {
            var segment = Uri.UnescapeDataString(path[propertyPrefix.Length..]);
            if (segment.Length > 0 && !segment.Contains('/'))
                return await RenderPropertyPageAsync(org, segment, normalizedHost, ownHostPaths: true, cancellationToken);
        }

        return NotFound();
    }

    private async Task<SeoPageResult> RenderOrgPageAsync(Org org, string? requestHost, CancellationToken cancellationToken)
    {
        var properties = await PublishedProperties(org.Id)
            .OrderBy(p => p.City).ThenBy(p => p.NightlyRate)
            .Take(MaxListedProperties)
            .Select(ProjectRow)
            .ToListAsync(cancellationToken);

        var indexable = properties.Count > 0 && await IsIndexableHostAsync(requestHost, org.Id, cancellationToken);
        var language = Language();
        var culture = CultureInfo.CurrentUICulture;
        var canonical = urls.TryLandingUrl(org);
        var displayName = SeoHtml.Collapse(org.DisplayName);

        var items = properties
            .Select(p => (Row: p, Url: urls.TryPropertyUrl(org, PropertySegment(p)) ?? PublicSitePaths.Property(org.Slug, PropertySegment(p))))
            .ToList();

        var body = new StringBuilder();
        body.Append("<header>\n<h1>").Append(SeoHtml.Encode(displayName)).Append("</h1>\n");
        if (!string.IsNullOrWhiteSpace(org.Tagline))
            body.Append("<p>").Append(SeoHtml.Encode(SeoHtml.Collapse(org.Tagline))).Append("</p>\n");
        body.Append("</header>\n<main>\n");
        if (items.Count > 0)
        {
            body.Append("<section>\n<h2>").Append(SeoHtml.Encode(T("SeoOurProperties"))).Append("</h2>\n<ul>\n");
            foreach (var (row, url) in items)
            {
                body.Append("<li><a href=\"").Append(SeoHtml.Encode(url)).Append("\">")
                    .Append(SeoHtml.Encode(row.Name)).Append("</a>");
                if (!string.IsNullOrWhiteSpace(row.City))
                    body.Append(", ").Append(SeoHtml.Encode(row.City));
                if (row.MaxGuests > 0)
                    body.Append(". ").Append(SeoHtml.Encode($"{T("SeoLabelGuests")}: {row.MaxGuests}"));
                if (row.NightlyRate > 0m)
                    body.Append(". ").Append(SeoHtml.Encode(T("SeoPriceFrom", Money(row.NightlyRate, culture))));
                body.Append("</li>\n");
            }

            body.Append("</ul>\n</section>\n");
        }
        else
        {
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoOrgDescriptionNone", displayName))).Append("</p>\n");
        }

        body.Append("</main>\n");

        var jsonLd = new List<JsonObject>
        {
            SeoJsonLd.Organization(displayName, canonical, SeoHtml.TryHttpsUrl(org.LogoUrl), SeoHtml.Collapse(org.Tagline)),
        };
        if (items.Count > 0)
            jsonLd.Add(SeoJsonLd.ItemList(items.Select(i => (i.Row.Name, (string?)i.Url))));

        var image = SeoHtml.TryHttpsUrl(org.LogoUrl)
            ?? SeoHtml.TryHttpsUrl(org.HeroImageUrl)
            ?? FirstPhoto(properties);

        return Ok(new SeoDocument
        {
            Language = language,
            Title = T("SeoOrgTitle", displayName),
            Description = OrgDescription(org, displayName, properties),
            CanonicalUrl = canonical,
            Indexable = indexable,
            SiteName = displayName,
            ImageUrl = image,
            JsonLd = jsonLd,
            BodyHtml = body.ToString(),
        });
    }

    public async Task<SeoPageResult> RenderPropertyAsync(
        string orgSlug, string propertySlugOrId, string? requestHost, CancellationToken cancellationToken = default)
    {
        var org = await orgService.GetPublicBySlugAsync(orgSlug, cancellationToken);
        if (org is null)
            return NotFound();

        var result = await RenderPropertyPageAsync(org, propertySlugOrId, requestHost, ownHostPaths: false, cancellationToken);
        // An old slug of the org: the same page under the current one (the property was found by its own id or slug).
        return result.Status == SeoPageStatus.Ok && !string.Equals(org.Slug, orgSlug, StringComparison.Ordinal)
            ? Moved(PublicSitePaths.Property(org.Slug, propertySlugOrId))
            : result;
    }

    private async Task<SeoPageResult> RenderPropertyPageAsync(
        Org org, string propertySlugOrId, string? requestHost, bool ownHostPaths, CancellationToken cancellationToken)
    {
        var query = PublishedProperties(org.Id);
        query = Guid.TryParse(propertySlugOrId, out var id)
            ? query.Where(p => p.Id == id)
            : query.Where(p => p.Slug == propertySlugOrId);

        var row = await query.Select(ProjectRow).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return NotFound();

        // A property reached by id when it has a slug lives at the slug path: permanent redirect, on the same host and in
        // the same path scheme (own host: /property/{slug}, platform: /book/{org}/property/{slug}).
        var segment = PropertySegment(row);
        if (!string.Equals(segment, propertySlugOrId, StringComparison.OrdinalIgnoreCase))
        {
            return Moved(ownHostPaths
                ? PublicSitePaths.HostProperty(segment)
                : PublicSitePaths.Property(org.Slug, segment));
        }

        var indexable = await IsIndexableHostAsync(requestHost, org.Id, cancellationToken);
        var culture = CultureInfo.CurrentUICulture;
        var canonical = urls.TryPropertyUrl(org, segment);
        var orgUrl = urls.TryLandingUrl(org);
        var orgName = SeoHtml.Collapse(org.DisplayName);
        var images = row.PhotoUrls.Select(SeoHtml.TryHttpsUrl).OfType<string>().Take(MaxImages).ToList();
        var cin = CinFormat.GetStatus(row.CinCode) == CinStatus.Valid ? CinFormat.Normalize(row.CinCode) : null;
        var amenityNames = row.Amenities.Distinct().Select(AmenityLabel).ToList();
        var petsAllowed = row.Amenities.Contains(PropertyAmenity.PetFriendly);

        var body = new StringBuilder();
        body.Append("<header>\n<p><a href=\"").Append(SeoHtml.Encode(orgUrl ?? PublicSitePaths.Org(org.Slug))).Append("\">")
            .Append(SeoHtml.Encode(orgName)).Append("</a></p>\n</header>\n<main>\n<article>\n");
        body.Append("<h1>").Append(SeoHtml.Encode(row.Name)).Append("</h1>\n");
        if (!string.IsNullOrWhiteSpace(row.City))
            body.Append("<p>").Append(SeoHtml.Encode(row.City)).Append("</p>\n");
        foreach (var image in images)
        {
            body.Append("<img src=\"").Append(SeoHtml.Encode(image)).Append("\" alt=\"").Append(SeoHtml.Encode(row.Name))
                .Append("\">\n");
        }

        body.Append(SeoHtml.Paragraphs(row.Description));

        body.Append("<h2>").Append(SeoHtml.Encode(T("SeoLabelDetails"))).Append("</h2>\n<ul>\n");
        if (row.MaxGuests > 0)
            AppendFact(body, T("SeoLabelGuests"), row.MaxGuests.ToString(culture));
        AppendFact(body, T("SeoLabelBedrooms"), row.Bedrooms.ToString(culture));
        if (row.Bathrooms > 0)
            AppendFact(body, T("SeoLabelBathrooms"), row.Bathrooms.ToString(culture));
        if (row.CleaningFee > 0m)
            AppendFact(body, T("SeoLabelCleaningFee"), Money(row.CleaningFee, culture));
        if (cin is not null)
            AppendFact(body, T("SeoLabelCin"), cin);
        body.Append("</ul>\n");

        if (row.NightlyRate > 0m)
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoPriceFrom", Money(row.NightlyRate, culture)))).Append("</p>\n");

        if (amenityNames.Count > 0)
        {
            body.Append("<h2>").Append(SeoHtml.Encode(T("SeoLabelAmenities"))).Append("</h2>\n<ul>\n");
            foreach (var name in amenityNames)
                body.Append("<li>").Append(SeoHtml.Encode(name)).Append("</li>\n");
            body.Append("</ul>\n");
        }

        AppendSection(body, T("SeoLabelHouseRules"), row.HouseRules);
        AppendSection(body, T("SeoLabelCancellation"), row.CancellationPolicySummary);
        body.Append("</article>\n</main>\n");

        var rental = new SeoJsonLd.VacationRentalData(
            row.Name,
            SeoHtml.Collapse(row.Description),
            canonical,
            images,
            row.City,
            row.PostalCode,
            // A valid CIN starts with the country code of the national register, so the country is read from real data.
            cin?[..2],
            row.Latitude,
            row.Longitude,
            row.MaxGuests,
            row.Bedrooms,
            row.Bathrooms,
            amenityNames,
            petsAllowed,
            // The CIN identifies the unit nationally; without a valid one the (unique) id of the property does.
            cin ?? row.Id.ToString(),
            row.NightlyRate,
            Currency,
            orgName,
            orgUrl);

        var breadcrumb = SeoJsonLd.BreadcrumbList([(orgName, orgUrl), (row.Name, canonical)]);

        return Ok(new SeoDocument
        {
            Language = Language(),
            Title = $"{row.Name} · {row.City} — {orgName}",
            Description = PropertyDescription(row),
            CanonicalUrl = canonical,
            Indexable = indexable,
            SiteName = orgName,
            ImageUrl = images.FirstOrDefault(),
            JsonLd = [SeoJsonLd.VacationRental(rental), breadcrumb],
            BodyHtml = body.ToString(),
        });
    }

    public async Task<SeoPageResult> RenderGuideAsync(string regionSlug, string comuneSlug, CancellationToken cancellationToken = default)
    {
        var page = await seoContentService.GetComplianceGuideAsync(regionSlug, comuneSlug, cancellationToken);
        return page is null ? NotFound() : Ok(BuildEditorialDocument(page));
    }

    public async Task<SeoPageResult> RenderTouristTaxAsync(string comuneSlug, CancellationToken cancellationToken = default)
    {
        var page = await seoContentService.GetTouristTaxPageAsync(comuneSlug, cancellationToken);
        return page is null ? NotFound() : Ok(BuildEditorialDocument(page));
    }

    public async Task<SeoPageResult> RenderHubAsync(CancellationToken cancellationToken = default)
    {
        var hub = await seoContentService.GetPublishedPagesAsync(cancellationToken);
        var language = Language();
        var title = T("SeoHubTitle");
        var description = T("SeoHubDescription");
        var entries = hub.Pages
            .Select(p => (Page: p, Url: links.TryPublicPage(p.Path) ?? p.Path))
            .ToList();

        var body = new StringBuilder();
        body.Append("<main>\n<h1>").Append(SeoHtml.Encode(T("SeoHubHeading"))).Append("</h1>\n");
        if (entries.Count == 0)
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoHubEmpty"))).Append("</p>\n");

        AppendHubSection(body, T("SeoHubGuides"), entries.Where(e => e.Page.PageType != SeoPageType.TouristTaxCalc));
        AppendHubSection(body, T("SeoHubCalculators"), entries.Where(e => e.Page.PageType == SeoPageType.TouristTaxCalc));
        body.Append("</main>\n");

        return Ok(new SeoDocument
        {
            Language = language,
            Title = title,
            Description = description,
            CanonicalUrl = hub.CanonicalUrl,
            // A hub with nothing to list is as thin as a booking site without properties.
            Indexable = entries.Count > 0,
            SiteName = PlatformName,
            JsonLd = [SeoJsonLd.CollectionPage(T("SeoHubHeading"), description, hub.CanonicalUrl, language, entries.Select(e => (e.Page.Title, (string?)e.Url)))],
            BodyHtml = body.ToString(),
        });
    }

    public async Task<string?> BuildOrgSitemapAsync(string orgSlug, CancellationToken cancellationToken = default)
    {
        links.EnsureConfigured();

        var org = await orgService.GetPublicBySlugAsync(orgSlug, cancellationToken);
        // An org with its own host has its sitemap there (/sitemap.xml of that host): a sitemap may only list URLs of its own
        // host, and the canonical URLs of such an org are not on the platform.
        if (org is null || !string.Equals(org.Slug, orgSlug, StringComparison.Ordinal) || urls.OwnHost(org) is not null)
            return null;

        return await BuildOrgUrlSetAsync(org, cancellationToken);
    }

    public async Task<HostSitemap> BuildHostSitemapAsync(string host, CancellationToken cancellationToken = default)
    {
        var normalizedHost = PublicSiteHosts.Normalize(host);
        var resolved = normalizedHost is null ? null : await hostResolver.ResolveAsync(normalizedHost, cancellationToken);
        if (resolved is null)
            return new HostSitemap(false, null);

        var org = await orgService.GetPublicBySlugAsync(resolved.Slug, cancellationToken);
        if (org is null || org.Id != resolved.OrgId || urls.OwnHost(org) != normalizedHost)
            return new HostSitemap(false, null);

        return new HostSitemap(true, await BuildOrgUrlSetAsync(org, cancellationToken));
    }

    /// <summary>The landing page and the published properties of an org, on the URLs its pages declare as canonical.</summary>
    private async Task<string?> BuildOrgUrlSetAsync(Org org, CancellationToken cancellationToken)
    {
        var properties = await PublishedProperties(org.Id)
            .OrderBy(p => p.City).ThenBy(p => p.Name)
            .Take(SeoSitemaps.MaxEntries - 1)
            .Select(p => new { p.Id, p.Slug, p.UpdatedAt })
            .ToListAsync(cancellationToken);
        if (properties.Count == 0)
            return null;

        var lastModified = properties.Max(p => p.UpdatedAt);
        var entries = new List<(string Url, DateTime LastModified)>
        {
            (urls.TryLandingUrl(org) ?? throw NotConfigured(), lastModified > org.UpdatedAt ? lastModified : org.UpdatedAt),
        };
        entries.AddRange(properties.Select(p =>
            (urls.TryPropertyUrl(org, string.IsNullOrWhiteSpace(p.Slug) ? p.Id.ToString() : p.Slug) ?? throw NotConfigured(), p.UpdatedAt)));

        return SeoSitemaps.UrlSet(entries);
    }

    public async Task<string> BuildOrgSitemapIndexAsync(CancellationToken cancellationToken = default)
    {
        links.EnsureConfigured();

        // IgnoreQueryFilters: a public read across every org, with the published rule as its filter.
        var published = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(PublicListing.IsPublished)
            .GroupBy(p => p.OrgId)
            .Select(g => new { OrgId = g.Key, LastModified = g.Max(p => p.UpdatedAt) })
            .ToListAsync(cancellationToken);
        var orgIds = published.Select(p => p.OrgId).ToList();

        var orgs = await db.Orgs.AsNoTracking()
            .Where(o => o.IsActive && orgIds.Contains(o.Id))
            .OrderBy(o => o.Slug)
            .Take(SeoSitemaps.MaxEntries)
            .ToListAsync(cancellationToken);
        var lastModifiedByOrg = published.ToDictionary(p => p.OrgId, p => p.LastModified);

        // The orgs with their own host are not listed: their sitemap is on their host, not under the platform path.
        return SeoSitemaps.Index(orgs
            .Where(o => urls.OwnHost(o) is null)
            .Select(o => (links.PublicPage(PublicSitePaths.OrgSitemap(o.Slug)),
                lastModifiedByOrg[o.Id] > o.UpdatedAt ? lastModifiedByOrg[o.Id] : o.UpdatedAt)));
    }

    private static EmailConfigurationException NotConfigured() =>
        new("App:PublicSiteBaseUrl is missing or invalid: public links cannot be built.");

    // ─── Editorial pages (/p/*) ──────────────────────────────────────────────────────────────────────

    private SeoDocument BuildEditorialDocument(SeoPagePublicDto page)
    {
        var language = Language();

        var body = new StringBuilder();
        body.Append("<main>\n<article>\n<header>\n<p>").Append(SeoHtml.Encode($"{page.ComuneName} · {page.RegionCode}"))
            .Append("</p>\n<h1>").Append(SeoHtml.Encode(page.Title)).Append("</h1>\n</header>\n");
        // Sanitized when it was approved and again on read (FD-15); once more here, as this is the last step before HTML.
        body.Append(SeoHtmlSanitizer.Sanitize(page.BodyHtml)).Append('\n');
        body.Append("<p><a href=\"").Append(SeoHtml.Encode(page.Cta.SignupUrl)).Append("\">")
            .Append(SeoHtml.Encode(T("SeoGuideCta"))).Append("</a></p>\n");
        body.Append("<footer>\n<p>").Append(SeoHtml.Encode(page.Disclaimers.LastUpdated)).Append("</p>\n<p>")
            .Append(SeoHtml.Encode(page.Disclaimers.NotLegalAdvice)).Append("</p>\n<p>")
            .Append(SeoHtml.Encode(page.Disclaimers.AiGenerated)).Append("</p>\n</footer>\n</article>\n</main>\n");

        // Indexable under the same rule as the sitemap: it has text and, for a calculator, a tourist tax rate in force
        // (the page itself is only served with an approved revision, SE-01).
        var indexable = !string.IsNullOrWhiteSpace(page.BodyHtml)
            && (page.PageType != SeoPageType.TouristTaxCalc || page.TouristTaxRates.Count > 0);

        var breadcrumb = SeoJsonLd.BreadcrumbList(
        [
            (T("SeoHubHeading"), links.TryPublicPage(SeoPagePaths.Hub)),
            (page.Title, page.CanonicalUrl),
        ]);

        return new SeoDocument
        {
            Language = language,
            Title = page.Title,
            Description = page.MetaDescription,
            CanonicalUrl = page.CanonicalUrl,
            Indexable = indexable,
            SiteName = PlatformName,
            OgType = "article",
            JsonLd =
            [
                SeoJsonLd.Article(page.Title, page.MetaDescription, page.CanonicalUrl, page.LastRefreshedAt, language, PlatformName),
                breadcrumb,
            ],
            BodyHtml = body.ToString(),
        };
    }

    private static void AppendHubSection(
        StringBuilder body,
        string heading,
        IEnumerable<(SeoPublishedPageDto Page, string Url)> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
            return;

        body.Append("<section>\n<h2>").Append(SeoHtml.Encode(heading)).Append("</h2>\n<ul>\n");
        foreach (var (page, url) in list)
        {
            body.Append("<li><a href=\"").Append(SeoHtml.Encode(url)).Append("\">").Append(SeoHtml.Encode(page.Title))
                .Append("</a></li>\n");
        }

        body.Append("</ul>\n</section>\n");
    }

    // ─── Booking sites ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The published properties of an org. IgnoreQueryFilters: a public read of one org's published data, filtered by
    /// its id and by the published rule, so a signed-in host of another org gets the same page as an anonymous visitor.
    /// </summary>
    private IQueryable<Property> PublishedProperties(Guid orgId) =>
        db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => p.OrgId == orgId)
            .Where(PublicListing.IsPublished);

    private static readonly System.Linq.Expressions.Expression<Func<Property, SeoPropertyRow>> ProjectRow = p => new SeoPropertyRow
    {
        Id = p.Id,
        Slug = p.Slug,
        Name = p.Name,
        Description = p.Description,
        City = p.City,
        PostalCode = p.PostalCode,
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        Bedrooms = p.Bedrooms,
        Bathrooms = p.Bathrooms,
        MaxGuests = p.MaxGuests,
        NightlyRate = p.NightlyRate,
        CleaningFee = p.CleaningFee,
        Amenities = p.Amenities,
        PhotoUrls = p.PhotoUrls,
        CinCode = p.CinCode,
        HouseRules = p.HouseRules,
        CancellationPolicySummary = p.CancellationPolicy != null ? p.CancellationPolicy.Description : string.Empty,
    };

    /// <summary>
    /// True when a page requested on <paramref name="requestHost"/> may be indexed for <paramref name="orgId"/>: no host
    /// (a direct call), the web app's own host, or a host that resolves to the same org (a subdomain or a verified
    /// custom domain, never a domain waiting for its verification). Anything else, including a value that is not a host
    /// name, is not.
    /// </summary>
    private async Task<bool> IsIndexableHostAsync(string? requestHost, Guid orgId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestHost))
            return true;

        var host = PublicSiteHosts.Normalize(requestHost);
        if (host is null)
            return false;

        if (links.IsPublicSiteHost(host))
            return true;

        var resolved = await hostResolver.ResolveAsync(host, cancellationToken);
        return resolved?.OrgId == orgId;
    }

    private string OrgDescription(Org org, string displayName, IReadOnlyList<SeoPropertyRow> properties)
    {
        if (!string.IsNullOrWhiteSpace(org.Tagline))
            return SeoHtml.Truncate(org.Tagline);

        if (properties.Count == 0)
            return T("SeoOrgDescriptionNone", displayName);

        var cities = properties
            .Select(p => SeoHtml.Collapse(p.City))
            .Where(city => city.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToList();
        var cityList = string.Join(", ", cities);

        return properties.Count == 1
            ? T("SeoOrgDescriptionOne", displayName, cityList)
            : T("SeoOrgDescriptionMany", displayName, properties.Count, cityList);
    }

    private string PropertyDescription(SeoPropertyRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Description))
            return SeoHtml.Truncate(row.Description);

        return row.MaxGuests > 0
            ? T("SeoPropertyDescriptionFallback", row.Name, row.City, row.MaxGuests, row.Bedrooms, row.Bathrooms)
            : T("SeoPropertyDescriptionBasic", row.Name, row.City);
    }

    /// <summary>The last segment of the property's public path: its slug, the id only when it has none.</summary>
    private static string PropertySegment(SeoPropertyRow row) =>
        string.IsNullOrWhiteSpace(row.Slug) ? row.Id.ToString() : row.Slug;

    private static string? FirstPhoto(IEnumerable<SeoPropertyRow> properties) =>
        properties.SelectMany(p => p.PhotoUrls).Select(SeoHtml.TryHttpsUrl).OfType<string>().FirstOrDefault();

    private void AppendFact(StringBuilder body, string label, string value) =>
        body.Append("<li>").Append(SeoHtml.Encode($"{label}: {value}")).Append("</li>\n");

    private static void AppendSection(StringBuilder body, string heading, string? text)
    {
        var paragraphs = SeoHtml.Paragraphs(text);
        if (paragraphs.Length == 0)
            return;

        body.Append("<h2>").Append(SeoHtml.Encode(heading)).Append("</h2>\n").Append(paragraphs);
    }

    private string AmenityLabel(PropertyAmenity amenity)
    {
        var label = localizer[$"SeoAmenity_{amenity}"];
        return label.ResourceNotFound ? amenity.ToString() : label.Value;
    }

    // ─── Results and texts ───────────────────────────────────────────────────────────────────────────

    private SeoPageResult Ok(SeoDocument document) => new(SeoPageStatus.Ok, SeoHtmlRenderer.Render(document));

    private SeoPageResult Moved(string path) =>
        new(SeoPageStatus.MovedPermanently, SeoHtmlRenderer.Render(new SeoDocument
        {
            Language = Language(),
            Title = PlatformName,
            Indexable = false,
            SiteName = PlatformName,
            BodyHtml = $"<main>\n<p><a href=\"{SeoHtml.Encode(path)}\">{SeoHtml.Encode(path)}</a></p>\n</main>\n",
        }), path);

    /// <summary>A host that serves no org site: a real 404 the crawler function tells apart from a missing page.</summary>
    private SeoPageResult UnknownHost() => NotFound() with { Status = SeoPageStatus.UnknownHost };

    private SeoPageResult NotFound() =>
        new(SeoPageStatus.NotFound, SeoHtmlRenderer.Render(new SeoDocument
        {
            Language = Language(),
            Title = T("SeoNotFoundTitle"),
            Indexable = false,
            SiteName = PlatformName,
            BodyHtml = $"<main>\n<h1>{SeoHtml.Encode(T("SeoNotFoundTitle"))}</h1>\n<p>{SeoHtml.Encode(T("SeoNotFoundText"))}</p>\n</main>\n",
        }));

    private string T(string key, params object[] arguments)
    {
        var text = localizer[key, arguments];
        return text.Value;
    }

    private static string Language() =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";

    private static string Money(decimal amount, CultureInfo culture) =>
        "€" + (amount % 1m == 0m ? amount.ToString("0", culture) : amount.ToString("0.00", culture));

    /// <summary>The columns of a published property that the crawler pages read.</summary>
    internal sealed class SeoPropertyRow
    {
        public Guid Id { get; init; }
        public string? Slug { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public decimal Latitude { get; init; }
        public decimal Longitude { get; init; }
        public int Bedrooms { get; init; }
        public int Bathrooms { get; init; }
        public int MaxGuests { get; init; }
        public decimal NightlyRate { get; init; }
        public decimal CleaningFee { get; init; }
        public List<PropertyAmenity> Amenities { get; init; } = [];
        public List<string> PhotoUrls { get; init; } = [];
        public string? CinCode { get; init; }
        public string HouseRules { get; init; } = string.Empty;
        public string CancellationPolicySummary { get; init; } = string.Empty;
    }
}
