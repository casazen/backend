using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>Where an org's site lives and its canonical URLs (BK-16): the own host only when it really serves the site.</summary>
public class PublicOrgSiteUrlsTests
{
    private const string PublicSite = "https://public.example.test";
    private readonly Mock<IEntitlementService> _entitlement = new();

    public PublicOrgSiteUrlsTests()
    {
        _entitlement.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns((OrgEntity o) => o.PlanTier);
    }

    private PublicOrgSiteUrls Urls(string? baseDomain = "sites.example.test", string? publicSite = PublicSite) => new(
        new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = publicSite })),
        Options.Create(new PublicHostOptions { BaseDomain = baseDomain }),
        _entitlement.Object);

    private static OrgEntity Org(
        PublicHostMode mode = PublicHostMode.CasazenPath,
        string? customDomain = null,
        DomainVerificationStatus status = DomainVerificationStatus.Pending,
        string? subdomain = null,
        PlanTier tier = PlanTier.Starter) => new()
        {
            Slug = "villa-rossi",
            PublicHostMode = mode,
            CustomDomain = customDomain,
            DomainVerificationStatus = status,
            Subdomain = subdomain,
            PlanTier = tier,
        };

    [Fact]
    public void OwnHost_PathMode_IsNullAndTheCanonicalIsThePlatformPath()
    {
        var urls = Urls();
        var org = Org();

        Assert.Null(urls.OwnHost(org));
        Assert.Equal($"{PublicSite}/book/villa-rossi", urls.TryLandingUrl(org));
        Assert.Equal($"{PublicSite}/book/villa-rossi/property/casa-mare", urls.TryPropertyUrl(org, "casa-mare"));
    }

    [Theory]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Scale)]
    public void OwnHost_VerifiedCustomDomainOnAPaidPlan_IsTheDomainWithCleanPaths(PlanTier tier)
    {
        var urls = Urls();
        var org = Org(PublicHostMode.CustomDomain, "WWW.Villa-Rossi.example.test", DomainVerificationStatus.Verified, tier: tier);

        Assert.Equal("www.villa-rossi.example.test", urls.OwnHost(org));
        Assert.Equal("https://www.villa-rossi.example.test/", urls.TryLandingUrl(org));
        Assert.Equal("https://www.villa-rossi.example.test/property/casa-mare", urls.TryPropertyUrl(org, "casa-mare"));
        Assert.Equal("https://www.villa-rossi.example.test/sitemap.xml", urls.TryOwnHostUrl(org, PublicSitePaths.HostSitemap));
    }

    [Theory]
    [InlineData(DomainVerificationStatus.Pending, PlanTier.Pro)]
    [InlineData(DomainVerificationStatus.Failed, PlanTier.Pro)]
    [InlineData(DomainVerificationStatus.Verified, PlanTier.Starter)]
    public void OwnHost_CustomDomainNotVerifiedOrNotPaidFor_IsNullSoTheCanonicalStaysOnThePlatform(
        DomainVerificationStatus status, PlanTier tier)
    {
        var urls = Urls();
        var org = Org(PublicHostMode.CustomDomain, "www.villa-rossi.example.test", status, tier: tier);

        Assert.Null(urls.OwnHost(org));
        Assert.Equal($"{PublicSite}/book/villa-rossi", urls.TryLandingUrl(org));
        Assert.Null(urls.TryOwnHostUrl(org, "/sitemap.xml"));
    }

    [Fact]
    public void OwnHost_CustomDomainVerifiedButTheOrgIsOnAnotherMode_IsNull()
    {
        var org = Org(PublicHostMode.CasazenPath, "www.villa-rossi.example.test", DomainVerificationStatus.Verified, tier: PlanTier.Pro);

        Assert.Null(Urls().OwnHost(org));
    }

    [Theory]
    [InlineData("rossi", "rossi.sites.example.test")]
    [InlineData(null, "villa-rossi.sites.example.test")]
    public void OwnHost_SubdomainMode_IsTheLabelOnTheConfiguredBaseDomain(string? subdomain, string expected)
    {
        var urls = Urls();
        var org = Org(PublicHostMode.CasazenSubdomain, subdomain: subdomain);

        Assert.Equal(expected, urls.OwnHost(org));
        Assert.Equal($"https://{expected}/property/casa", urls.TryPropertyUrl(org, "casa"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OwnHost_SubdomainModeWithoutABaseDomain_IsNullBecauseThereIsNoDefaultDomain(string? baseDomain)
    {
        var org = Org(PublicHostMode.CasazenSubdomain, subdomain: "rossi");

        Assert.Null(Urls(baseDomain).OwnHost(org));
    }

    [Fact]
    public void TryLandingUrl_NoOwnHostAndPublicSiteNotConfigured_IsNull()
    {
        Assert.Null(Urls(publicSite: null).TryLandingUrl(Org()));
    }

    [Fact]
    public void TryPropertyUrl_PropertySegmentWithSpecialCharacters_IsEscaped()
    {
        var urls = Urls();
        var org = Org(PublicHostMode.CustomDomain, "www.villa-rossi.example.test", DomainVerificationStatus.Verified, tier: PlanTier.Pro);

        Assert.Equal("https://www.villa-rossi.example.test/property/a%2Fb", urls.TryPropertyUrl(org, "a/b"));
    }
}
