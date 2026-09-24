using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Decision D3 residues closed by SE-03: the booking-site link of the domain settings and of the activation status and
/// the supplier check-in QR are built on <c>App:PublicSiteBaseUrl</c> (no <c>casazen.app</c> fallback), the org
/// subdomains only on <c>PublicHost:BaseDomain</c> (no <c>casazen.it</c> default).
/// </summary>
public class PublicUrlConfigurationTests
{
    private const string PublicSite = "https://public-site.example.test";

    [Fact]
    public async Task GetDomainConfigAsync_ConfiguredDomains_BuildsUrlsOnThem()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, subdomain: "villa-mare");

        var config = await CreateOrgDomainService(db, PublicSite, "sites.example.test").GetDomainConfigAsync(org.Id);

        Assert.Equal($"{PublicSite}/book/villa-mare", config!.PublicUrls.PathUrl);
        Assert.Equal("https://villa-mare.sites.example.test", config.PublicUrls.SubdomainUrl);
    }

    [Fact]
    public async Task GetDomainConfigAsync_NothingConfigured_UsesNoFallbackDomain()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, subdomain: "villa-mare");

        var config = await CreateOrgDomainService(db, publicSite: null, baseDomain: null).GetDomainConfigAsync(org.Id);

        Assert.Equal("/book/villa-mare", config!.PublicUrls.PathUrl);
        Assert.Null(config.PublicUrls.SubdomainUrl);
    }

    [Fact]
    public async Task SetDomainAsync_SubdomainWithoutBaseDomain_IsRefusedAndNothingChanges()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, subdomain: null);

        var result = await CreateOrgDomainService(db, PublicSite, baseDomain: " ")
            .SetDomainAsync(org.Id, PublicHostMode.CasazenSubdomain, customDomain: null, subdomain: "villa-mare");

        Assert.Equal(SetOrgDomainOutcome.SubdomainsNotConfigured, result.Outcome);
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Equal(PublicHostMode.CasazenPath, stored.PublicHostMode);
        Assert.Null(stored.Subdomain);
    }

    [Fact]
    public void BuildCheckInUrl_ConfiguredPublicSite_IsOnThatDomain()
    {
        var jobId = Guid.NewGuid();

        var url = new QrCodeService(EmailTestHelpers.Links(PublicSite + "/")).BuildCheckInUrl(jobId, "ABC", "Via Roma 1");

        Assert.Equal($"{PublicSite}/check-in/{jobId}?token=ABC&loc=Via%20Roma%201", url);
    }

    [Fact]
    public void BuildCheckInUrl_PublicSiteMissing_IsRelativeNotOnAFallbackDomain()
    {
        var jobId = Guid.NewGuid();

        var url = new QrCodeService(EmailTestHelpers.Links(null)).BuildCheckInUrl(jobId, "ABC", null);

        Assert.Equal($"/check-in/{jobId}?token=ABC&loc=Property", url);
    }

    private static OrgDomainService CreateOrgDomainService(AppDbContext db, string? publicSite, string? baseDomain)
    {
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.CanUseCustomDomainAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        return new OrgDomainService(
            db,
            entitlements.Object,
            Mock.Of<IDomainVerificationService>(),
            Mock.Of<IPublicHostResolver>(),
            Options.Create(new PublicHostOptions { BaseDomain = baseDomain }),
            EmailTestHelpers.Links(publicSite));
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Casazen.Core.Entities.Org> SeedOrgAsync(AppDbContext db, string? subdomain)
    {
        var org = new Casazen.Core.Entities.Org
        {
            Name = "Villa Mare",
            DisplayName = "Villa Mare",
            Slug = "villa-mare",
            ContactEmail = "info@villa-mare.test",
            Subdomain = subdomain,
            PublicHostMode = subdomain is null ? PublicHostMode.CasazenPath : PublicHostMode.CasazenSubdomain,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }
}
