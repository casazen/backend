using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Supplier;
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
/// preview deployment, gets <c>noindex</c>: the request host is only compared, never written into a page.</item>
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
    IStringLocalizer<SharedResources> localizer,
    IFeatureFlags featureFlags,
    IComuneDirectory comuneDirectory) : IPublicSeoService
{
    /// <summary>Brand of the platform pages (<c>og:site_name</c>, publisher of the guides).</summary>
    public const string PlatformName = "CasaZen";

    /// <summary>Most properties an org landing page lists, like the booking site itself.</summary>
    public const int MaxListedProperties = 50;

    private const int MaxImages = 8;

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
                    body.Append(". ").Append(SeoHtml.Encode(T("SeoPriceFrom", Money(row.NightlyRate, row.Currency, culture))));
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
            AppendFact(body, T("SeoLabelCleaningFee"), Money(row.CleaningFee, row.Currency, culture));
        if (cin is not null)
            AppendFact(body, T("SeoLabelCin"), cin);
        body.Append("</ul>\n");

        if (row.NightlyRate > 0m)
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoPriceFrom", Money(row.NightlyRate, row.Currency, culture)))).Append("</p>\n");

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
            StayCurrency.Normalize(row.Currency),
            orgName,
            orgUrl,
            row.AreaSqm,
            row.CheckInFrom,
            row.CheckOutBy,
            row.PetsPolicy == PetsPolicy.No);

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

    public async Task<SeoPageResult> RenderSupplierAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalized = SupplierShowcaseSlug.Normalize(slug);
        if (!SupplierShowcaseSlug.IsLookupable(normalized, PublicShowcaseLimits.SlugMaxLength))
            return NotFound();

        // Same rule as the public API: only an Active supplier; pending, suspended and unknown are one 404.
        var profile = await db.SupplierProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ShowcaseSlug == normalized && p.Status == SupplierStatus.Active, cancellationToken);
        if (profile is null)
            return NotFound();

        if (!string.Equals(slug, normalized, StringComparison.Ordinal))
            return Moved(PublicSitePaths.SupplierShowcase(normalized));

        var listed = await comuneDirectory.GetByIstatCodesAsync(SupplierComuniView.IstatCodes(profile), cancellationToken);
        var name = SeoHtml.Collapse(profile.LegalName);
        var categories = ReadStrings(profile.CategoriesJson).Select(SupplierCategoryLabel).ToList();
        var comuni = SupplierComuniView.Names(profile, listed);
        var photos = ReadStrings(profile.PhotoUrlsJson).Select(SeoHtml.TryHttpsUrl).OfType<string>().Take(MaxImages).ToList();
        var canonical = links.TryPublicPage(PublicSitePaths.SupplierShowcase(normalized));
        var bio = SeoHtml.Collapse(profile.Bio);

        var body = new StringBuilder();
        body.Append("<main>\n<article>\n<h1>").Append(SeoHtml.Encode(name)).Append("</h1>\n");
        foreach (var photo in photos)
        {
            body.Append("<img src=\"").Append(SeoHtml.Encode(photo)).Append("\" alt=\"").Append(SeoHtml.Encode(name)).Append("\">\n");
        }

        body.Append(SeoHtml.Paragraphs(profile.Bio));
        if (categories.Count > 0)
        {
            body.Append("<h2>").Append(SeoHtml.Encode(T("SeoSupplierServices"))).Append("</h2>\n<ul>\n");
            foreach (var category in categories)
                body.Append("<li>").Append(SeoHtml.Encode(category)).Append("</li>\n");
            body.Append("</ul>\n");
        }

        if (comuni.Count > 0)
        {
            body.Append("<h2>").Append(SeoHtml.Encode(T("SeoSupplierAreas"))).Append("</h2>\n<ul>\n");
            foreach (var comune in comuni)
                body.Append("<li>").Append(SeoHtml.Encode(comune)).Append("</li>\n");
            body.Append("</ul>\n");
        }

        body.Append("</article>\n</main>\n");

        return Ok(new SeoDocument
        {
            Language = Language(),
            Title = T("SeoSupplierTitle", name),
            Description = SeoHtml.Truncate(string.IsNullOrEmpty(bio) ? SupplierDescription(name, categories, comuni) : bio),
            CanonicalUrl = canonical,
            // A showcase with no category and no text is as thin as a booking site without properties.
            Indexable = SupplierHasContent(profile),
            SiteName = PlatformName,
            ImageUrl = photos.FirstOrDefault(),
            JsonLd =
            [
                SeoJsonLd.Supplier(name, canonical, SeoHtml.Truncate(profile.Bio), photos, comuni),
                SeoJsonLd.BreadcrumbList([(PlatformName, links.TryPublicPage("/")), (name, canonical)]),
            ],
            BodyHtml = body.ToString(),
        });
    }

    public async Task<string> BuildSupplierSitemapAsync(CancellationToken cancellationToken = default)
    {
        links.EnsureConfigured();

        var rows = await db.SupplierProfiles.AsNoTracking()
            .Where(p => p.Status == SupplierStatus.Active && p.ShowcaseSlug != null && p.ShowcaseSlug != "")
            .OrderBy(p => p.ShowcaseSlug)
            .Take(SeoSitemaps.MaxEntries)
            .Select(p => new { p.ShowcaseSlug, p.CategoriesJson, p.Bio, p.UpdatedAt })
            .ToListAsync(cancellationToken);

        return SeoSitemaps.UrlSet(rows
            .Where(r => ReadStrings(r.CategoriesJson).Count > 0 || !string.IsNullOrWhiteSpace(r.Bio))
            .Select(r => (links.PublicPage(PublicSitePaths.SupplierShowcase(r.ShowcaseSlug!)), r.UpdatedAt)));
    }

    private static bool SupplierHasContent(Casazen.Core.Entities.SupplierProfile profile) =>
        ReadStrings(profile.CategoriesJson).Count > 0 || !string.IsNullOrWhiteSpace(profile.Bio);

    private static List<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string SupplierCategoryLabel(string code)
    {
        var label = localizer[$"SeoSupplierCategory_{code}"];
        return label.ResourceNotFound ? code : label.Value;
    }

    private string SupplierDescription(string name, IReadOnlyList<string> categories, IReadOnlyList<string> comuni) =>
        categories.Count > 0 && comuni.Count > 0
            ? T("SeoSupplierDescriptionFull", name, string.Join(", ", categories), string.Join(", ", comuni.Take(5)))
            : T("SeoSupplierDescriptionBasic", name);

    public async Task<SeoPageResult> RenderGuideAsync(string regionSlug, string comuneSlug, CancellationToken cancellationToken = default)
    {
        var page = await seoContentService.GetComplianceGuideAsync(regionSlug, comuneSlug, cancellationToken);
        return page is null ? NotFound() : Ok(await BuildEditorialDocumentAsync(page, cancellationToken));
    }

    public async Task<SeoPageResult> RenderTouristTaxAsync(string comuneSlug, CancellationToken cancellationToken = default)
    {
        var page = await seoContentService.GetTouristTaxPageAsync(comuneSlug, cancellationToken);
        return page is null ? NotFound() : Ok(await BuildEditorialDocumentAsync(page, cancellationToken));
    }

    public async Task<SeoPageResult> RenderHubAsync(CancellationToken cancellationToken = default)
    {
        var hub = await seoContentService.GetPublishedPagesAsync(cancellationToken);
        var language = Language();
        var title = T("SeoHubTitle");
        var entries = hub.Pages
            .Select(p => (Page: p, Url: links.TryPublicPage(p.Path) ?? p.Path))
            .ToList();
        var guides = entries.Where(e => e.Page.PageType != SeoPageType.TouristTaxCalc).ToList();
        var calculators = entries.Where(e => e.Page.PageType == SeoPageType.TouristTaxCalc).ToList();
        var description = entries.Count == 0 ? T("SeoHubDescription") : HubDescription(hub.Pages);
        var homeUrl = HomeUrl();

        var body = new StringBuilder();
        body.Append(SeoPageChrome.Header(PlatformName, homeUrl, T("SeoNavMainLabel"), SiteLinks()));
        body.Append(SeoPageChrome.Breadcrumb(T("SeoNavBreadcrumbLabel"), [(PlatformName, homeUrl), (T("SeoHubHeading"), null)]));
        body.Append("<main>\n<h1>").Append(SeoHtml.Encode(T("SeoHubHeading"))).Append("</h1>\n");
        if (entries.Count == 0)
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoHubEmpty"))).Append("</p>\n");
        else
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoHubIntro", guides.Count, calculators.Count))).Append("</p>\n");

        AppendHubSection(body, T("SeoHubGuides"), guides);
        AppendHubSection(body, T("SeoHubCalculators"), calculators);
        body.Append("</main>\n");
        body.Append(SeoPageChrome.Footer(T("SeoFooterTagline"), T("SeoNavFooterLabel"), SiteLinks()));

        return Ok(new SeoDocument
        {
            Language = language,
            Title = title,
            Description = description,
            CanonicalUrl = hub.CanonicalUrl,
            // A hub with nothing to list is as thin as a booking site without properties.
            Indexable = entries.Count > 0,
            SiteName = PlatformName,
            ImageUrl = BrandImageUrl(),
            TwitterCard = "summary",
            JsonLd =
            [
                SeoJsonLd.CollectionPage(T("SeoHubHeading"), description, hub.CanonicalUrl, language, entries.Select(e => (e.Page.Title, (string?)e.Url))),
                SeoJsonLd.WebSite(PlatformName, links.TryPublicPage(SeoPagePaths.SiteHome), language),
                SeoJsonLd.Organization(PlatformName, links.TryPublicPage(SeoPagePaths.SiteHome), BrandImageUrl(), null),
            ],
            BodyHtml = body.ToString(),
        });
    }

    /// <summary>
    /// The description of the hub from what it really lists: four of its comuni by name, those with both a guide and a tourist tax
    /// page first (the pages with the most to say), then by name.
    /// </summary>
    private string HubDescription(IReadOnlyList<SeoPublishedPageDto> pages)
    {
        var names = pages
            .GroupBy(p => p.ComuneSlug)
            .OrderBy(g => g.Select(p => p.PageType).Distinct().Count() > 1 ? 0 : 1)
            .ThenBy(g => g.First().ComuneName, ItalianCollator)
            .Select(g => g.First().ComuneName)
            .Take(4);
        return SeoHtml.Truncate(T("SeoHubMetaDescription", string.Join(", ", names)));
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

    /// <summary>Longest title the page gets from its data; a comune with a long name keeps the title the page was approved with.</summary>
    private const int MaxMetaTitleLength = 70;

    /// <summary>Pages of the same kind and region listed under «Vedi anche».</summary>
    private const int MaxNeighbours = 6;

    private static readonly StringComparer ItalianCollator = StringComparer.Create(CultureInfo.GetCultureInfo("it-IT"), ignoreCase: true);

    /// <summary>A question of the page and its answer, both built from data the page really has (never from the AI).</summary>
    private sealed record FaqItem(string Question, string Answer, string? SourceUrl = null, string? SourceLabel = null);

    private async Task<SeoDocument> BuildEditorialDocumentAsync(SeoPagePublicDto page, CancellationToken cancellationToken)
    {
        var language = Language();
        var culture = CultureInfo.CurrentUICulture;
        var published = (await seoContentService.GetPublishedPagesAsync(cancellationToken)).Pages;
        var facts = SeoTouristTaxFacts.From(page.TouristTaxRates);
        var homeUrl = HomeUrl();
        var regionName = RegionName(page.RegionSlug) ?? page.RegionCode;
        var (title, description) = EditorialMeta(page, facts, culture);
        // The questions are the tourist tax ones, so they live on the page of the tax; the guide only shows the summary.
        var faq = page.PageType == SeoPageType.TouristTaxCalc && facts is not null ? BuildFaq(page, facts, culture) : [];

        var body = new StringBuilder();
        body.Append(SeoPageChrome.Header(PlatformName, homeUrl, T("SeoNavMainLabel"), SiteLinks()));
        body.Append(SeoPageChrome.Breadcrumb(
            T("SeoNavBreadcrumbLabel"),
            [(PlatformName, homeUrl), (T("SeoHubHeading"), links.TryPublicPage(SeoPagePaths.Hub) ?? SeoPagePaths.Hub), (page.Title, null)]));
        body.Append("<main>\n<article>\n<header>\n<p>").Append(SeoHtml.Encode($"{page.ComuneName} · {regionName}"))
            .Append("</p>\n<h1>").Append(SeoHtml.Encode(page.Title)).Append("</h1>\n</header>\n");
        if (facts is not null)
            AppendFactsBlock(body, page, facts, culture);
        // Sanitized when it was approved and again on read (FD-15); once more here, as this is the last step before HTML.
        body.Append(SeoHtmlSanitizer.Sanitize(page.BodyHtml)).Append('\n');
        AppendFaqBlock(body, faq);
        // SEO-INVITE: no link to the signup while CasaZen is on private invitation (flag SeoSignupCta off).
        if (page.Cta is not null && featureFlags.IsEnabled(FeatureFlags.SeoSignupCta))
        {
            body.Append("<p><a href=\"").Append(SeoHtml.Encode(page.Cta.SignupUrl)).Append("\">")
                .Append(SeoHtml.Encode(T("SeoGuideCta"))).Append("</a></p>\n");
        }

        var lastUpdated = page.LastRefreshedAt.HasValue
            ? T("SeoDisclaimerLastUpdated", page.LastRefreshedAt.Value.ToString("d MMMM yyyy", culture))
            : T("SeoDisclaimerLastUpdatedUnknown");
        body.Append("<footer>\n<p>").Append(SeoHtml.Encode(lastUpdated)).Append("</p>\n<p>")
            .Append(SeoHtml.Encode(T("SeoDisclaimerNotLegalAdvice"))).Append("</p>\n");
        // EU AI Act transparency (A8-27): only for text that the AI generated, never a constant.
        if (page.AiGenerated)
            body.Append("<p>").Append(SeoHtml.Encode(T("SeoDisclaimerAiGenerated"))).Append("</p>\n");
        body.Append("</footer>\n</article>\n");
        body.Append(SeoPageChrome.SeeAlso(T("SeoSeeAlsoHeading"), SeeAlsoGroups(page, published, regionName)));
        body.Append("</main>\n");
        body.Append(SeoPageChrome.Footer(T("SeoFooterTagline"), T("SeoNavFooterLabel"), SiteLinks()));

        // Indexable under the same rule as the sitemap: it has text and, for a calculator, a tourist tax rate in force
        // (the page itself is only served with an approved revision, SE-01).
        var indexable = !string.IsNullOrWhiteSpace(page.BodyHtml)
            && (page.PageType != SeoPageType.TouristTaxCalc || page.TouristTaxRates.Count > 0);

        var homeAbsolute = links.TryPublicPage(SeoPagePaths.SiteHome);
        var jsonLd = new List<JsonObject>
        {
            SeoJsonLd.Article(page.Title, description, page.CanonicalUrl, page.LastRefreshedAt, language, PlatformName),
            SeoJsonLd.BreadcrumbList(
            [
                (PlatformName, homeAbsolute),
                (T("SeoHubHeading"), links.TryPublicPage(SeoPagePaths.Hub)),
                (page.Title, page.CanonicalUrl),
            ]),
            SeoJsonLd.WebSite(PlatformName, homeAbsolute, language),
            SeoJsonLd.Organization(PlatformName, homeAbsolute, BrandImageUrl(), null),
        };
        // The markup says what the visible questions say: both come from the same list.
        if (faq.Count > 0)
            jsonLd.Add(SeoJsonLd.FaqPage(faq.Select(item => (item.Question, item.Answer))));

        return new SeoDocument
        {
            Language = language,
            Title = title,
            Description = description,
            CanonicalUrl = page.CanonicalUrl,
            Indexable = indexable,
            SiteName = PlatformName,
            OgType = "article",
            ImageUrl = BrandImageUrl(),
            TwitterCard = "summary",
            JsonLd = jsonLd,
            BodyHtml = body.ToString(),
        };
    }

    /// <summary>
    /// The title and the description a search result shows. The title carries the comune, what the page is and the year it was last
    /// verified; the description says the real figure when the page has one. Without those data (no refresh date, several or no
    /// rate, a title that would be too long) the page keeps what it was approved with.
    /// </summary>
    private (string Title, string? Description) EditorialMeta(SeoPagePublicDto page, SeoTouristTaxFacts? facts, CultureInfo culture)
    {
        var title = page.Title;
        var description = page.MetaDescription;
        var isTax = page.PageType == SeoPageType.TouristTaxCalc;

        if (page.LastRefreshedAt is { } refreshedAt)
        {
            var candidate = T(isTax ? "SeoTaxMetaTitle" : "SeoGuideMetaTitle", page.ComuneName, refreshedAt.Year);
            if (candidate.Length <= MaxMetaTitleLength)
                title = candidate;
        }

        if (facts is not null)
        {
            var summary = RateSummary(facts, culture);
            var candidate = isTax
                ? T("SeoTaxMetaDescription", page.ComuneName, summary)
                : page.LastRefreshedAt is { } refreshed
                    ? T("SeoGuideMetaDescription", page.ComuneName, summary, refreshed.ToString("MMMM yyyy", culture))
                    : null;
            if (!string.IsNullOrWhiteSpace(candidate))
                description = SeoHtml.Truncate(candidate);
        }

        return (title, description);
    }

    // ── What the page can say from its data: the facts of the tax, the questions, the related pages ──

    private static string Euro(decimal amount, CultureInfo culture) => amount.ToString("0.00", culture) + " €";

    /// <summary>The rate in a sentence: «3,00 € a persona per notte», or the percentage of the night with its ceiling.</summary>
    private string RateSentence(SeoTouristTaxFacts facts, CultureInfo culture)
    {
        if (facts.Method == TouristTaxCalculationMethod.PercentOfNightlyPrice)
        {
            var percent = T("SeoFactRatePercent", facts.Rate.ToString("0.##", culture));
            return facts.CapPerPersonPerNight is { } cap ? percent + T("SeoFactRateCap", Euro(cap, culture)) : percent;
        }

        return T("SeoFactRateFixed", Euro(facts.Rate, culture));
    }

    /// <summary>The rate with the nights it applies to, when the comune caps them.</summary>
    private string RateSummary(SeoTouristTaxFacts facts, CultureInfo culture) =>
        facts.MaxNights is { } nights
            ? $"{RateSentence(facts, culture)}, {T("SeoFactNightsValue", nights)}"
            : RateSentence(facts, culture);

    private void AppendFactsBlock(StringBuilder body, SeoPagePublicDto page, SeoTouristTaxFacts facts, CultureInfo culture)
    {
        body.Append("<section>\n<h2>").Append(SeoHtml.Encode(T("SeoFactsHeading", page.ComuneName))).Append("</h2>\n<ul>\n");
        AppendFact(body, T("SeoFactRateLabel"), RateSentence(facts, culture));
        if (facts.MaxNights is { } nights)
            AppendFact(body, T("SeoFactNightsLabel"), T("SeoFactNightsValue", nights));
        if (facts.ExemptUnderAge > 0)
            AppendFact(body, T("SeoFactExemptLabel"), T("SeoFactExemptValue", facts.ExemptUnderAge));
        AppendFact(body, T("SeoFactSinceLabel"), facts.EffectiveFrom.ToString("d MMMM yyyy", culture));
        if (facts.SourceUrl is { } source)
        {
            body.Append("<li>").Append(SeoHtml.Encode(T("SeoFactSourceLabel"))).Append(": <a href=\"").Append(SeoHtml.Encode(source))
                .Append("\" rel=\"noopener noreferrer\">").Append(SeoHtml.Encode(T("SeoFactSourceLink", page.ComuneName)))
                .Append("</a></li>\n");
        }

        body.Append("</ul>\n</section>\n");
    }

    private IReadOnlyList<FaqItem> BuildFaq(SeoPagePublicDto page, SeoTouristTaxFacts facts, CultureInfo culture)
    {
        var comune = page.ComuneName;
        var items = new List<FaqItem>
        {
            new(T("SeoFaqRateQuestion", comune), T("SeoFaqRateAnswer", comune, RateSummary(facts, culture))),
        };
        if (facts.MaxNights is { } nights)
            items.Add(new FaqItem(T("SeoFaqNightsQuestion", comune), T("SeoFaqNightsAnswer", nights)));
        if (facts.ExemptUnderAge > 0)
            items.Add(new FaqItem(T("SeoFaqExemptQuestion", comune), T("SeoFaqExemptAnswer", facts.ExemptUnderAge)));
        if (facts.SourceUrl is { } source)
            items.Add(new FaqItem(T("SeoFaqSourceQuestion", comune), T("SeoFaqSourceAnswer", comune), source, T("SeoFactSourceLink", comune)));
        return items;
    }

    private void AppendFaqBlock(StringBuilder body, IReadOnlyList<FaqItem> faq)
    {
        if (faq.Count == 0)
            return;

        body.Append("<section>\n<h2>").Append(SeoHtml.Encode(T("SeoFaqHeading"))).Append("</h2>\n");
        foreach (var item in faq)
        {
            body.Append("<h3>").Append(SeoHtml.Encode(item.Question)).Append("</h3>\n<p>").Append(SeoHtml.Encode(item.Answer));
            if (item.SourceUrl is not null)
            {
                body.Append(" <a href=\"").Append(SeoHtml.Encode(item.SourceUrl)).Append("\" rel=\"noopener noreferrer\">")
                    .Append(SeoHtml.Encode(item.SourceLabel)).Append("</a>");
            }

            body.Append("</p>\n");
        }

        body.Append("</section>\n");
    }

    /// <summary>
    /// «Vedi anche»: the other page of the same comune (the guide from the tax page and back) and the hub, then the pages of the
    /// same kind in the same region. A page used to have no link to any of them.
    /// </summary>
    private IReadOnlyList<SeoLinkGroup> SeeAlsoGroups(SeoPagePublicDto page, IReadOnlyList<SeoPublishedPageDto> published, string regionName)
    {
        var isGuide = page.PageType != SeoPageType.TouristTaxCalc;
        var sibling = published.FirstOrDefault(p => p.ComuneSlug == page.ComuneSlug && (p.PageType == SeoPageType.TouristTaxCalc) == isGuide);

        var own = new List<SeoLink>();
        if (sibling is not null)
            own.Add(new SeoLink(sibling.Title, PageUrl(sibling)));
        own.Add(new SeoLink(T("SeoSeeAlsoHub"), links.TryPublicPage(SeoPagePaths.Hub) ?? SeoPagePaths.Hub));

        var others = Neighbours(published, page).Select(p => new SeoLink(p.Title, PageUrl(p))).ToList();
        return
        [
            new SeoLinkGroup(null, own),
            new SeoLinkGroup(T(isGuide ? "SeoSeeAlsoOtherGuides" : "SeoSeeAlsoOtherCalculators", regionName), others),
        ];
    }

    private string PageUrl(SeoPublishedPageDto page) => links.TryPublicPage(page.Path) ?? page.Path;

    /// <summary>
    /// The pages of the same kind in the same region, starting from the one after this comune in alphabetical order and wrapping
    /// round: every page links to different neighbours, and every page is linked from several others.
    /// </summary>
    private static List<SeoPublishedPageDto> Neighbours(IReadOnlyList<SeoPublishedPageDto> published, SeoPagePublicDto page)
    {
        var sameKind = published
            .Where(p => p.PageType == page.PageType && p.RegionSlug == page.RegionSlug)
            .OrderBy(p => p.ComuneName, ItalianCollator)
            .ToList();
        var index = sameKind.FindIndex(p => p.ComuneSlug == page.ComuneSlug);
        if (index < 0)
            return sameKind.Take(MaxNeighbours).ToList();

        return Enumerable.Range(1, sameKind.Count - 1)
            .Select(offset => sameKind[(index + offset) % sameKind.Count])
            .Take(MaxNeighbours)
            .ToList();
    }

    /// <summary>The name of a region for the page («Lombardia»); the official list writes bilingual regions with both names, the first is used.</summary>
    private static string? RegionName(string? slug)
    {
        var name = ItalianRegions.FindBySlug(slug)?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? name[..slash].Trim() : name.Trim();
    }

    // ── The chrome shared by the editorial pages ──

    private string HomeUrl() => links.TryPublicPage(SeoPagePaths.SiteHome) ?? SeoPagePaths.SiteHome;

    /// <summary>The brand image as an absolute URL; <c>null</c> when the public URL is not configured (an image needs one).</summary>
    private string? BrandImageUrl() => links.TryPublicPage(SeoPagePaths.BrandImage);

    /// <summary>The pages of the public site, as the header of the site shows them (the guides included).</summary>
    private IReadOnlyList<SeoLink> SiteLinks()
    {
        string Url(string path) => links.TryPublicPage(path) ?? path;
        return
        [
            new SeoLink(T("SeoNavFeatures"), Url(SeoPagePaths.SiteFeatures)),
            new SeoLink(T("SeoNavSuppliers"), Url(SeoPagePaths.SiteSuppliers)),
            new SeoLink(T("SeoNavAbout"), Url(SeoPagePaths.SiteAbout)),
            new SeoLink(T("SeoNavGuides"), Url(SeoPagePaths.Hub)),
            new SeoLink(T("SeoNavSearch"), Url(SeoPagePaths.SiteSearch)),
        ];
    }

    /// <summary>The hub's lists, one block per region (a long flat list says nothing about where the pages are).</summary>
    private static void AppendHubSection(
        StringBuilder body,
        string heading,
        IEnumerable<(SeoPublishedPageDto Page, string Url)> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
            return;

        body.Append("<section>\n<h2>").Append(SeoHtml.Encode(heading)).Append("</h2>\n");
        foreach (var region in list.GroupBy(e => e.Page.RegionSlug).OrderBy(g => RegionName(g.Key) ?? g.Key, ItalianCollator))
        {
            body.Append("<h3>").Append(SeoHtml.Encode(RegionName(region.Key) ?? region.Key)).Append("</h3>\n<ul>\n");
            foreach (var (page, url) in region.OrderBy(e => e.Page.ComuneName, ItalianCollator))
            {
                body.Append("<li><a href=\"").Append(SeoHtml.Encode(url)).Append("\">").Append(SeoHtml.Encode(page.Title))
                    .Append("</a></li>\n");
            }

            body.Append("</ul>\n");
        }

        body.Append("</section>\n");
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
        Currency = p.Currency,
        Amenities = p.Amenities,
        PhotoUrls = p.PhotoUrls,
        CinCode = p.CinCode,
        HouseRules = p.HouseRules,
        CancellationPolicySummary = p.CancellationPolicy != null ? p.CancellationPolicy.Description : string.Empty,
        AreaSqm = p.AreaSqm,
        CheckInFrom = p.CheckInFrom,
        CheckOutBy = p.CheckOutBy,
        PetsPolicy = p.PetsPolicy,
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

    /// <summary>An amount of a property for a crawler page: <c>€120</c> for the euro, <c>CHF 120</c> for any other code (CHF-MONEY).</summary>
    private static string Money(decimal amount, string? currency, CultureInfo culture)
    {
        var number = amount % 1m == 0m ? amount.ToString("0", culture) : amount.ToString("0.00", culture);
        var code = StayCurrency.Normalize(currency);
        return code == StayCurrency.Euro ? "€" + number : code + " " + number;
    }

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
        public string Currency { get; init; } = StayCurrency.Default;
        public List<PropertyAmenity> Amenities { get; init; } = [];
        public List<string> PhotoUrls { get; init; } = [];
        public string? CinCode { get; init; }
        public string HouseRules { get; init; } = string.Empty;
        public string CancellationPolicySummary { get; init; } = string.Empty;

        // What the host said (SR-07a), for the structured data of the page (DB-11a).
        public int? AreaSqm { get; init; }
        public TimeOnly? CheckInFrom { get; init; }
        public TimeOnly? CheckOutBy { get; init; }
        public PetsPolicy? PetsPolicy { get; init; }
    }
}
