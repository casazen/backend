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
    private readonly Mock<IEntitlementService> _entitlementService = new();
    private readonly PublicHostResolver _resolver;

    public PublicHostResolverTests()
    {
        _entitlementService.Setup(s => s.CanUseCustomDomainAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var options = Options.Create(new PublicHostOptions
        {
            BaseDomain = "casazen.it",
            ReservedSubdomains = ["www", "api", "app", "admin"],
        });
        _resolver = new PublicHostResolver(
            _orgService.Object,
            _entitlementService.Object,
            options,
            new MemoryCache(new MemoryCacheOptions()));
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
    public async Task ResolveAsync_VerifiedCustomDomainWithoutEntitlement_ReturnsNull()
    {
        var org = BuildOrg("villa-mare", PublicHostMode.CustomDomain);
        org.CustomDomain = "www.villa-mare.it";
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("www.villa-mare.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);
        _entitlementService.Setup(s => s.CanUseCustomDomainAsync(org.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _resolver.ResolveAsync("www.villa-mare.it", CancellationToken.None);

        Assert.Null(result);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ResolveAsync_BaseDomainNotConfigured_NoHostIsAnOrgSubdomain(string? baseDomain)
    {
        // D3 (SE-03): PublicHost:BaseDomain has no default; without it there are no org subdomains to resolve.
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("villa-mare.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);
        var resolver = new PublicHostResolver(
            _orgService.Object,
            _entitlementService.Object,
            Options.Create(new PublicHostOptions { BaseDomain = baseDomain }),
            new MemoryCache(new MemoryCacheOptions()));

        var result = await resolver.ResolveAsync("villa-mare.casazen.it", CancellationToken.None);

        Assert.Null(result);
        _orgService.Verify(
            s => s.GetBySubdomainOrSlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

    [Fact]
    public async Task ResolveAsync_UnresolvedHost_IsRememberedUntilItIsInvalidated()
    {
        // BK-16: a host that resolves to nothing is remembered (the CORS check asks about every foreign origin), and a
        // domain that gets verified is picked up at once because the verification invalidates the host.
        var org = BuildOrg("villa-mare", PublicHostMode.CustomDomain);
        org.CustomDomain = "pending.example.it";
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        var verified = false;

        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("pending.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => verified ? org : null);
        _orgService.Setup(s => s.GetBySubdomainOrSlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);

        var beforeVerify = await _resolver.ResolveAsync("pending.example.it", CancellationToken.None);
        var rememberedMiss = await _resolver.ResolveAsync("PENDING.example.it:443", CancellationToken.None);
        verified = true;
        var stillRemembered = await _resolver.ResolveAsync("pending.example.it", CancellationToken.None);
        _resolver.InvalidateCacheForHost("pending.example.it");
        var afterInvalidation = await _resolver.ResolveAsync("pending.example.it", CancellationToken.None);

        Assert.Null(beforeVerify);
        Assert.Null(rememberedMiss);
        Assert.Null(stillRemembered);
        Assert.NotNull(afterInvalidation);
        Assert.Equal("villa-mare", afterInvalidation!.Slug);
        _orgService.Verify(
            s => s.GetByVerifiedCustomDomainAsync("pending.example.it", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ResolveAsync_ResolvedHost_IsServedFromTheCacheUntilItIsInvalidated()
    {
        var org = BuildOrg("villa-mare", PublicHostMode.CustomDomain);
        org.CustomDomain = "www.villa-mare.it";
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("www.villa-mare.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        await _resolver.ResolveAsync("www.villa-mare.it", CancellationToken.None);
        await _resolver.ResolveAsync("www.villa-mare.it", CancellationToken.None);
        _resolver.InvalidateCacheForHost("www.villa-mare.it");
        await _resolver.ResolveAsync("www.villa-mare.it", CancellationToken.None);

        _orgService.Verify(
            s => s.GetByVerifiedCustomDomainAsync("www.villa-mare.it", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Theory]
    [InlineData(CasazenPathMode)]
    [InlineData(CustomDomainMode)]
    public async Task ResolveAsync_SubdomainOfAnOrgThatDidNotChooseTheSubdomainMode_ReturnsNull(string mode)
    {
        // BK-16 (A3-08): no wildcard on the base domain, only the orgs that opted in are served on a label.
        var org = BuildOrg("villa-mare", Enum.Parse<PublicHostMode>(mode));
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync("villa-mare.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);
        _orgService.Setup(s => s.GetBySubdomainOrSlugAsync("villa-mare", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var result = await _resolver.ResolveAsync("villa-mare.casazen.it", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("evil.test/path")]
    [InlineData("villa-mare.casazen.it\"><script>")]
    [InlineData("a b")]
    [InlineData("-x.casazen.it")]
    [InlineData("https://villa-mare.casazen.it")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ResolveAsync_NotAHostName_ReturnsNullWithoutAnyLookup(string host)
    {
        var result = await _resolver.ResolveAsync(host, CancellationToken.None);

        Assert.Null(result);
        _orgService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_AnswersAreCachedPerHostAndNeverAcrossHosts()
    {
        var org = BuildOrg("villa-mare", PublicHostMode.CasazenSubdomain);
        org.Subdomain = "villa-mare";
        _orgService.Setup(s => s.GetByVerifiedCustomDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);
        _orgService.Setup(s => s.GetBySubdomainOrSlugAsync("villa-mare", It.IsAny<CancellationToken>())).ReturnsAsync(org);
        _orgService.Setup(s => s.GetBySubdomainOrSlugAsync("other", It.IsAny<CancellationToken>())).ReturnsAsync((OrgEntity?)null);

        var found = await _resolver.ResolveAsync("villa-mare.casazen.it", CancellationToken.None);
        var missing = await _resolver.ResolveAsync("other.casazen.it", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Null(missing);
    }

    private const string CasazenPathMode = nameof(PublicHostMode.CasazenPath);
    private const string CustomDomainMode = nameof(PublicHostMode.CustomDomain);

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
