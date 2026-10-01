using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-15 (A3-20, A8-09, A8-29): what a crawler receives for the booking sites. The tests read the HTML the way a search
/// engine does (head tags, JSON-LD) and check what must never be indexed: unpublished data, unknown or unverified hosts.
/// </summary>
public class PublicSeoIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Cin = "IT058091C27G5FFZDZ";
    private readonly CasazenWebApplicationFactory _factory;

    public PublicSeoIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private string PublicSite => _factory.Services.GetRequiredService<IOptions<PublicSiteOptions>>().Value.PublicSiteBaseUrl!.TrimEnd('/');

    // ── Org landing page ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrg_WithPublishedProperties_ServesIndexableHtmlWithTheHeadOfSpecAc13()
    {
        var org = await SeedOrgAsync(tagline: "Il tuo rifugio sul lago");
        var property = await SeedPropertyAsync(org, "Villa Lago", city: "Como");

        var response = await GetAsync($"/api/public/seo/orgs/{org.Slug}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("<html lang=\"it\">", html);
        Assert.Contains("<title>Rossi Ospitalità — Prenota direttamente</title>", html);
        Assert.Contains("<meta name=\"description\" content=\"Il tuo rifugio sul lago\">", html);
        Assert.Contains("content=\"index,follow", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"{PublicSite}/book/{org.Slug}\">", html);
        Assert.Contains($"<link rel=\"alternate\" hreflang=\"x-default\" href=\"{PublicSite}/book/{org.Slug}\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{PublicSite}/book/{org.Slug}\">", html);
        Assert.Contains("<meta property=\"og:image\" content=\"https://cdn.example.test/logo.png\">", html);
        Assert.Contains("<meta property=\"og:site_name\" content=\"Rossi Ospitalità\">", html);
        Assert.Contains("<h1>Rossi Ospitalità</h1>", html);
        // The property is a link on the public domain, with its real facts.
        Assert.Contains($"<a href=\"{PublicSite}/book/{org.Slug}/property/{property.Slug}\">Villa Lago</a>", html);
        Assert.Contains("Ospiti: 4", html);
        Assert.Contains("Da €150 a notte", html);
    }

    [Fact]
    public async Task GetOrg_JsonLd_IsAnOrganizationWithItsPropertiesAsAnItemList()
    {
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "Villa Lago");
        await SeedPropertyAsync(org, "Casa Monte", city: "Sondrio");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}");
        var blocks = JsonLdBlocks(html);

        var organization = blocks.Single(b => b.GetProperty("@type").GetString() == "Organization");
        Assert.Equal("Rossi Ospitalità", organization.GetProperty("name").GetString());
        Assert.Equal($"{PublicSite}/book/{org.Slug}", organization.GetProperty("url").GetString());
        Assert.Equal("https://cdn.example.test/logo.png", organization.GetProperty("logo").GetString());
        var list = blocks.Single(b => b.GetProperty("@type").GetString() == "ItemList");
        var items = list.GetProperty("itemListElement").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.StartsWith($"{PublicSite}/book/{org.Slug}/property/", item.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task GetOrg_WithoutTagline_DescribesTheRealPropertiesAndCities()
    {
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "Villa Lago", city: "Como");
        await SeedPropertyAsync(org, "Casa Monte", city: "Sondrio");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}");

        Assert.Contains("content=\"Prenota direttamente da Rossi Ospitalità: 2 alloggi (Como, Sondrio).\"", html);
    }

    [Fact]
    public async Task GetOrg_OnlyUnpublishedProperties_IsNoindexAndListsNothing()
    {
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "In pausa", paused: true);
        await SeedPropertyAsync(org, "Da attivare", compliance: PropertyComplianceStatus.Pending);
        await SeedPropertyAsync(org, "Sospesa", compliance: PropertyComplianceStatus.Suspended);

        var response = await GetAsync($"/api/public/seo/orgs/{org.Slug}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("content=\"noindex,nofollow\"", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("In pausa", html);
        Assert.DoesNotContain("Da attivare", html);
        Assert.DoesNotContain("Sospesa", html);
        Assert.DoesNotContain("\"ItemList\"", html);
    }

    [Fact]
    public async Task GetOrg_UnknownOrInactiveOrg_IsARealNotFoundThatIsNotIndexed()
    {
        var inactive = await SeedOrgAsync(isActive: false);
        await SeedPropertyAsync(inactive, "Villa");

        foreach (var slug in new[] { $"does-not-exist-{Guid.NewGuid():N}", inactive.Slug })
        {
            var response = await GetAsync($"/api/public/seo/orgs/{slug}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("content=\"noindex,nofollow\"", html);
            Assert.Contains("<title>Pagina non trovata</title>", html);
            Assert.DoesNotContain("Villa", html);
        }
    }

    [Fact]
    public async Task GetOrg_OldSlugOfTheOrg_IsAPermanentRedirectToTheCurrentPathOnTheSameHost()
    {
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "Villa Lago");
        var oldSlug = $"old-{Guid.NewGuid():N}";
        await SeedAliasAsync(org, oldSlug);

        var response = await GetAsync($"/api/public/seo/orgs/{oldSlug}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/book/{org.Slug}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GetOrg_InvalidSlug_IsABadRequestNotAPage()
    {
        var response = await GetAsync("/api/public/seo/orgs/a_b%20c");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetOrg_HostileHostAndTexts_NeverReachTheMarkup()
    {
        var org = await SeedOrgAsync(displayName: "</title><script>alert('n')</script>", tagline: "\"><img src=x onerror=alert(1)>");
        await SeedPropertyAsync(org, "<b>Villa</b>", description: "</script><script>alert('d')</script>");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}?host=evil.example\"><script>alert(1)</script>");

        // The page is noindex for such a host, and the host is not written anywhere.
        Assert.Contains("content=\"noindex,nofollow\"", html);
        Assert.DoesNotContain("evil.example", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>Villa</b>", html);
        foreach (var block in JsonLdBlocks(html))
            Assert.Equal("https://schema.org", block.GetProperty("@context").GetString());
    }

    // ── Hosts: the verified ones are indexed, the others are not ────────────────────────

    [Fact]
    public async Task GetOrg_OnThePublicSiteHostOrWithoutHost_IsIndexable()
    {
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "Villa Lago");
        var publicHost = new Uri(PublicSite).Host;

        foreach (var query in new[] { string.Empty, $"?host={publicHost}", $"?host={publicHost.ToUpperInvariant()}:443" })
        {
            var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}{query}");

            Assert.Contains("content=\"index,follow", html);
        }
    }

    [Fact]
    public async Task GetOrg_OnAVerifiedCustomDomainOfTheSameOrg_IsIndexable()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        await SeedPropertyAsync(org, "Villa Lago");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}?host={domain}");

        Assert.Contains("content=\"index,follow", html);
    }

    [Fact]
    public async Task GetOrg_OnACustomDomainStillWaitingForVerification_IsNoindex()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: false);
        await SeedPropertyAsync(org, "Villa Lago");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}?host={domain}");

        Assert.Contains("content=\"noindex,nofollow\"", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
    }

    [Fact]
    public async Task GetOrg_OnAVerifiedCustomDomainWithoutAPaidProPlan_IsNoindex()
    {
        // The domain is verified, but a Pro tier nobody pays for is Starter: the host no longer resolves (A3-07).
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, paid: false, customDomain: domain, verified: true);
        await SeedPropertyAsync(org, "Villa Lago");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}?host={domain}");

        Assert.Contains("content=\"noindex,nofollow\"", html);
    }

    [Fact]
    public async Task GetOrg_OnTheVerifiedDomainOfAnotherOrgOrAnUnknownHost_IsNoindex()
    {
        var other = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: other, verified: true);
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "Villa Lago");

        foreach (var host in new[] { other, "unknown-host.example.test", "casazen-app-git-branch.vercel.app" })
        {
            var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}?host={host}");

            Assert.Contains("content=\"noindex,nofollow\"", html);
        }
    }

    // ── Property page ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProperty_Published_ServesTheHeadOfSpecAc13AndAVacationRentalOfRealData()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(
            org, "Villa Lago", city: "Como", description: "Una villa con vista sul lago, a due passi dal centro.",
            amenities: [PropertyAmenity.WiFi, PropertyAmenity.Pool, PropertyAmenity.PetFriendly]);

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");
        var canonical = $"{PublicSite}/book/{org.Slug}/property/{property.Slug}";

        Assert.Contains("<title>Villa Lago · Como — Rossi Ospitalità</title>", html);
        Assert.Contains("<meta name=\"description\" content=\"Una villa con vista sul lago, a due passi dal centro.\">", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"{canonical}\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{canonical}\">", html);
        Assert.Contains("<meta property=\"og:image\" content=\"https://cdn.example.test/villa-1.jpg\">", html);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary_large_image\">", html);
        Assert.Contains("content=\"index,follow", html);
        Assert.Contains("<h1>Villa Lago</h1>", html);
        Assert.Contains("Wi-Fi", html);
        Assert.Contains("Piscina", html);
        Assert.Contains($"Codice CIN: {Cin}", html);

        var blocks = JsonLdBlocks(html);
        var rental = blocks.Single(b => b.GetProperty("@type").GetString() == "VacationRental");
        Assert.Equal("Villa Lago", rental.GetProperty("name").GetString());
        Assert.Equal(canonical, rental.GetProperty("url").GetString());
        Assert.Equal(Cin, rental.GetProperty("identifier").GetString());
        Assert.Equal("Como", rental.GetProperty("address").GetProperty("addressLocality").GetString());
        Assert.Equal("IT", rental.GetProperty("address").GetProperty("addressCountry").GetString());
        Assert.Equal(45.81m, rental.GetProperty("geo").GetProperty("latitude").GetDecimal());
        Assert.Equal(2, rental.GetProperty("image").GetArrayLength());
        var place = rental.GetProperty("containsPlace");
        Assert.Equal(4, place.GetProperty("occupancy").GetProperty("value").GetInt32());
        Assert.Equal(2, place.GetProperty("numberOfBedrooms").GetInt32());
        Assert.Equal(
            new[] { "Wi-Fi", "Piscina", "Animali ammessi" },
            place.GetProperty("amenityFeature").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.True(rental.GetProperty("petsAllowed").GetBoolean());
        Assert.Equal(150m, rental.GetProperty("makesOffer")[0].GetProperty("priceSpecification").GetProperty("price").GetDecimal());
        // Nothing the database does not hold is invented.
        foreach (var key in new[] { "aggregateRating", "review", "checkinTime", "checkoutTime" })
            Assert.False(rental.TryGetProperty(key, out _), key);

        var breadcrumb = blocks.Single(b => b.GetProperty("@type").GetString() == "BreadcrumbList");
        Assert.Equal(2, breadcrumb.GetProperty("itemListElement").GetArrayLength());
    }

    [Fact]
    public async Task GetProperty_WithoutValidCin_DoesNotShowOrPublishTheCinAndIdentifiesByPropertyId()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(org, "Villa Lago", cinCode: "IT-12345-1234567890");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");

        // The old, wrong CIN format is never shown to guests (compliance.md) and carries no country.
        Assert.DoesNotContain("IT-12345", html);
        Assert.DoesNotContain("Codice CIN", html);
        var rental = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "VacationRental");
        Assert.Equal(property.Id.ToString(), rental.GetProperty("identifier").GetString());
        Assert.False(rental.GetProperty("address").TryGetProperty("addressCountry", out _));
    }

    [Fact]
    public async Task GetProperty_WithoutDescription_UsesAFactualDescription()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(org, "Villa Lago", city: "Como", description: "");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");

        Assert.Contains("content=\"Villa Lago a Como: 4 ospiti, 2 camere da letto, 1 bagni. Prenota direttamente dall&#x27;host.\"", html);
    }

    [Fact]
    public async Task GetProperty_LongDescription_IsCutToTheFirst155CharactersOfTheSnippet()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(org, "Villa Lago", description: string.Join(' ', Enumerable.Repeat("panorama", 80)));

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");

        var description = Regex.Match(html, "<meta name=\"description\" content=\"([^\"]*)\">").Groups[1].Value;
        Assert.InRange(description.Length, 100, 160);
        Assert.EndsWith("…", description);
    }

    [Fact]
    public async Task GetProperty_NotPublished_IsARealNotFound()
    {
        var org = await SeedOrgAsync();
        var paused = await SeedPropertyAsync(org, "In pausa", paused: true);
        var pending = await SeedPropertyAsync(org, "Da attivare", compliance: PropertyComplianceStatus.Pending);
        var inactive = await SeedPropertyAsync(org, "Disattivata", active: false);

        foreach (var property in new[] { paused, pending, inactive })
        {
            var response = await GetAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("content=\"noindex,nofollow\"", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task GetProperty_OfAnotherOrg_IsNotFoundForThisOne()
    {
        var org = await SeedOrgAsync();
        var other = await SeedOrgAsync();
        var foreign = await SeedPropertyAsync(other, "Villa Altrui");

        var byId = await GetAsync($"/api/public/seo/orgs/{org.Slug}/properties/{foreign.Id}");
        var bySlug = await GetAsync($"/api/public/seo/orgs/{org.Slug}/properties/{foreign.Slug}");

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bySlug.StatusCode);
    }

    [Fact]
    public async Task GetProperty_ByIdWhenItHasASlug_IsAPermanentRedirectToTheSlugPath()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(org, "Villa Lago");

        var response = await GetAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/book/{org.Slug}/property/{property.Slug}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GetProperty_OldOrgSlug_IsAPermanentRedirectToTheCurrentOrgSlug()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(org, "Villa Lago");
        var oldSlug = $"old-{Guid.NewGuid():N}";
        await SeedAliasAsync(org, oldSlug);

        var response = await GetAsync($"/api/public/seo/orgs/{oldSlug}/properties/{property.Slug}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/book/{org.Slug}/property/{property.Slug}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GetProperty_HostileTexts_NeverReachTheMarkupOrBreakTheJsonLd()
    {
        var org = await SeedOrgAsync();
        var property = await SeedPropertyAsync(
            org, "Villa \"Lago\" <i>", city: "</script>", description: "Tutto \"incluso\" </script><script>alert(1)</script>",
            houseRules: "<img src=x onerror=alert(1)>");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");

        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Equal(2, Regex.Matches(html, "<script type=\"application/ld\\+json\">").Count);
        Assert.Equal(2, Regex.Matches(html, "</script>").Count);
        var rental = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "VacationRental");
        Assert.Equal("Villa \"Lago\" <i>", rental.GetProperty("name").GetString());
    }

    // ── Org's own host: subdomain and custom domain (BK-16) ──────────────────────────────

    [Fact]
    public async Task GetHostPage_VerifiedCustomDomain_RendersTheLandingPageWithTheCanonicalOnThatDomain()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        var property = await SeedPropertyAsync(org, "Villa Lago");

        var response = await GetAsync($"/api/public/seo/hosts/page?host={domain}&path=/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://{domain}/\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"https://{domain}/\">", html);
        Assert.Contains($"<a href=\"https://{domain}/property/{property.Slug}\">Villa Lago</a>", html);
        Assert.Contains("content=\"index,follow", html);
        Assert.DoesNotContain("/book/", html);
        var organization = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "Organization");
        Assert.Equal($"https://{domain}/", organization.GetProperty("url").GetString());
    }

    [Fact]
    public async Task GetHostPage_VerifiedCustomDomain_RendersAPropertyAtItsCleanPath()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        var property = await SeedPropertyAsync(org, "Villa Lago", city: "Como");

        var response = await GetAsync($"/api/public/seo/hosts/page?host={domain}&path=/property/{property.Slug}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://{domain}/property/{property.Slug}\">", html);
        Assert.Contains("<title>Villa Lago · Como — Rossi Ospitalità</title>", html);
        var rental = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "VacationRental");
        Assert.Equal($"https://{domain}/property/{property.Slug}", rental.GetProperty("url").GetString());
        var breadcrumb = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "BreadcrumbList");
        Assert.Equal($"https://{domain}/", breadcrumb.GetProperty("itemListElement")[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task GetHostPage_PropertyReachedByIdOnAnOwnHost_IsARedirectToTheSlugPathOfThatHost()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        var property = await SeedPropertyAsync(org, "Villa Lago");

        var response = await GetAsync($"/api/public/seo/hosts/page?host={domain}&path=/property/{property.Id}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/property/{property.Slug}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GetHostPage_SubdomainOfAnOrgOnTheSubdomainMode_IsServed()
    {
        var label = $"v{Guid.NewGuid():N}"[..20];
        var org = await SeedOrgAsync(subdomain: label);
        await SeedPropertyAsync(org, "Villa Lago");

        var html = await GetHtmlAsync($"/api/public/seo/hosts/page?host={label}.casazen.it&path=/");

        Assert.Contains($"<link rel=\"canonical\" href=\"https://{label}.casazen.it/\">", html);
    }

    [Theory]
    [InlineData("unknown-host.example.test")]
    [InlineData("casazen-app.vercel.app")]
    [InlineData("nobody.casazen.it")]
    [InlineData("www.casazen.it")]
    [InlineData("evil.example\"><script>")]
    public async Task GetHostPage_HostThatServesNoOrgSite_Is404MarkedAsAnUnknownHost(string host)
    {
        var response = await GetAsync($"/api/public/seo/hosts/page?host={Uri.EscapeDataString(host)}&path=/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown", Assert.Single(response.Headers.GetValues("X-Seo-Host")));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("content=\"noindex,nofollow\"", html);
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public async Task GetHostPage_CustomDomainWaitingForItsVerification_IsAnUnknownHost()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: false);
        await SeedPropertyAsync(org, "Villa Lago");

        var response = await GetAsync($"/api/public/seo/hosts/page?host={domain}&path=/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown", Assert.Single(response.Headers.GetValues("X-Seo-Host")));
        Assert.DoesNotContain("Villa Lago", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/book/anything")]
    [InlineData("/property/")]
    [InlineData("/other")]
    [InlineData("/property/a/b")]
    public async Task GetHostPage_PathWithoutACrawlerPage_IsBadRequestOrNotFound(string path)
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);

        var response = await GetAsync($"/api/public/seo/hosts/page?host={domain}&path={Uri.EscapeDataString(path)}");

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, response.StatusCode.ToString());
    }

    [Fact]
    public async Task GetOrg_OnThePlatformPathOfAnOrgWithItsOwnHost_PointsTheCanonicalAtTheOwnHost()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        await SeedPropertyAsync(org, "Villa Lago");

        var html = await GetHtmlAsync($"/api/public/seo/orgs/{org.Slug}");

        Assert.Contains($"<link rel=\"canonical\" href=\"https://{domain}/\">", html);
    }

    [Fact]
    public async Task OrgSitemap_OrgWithItsOwnHost_IsNotUnderThePlatformPathAndIsLeftOutOfTheIndex()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        await SeedPropertyAsync(org, "Villa Lago");

        var platformSitemap = await GetAsync($"/api/public/orgs/{org.Slug}/sitemap.xml");
        var index = await (await GetAsync("/api/public/sitemap-book.xml")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, platformSitemap.StatusCode);
        Assert.DoesNotContain(org.Slug, index);
    }

    [Fact]
    public async Task Sitemap_WithTheHostOfAnOrg_ListsItsPagesOnThatHost()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);
        var property = await SeedPropertyAsync(org, "Villa Lago");
        await SeedPropertyAsync(org, "In pausa", paused: true);

        var response = await GetAsync($"/api/public/sitemap.xml?host={domain}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new[] { $"https://{domain}/", $"https://{domain}/property/{property.Slug}" },
            Locations(await response.Content.ReadAsStringAsync(), "url"));
    }

    [Fact]
    public async Task Sitemap_WithTheHostOfAnOrgWithNothingPublished_Is404NotTheGuidesOfThePlatform()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(planTier: PlanTier.Pro, customDomain: domain, verified: true);

        var response = await GetAsync($"/api/public/sitemap.xml?host={domain}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?host=")]
    [InlineData("?host=casazen-app.vercel.app")]
    [InlineData("?host=unknown-host.example.test")]
    [InlineData("?host=not%20a%20host")]
    public async Task Sitemap_WithoutAnOrgHost_IsStillTheGuidesSitemap(string query)
    {
        var response = await GetAsync($"/api/public/sitemap.xml{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(Locations(await response.Content.ReadAsStringAsync(), "url"), loc => Assert.StartsWith(PublicSite + "/p/", loc));
    }

    // ── Sitemaps ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrgSitemap_ListsTheLandingPageAndThePublishedPropertiesOnThePublicSite()
    {
        var org = await SeedOrgAsync();
        var published = await SeedPropertyAsync(org, "Villa Lago");
        var paused = await SeedPropertyAsync(org, "In pausa", paused: true);

        var response = await GetAsync($"/api/public/orgs/{org.Slug}/sitemap.xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var locations = Locations(await response.Content.ReadAsStringAsync(), "url");
        Assert.Equal(
            new[] { $"{PublicSite}/book/{org.Slug}", $"{PublicSite}/book/{org.Slug}/property/{published.Slug}" },
            locations);
        Assert.DoesNotContain(locations, loc => loc.Contains(paused.Slug!));
    }

    [Fact]
    public async Task OrgSitemap_OrgWithNothingPublishedUnknownOrOldSlug_IsNotFound()
    {
        var empty = await SeedOrgAsync();
        await SeedPropertyAsync(empty, "In pausa", paused: true);
        var renamed = await SeedOrgAsync();
        await SeedPropertyAsync(renamed, "Villa Lago");
        var oldSlug = $"old-{Guid.NewGuid():N}";
        await SeedAliasAsync(renamed, oldSlug);

        foreach (var slug in new[] { empty.Slug, $"unknown-{Guid.NewGuid():N}", oldSlug })
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/public/orgs/{slug}/sitemap.xml")).StatusCode);
    }

    [Fact]
    public async Task SitemapIndex_ListsOnlyTheOrgsThatHavePublishedProperties()
    {
        var published = await SeedOrgAsync();
        await SeedPropertyAsync(published, "Villa Lago");
        var unpublished = await SeedOrgAsync();
        await SeedPropertyAsync(unpublished, "In pausa", paused: true);
        var inactive = await SeedOrgAsync(isActive: false);
        await SeedPropertyAsync(inactive, "Villa");

        var response = await GetAsync("/api/public/sitemap-book.xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var locations = Locations(await response.Content.ReadAsStringAsync(), "sitemap");
        Assert.Contains($"{PublicSite}/book/{published.Slug}/sitemap.xml", locations);
        Assert.DoesNotContain($"{PublicSite}/book/{unpublished.Slug}/sitemap.xml", locations);
        Assert.DoesNotContain($"{PublicSite}/book/{inactive.Slug}/sitemap.xml", locations);
        Assert.All(locations, loc => Assert.StartsWith(PublicSite + "/book/", loc));
    }

    // ── Guides and hub ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGuide_ApprovedRevision_IsServedAsAnIndexableArticle()
    {
        // Own comune: a page is seeded once per comune and shared by the tests of this class.
        await SeedGuideAsync("013250", "bellagio", "<h2>CIN</h2><p>Il CIN è obbligatorio.</p><script>alert(1)</script>");

        var response = await GetAsync("/api/public/seo/guides/lombardia/bellagio");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<meta property=\"og:type\" content=\"article\">", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"{PublicSite}/p/affitti-brevi/lombardia/bellagio\">", html);
        Assert.Contains("content=\"index,follow", html);
        Assert.Contains("<h2>CIN</h2>", html);
        Assert.Contains("Il CIN è obbligatorio.", html);
        Assert.DoesNotContain("alert(1)", html);
        Assert.Contains("non consulenza legale", html);
        Assert.Contains("Contenuto generato con AI", html);
        var article = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "Article");
        Assert.Equal("CasaZen", article.GetProperty("publisher").GetProperty("name").GetString());
        var breadcrumb = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "BreadcrumbList");
        Assert.Equal($"{PublicSite}/p/affitti-brevi", breadcrumb.GetProperty("itemListElement")[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task GetGuide_UnknownPage_IsARealNotFound()
    {
        var response = await GetAsync("/api/public/seo/guides/lombardia/unknown-comune");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTouristTaxPage_WithoutARateInForce_IsNoindexLikeTheSitemap()
    {
        // Palermo: no rate is seeded for it (Como has one), so no rate is in force.
        await SeedGuideAsync("082053", "palermo", "<p>Calcolatore</p>", SeoPageType.TouristTaxCalc, slugPrefix: "tassa-soggiorno", regionCode: "SIC");

        var response = await GetAsync("/api/public/seo/tourist-tax/palermo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("content=\"noindex,nofollow\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetHub_WithAPublishedGuide_ListsItOnThePublicSiteAndIsIndexable()
    {
        await SeedGuideAsync("013075", "como", "<p>Guida</p>");

        var response = await GetAsync("/api/public/seo/hub");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>Guide per gli affitti brevi per comune | CasaZen</title>", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"{PublicSite}/p/affitti-brevi\">", html);
        Assert.Contains($"<a href=\"{PublicSite}/p/affitti-brevi/lombardia/como\">", html);
        Assert.Contains("content=\"index,follow", html);
        var collection = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "CollectionPage");
        Assert.Equal(1, collection.GetProperty("mainEntity").GetProperty("itemListElement").GetArrayLength());
    }

    // ── Language ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrg_EnglishAcceptLanguage_IsServedInEnglishAndSaysSo()
    {
        var org = await SeedOrgAsync();
        await SeedPropertyAsync(org, "Villa Lago");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/seo/orgs/{org.Slug}");
        request.Headers.AcceptLanguage.ParseAdd("en");

        var response = await _factory.CreateClient().SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<title>Rossi Ospitalità — Book direct</title>", html);
        Assert.Contains("Our properties", html);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    // No redirect following: a 301 is the answer under test, not a request to repeat.
    private Task<HttpResponseMessage> GetAsync(string url) =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync(url);

    private async Task<string> GetHtmlAsync(string url)
    {
        var response = await GetAsync(url);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"{url} answered {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static List<JsonElement> JsonLdBlocks(string html) =>
        Regex.Matches(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline)
            .Select(m => JsonDocument.Parse(m.Groups[1].Value).RootElement.Clone())
            .ToList();

    private static List<string> Locations(string xml, string entry)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        return XDocument.Parse(xml).Descendants(ns + entry).Select(e => e.Element(ns + "loc")!.Value).ToList();
    }

    private async Task<OrgEntity> SeedOrgAsync(
        string displayName = "Rossi Ospitalità",
        string? tagline = null,
        bool isActive = true,
        PlanTier planTier = PlanTier.Starter,
        bool paid = true,
        string? customDomain = null,
        bool verified = false,
        string? subdomain = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var withSubscription = planTier != PlanTier.Starter && paid;
        var org = new OrgEntity
        {
            Name = displayName,
            DisplayName = displayName,
            Slug = $"seo-{Guid.NewGuid():N}",
            ContactEmail = "host@example.test",
            LogoUrl = "https://cdn.example.test/logo.png",
            Tagline = tagline,
            PlanTier = planTier,
            SubscriptionId = withSubscription ? $"sub_test_{Guid.NewGuid():N}" : null,
            SubscriptionStatus = withSubscription ? SubscriptionStatus.Active : SubscriptionStatus.None,
            IsActive = isActive,
            PublicHostMode = customDomain is not null
                ? PublicHostMode.CustomDomain
                : subdomain is not null ? PublicHostMode.CasazenSubdomain : PublicHostMode.CasazenPath,
            CustomDomain = customDomain,
            Subdomain = subdomain,
            DomainVerificationStatus = verified ? DomainVerificationStatus.Verified : DomainVerificationStatus.Pending,
            DomainVerificationToken = customDomain is null ? null : "token",
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private async Task SeedAliasAsync(OrgEntity org, string oldSlug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = oldSlug, OrgId = org.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<Property> SeedPropertyAsync(
        OrgEntity org,
        string name,
        string city = "Como",
        string description = "Una bella casa.",
        string houseRules = "",
        string? cinCode = Cin,
        bool paused = false,
        bool active = true,
        PropertyComplianceStatus compliance = PropertyComplianceStatus.Active,
        List<PropertyAmenity>? amenities = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = name,
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = description,
            Address = $"Via Segreta {Guid.NewGuid():N}",
            City = city,
            PostalCode = "22100",
            Latitude = 45.81m,
            Longitude = 9.08m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            CinCode = cinCode,
            HouseRules = houseRules,
            IsActive = active,
            IsPaused = paused,
            ComplianceStatus = compliance,
            Amenities = amenities ?? [],
            PhotoUrls = ["https://cdn.example.test/villa-1.jpg", "https://cdn.example.test/villa-2.jpg", "/uploads/legacy.jpg"],
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task SeedGuideAsync(
        string comuneCode, string comuneSlug, string bodyHtml, SeoPageType pageType = SeoPageType.ComplianceGuide, string slugPrefix = "affitti-brevi/lombardia", string regionCode = "LOM")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slug = $"{slugPrefix}/{comuneSlug}";
        var existing = db.SeoContentPages.FirstOrDefault(p => p.Slug == slug);
        if (existing is not null)
            return;

        var page = new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comuneCode,
            RegionCode = regionCode,
            PageType = pageType,
            Title = "Affitti brevi a Como: la guida",
            MetaDescription = "CIN, Alloggiati Web e tassa di soggiorno a Como.",
            LegalReviewStatus = LegalReviewStatus.Reviewed,
            LastRefreshedAt = DateTime.UtcNow,
        };
        db.SeoContentPages.Add(page);
        await db.SaveChangesAsync();
        var revision = new SeoContentRevision
        {
            PageId = page.Id,
            BodyHtml = bodyHtml,
            AiModelTier = AiModelTier.Economy,
            SourceDataVersion = "bk15",
        };
        db.SeoContentRevisions.Add(revision);
        await db.SaveChangesAsync();
        page.PublishedRevisionId = revision.Id;
        page.PublishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}

/// <summary>The hub with nothing to list is thin content: not indexed, like its absence from the sitemap (BK-15).</summary>
public class PublicSeoEmptyHubIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PublicSeoEmptyHubIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetHub_NoPublishedPage_IsNoindexAndSaysThereIsNothingYet()
    {
        var response = await _factory.CreateClient().GetAsync("/api/public/seo/hub");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("content=\"noindex,nofollow\"", html);
        Assert.Contains("Non ci sono ancora guide pubblicate.", html);
    }
}
