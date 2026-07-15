using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PublicHostResolverTests
{
    private readonly Mock<IOrgService> _orgService = new();
    private readonly PublicHostResolver _resolver;

    public PublicHostResolverTests()
    {
        var options = Options.Create(new PublicHostOptions
        {
            BaseDomain = "casazen.it",
            ReservedSubdomains = ["www", "api", "app", "admin"],
        });
        _resolver = new PublicHostResolver(_orgService.Object, options, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task ResolveAsync_VerifiedCustomDomain_ReturnsOrg()
    {
        var org = BuildOrg("villa-mare", PublicHostMode.CustomDomain);
        org.CustomDomain = "www.villa-mare.it";
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("www.villa-mare.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var result = await _resolver.ResolveAsync("www.villa-mare.it", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PublicHostMode.CustomDomain, result!.PublicHostMode);
        Assert.Equal("villa-mare", result.Slug);
    }

    [Fact]
    public async Task ResolveAsync_CasazenSubdomain_ReturnsOrgBranding()
    {
        var org = BuildOrg("villa-mare", PublicHostMode.CasazenSubdomain);
        org.Subdomain = "villa-mare";
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("villa-mare.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);
        _orgService.Setup(s => s.GetBySubdomainOrSlugAsync("villa-mare", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var result = await _resolver.ResolveAsync("villa-mare.casazen.it", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PublicHostMode.CasazenSubdomain, result!.PublicHostMode);
        Assert.Equal("villa-mare", result.Slug);
        Assert.Equal("Villa Mare", result.Branding.DisplayName);
    }

    [Fact]
    public async Task ResolveAsync_UnverifiedCustomDomain_ReturnsNull()
    {
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("pending.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);
        _orgService.Setup(s => s.GetBySubdomainOrSlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);

        var result = await _resolver.ResolveAsync("pending.example.it", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("api.casazen.it")]
    [InlineData("www.casazen.it")]
    [InlineData("casazen.it")]
    [InlineData("unknown.example.com")]
    public async Task ResolveAsync_ReservedOrUnknownHost_ReturnsNull(string host)
    {
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync(host, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);

        var result = await _resolver.ResolveAsync(host, CancellationToken.None);
        Assert.Null(result);
        _orgService.Verify(
            s => s.GetBySubdomainOrSlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static OrgEntity BuildOrg(string slug, PublicHostMode mode) => new()
    {
        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"),
        Slug = slug,
        DisplayName = "Villa Mare",
        ThemeColor = "#2563eb",
        ContactEmail = "host@villamare.it",
        IsActive = true,
        PublicHostMode = mode,
        PlanTier = PlanTier.Pro,
    };
}
