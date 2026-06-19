using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
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
        var options = Options.Create(new PublicHostOptions { BaseDomain = "casazen.it" });
        _resolver = new PublicHostResolver(_orgService.Object, options);
    }

    [Fact]
    public async Task ResolveAsync_CasazenSubdomain_ReturnsOrgBranding()
    {
        var org = new OrgEntity
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"),
            Slug = "villa-mare",
            DisplayName = "Villa Mare",
            ThemeColor = "#2563eb",
            ContactEmail = "host@villamare.it",
            IsActive = true,
        };
        _orgService.Setup(s => s.GetPublicBySlugAsync("villa-mare", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var result = await _resolver.ResolveAsync("villa-mare.casazen.it", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PublicHostMode.CasazenSubdomain, result!.PublicHostMode);
        Assert.Equal("villa-mare", result.Slug);
        Assert.Equal("Villa Mare", result.Branding.DisplayName);
    }

    [Theory]
    [InlineData("api.casazen.it")]
    [InlineData("www.casazen.it")]
    [InlineData("casazen.it")]
    [InlineData("unknown.example.com")]
    public async Task ResolveAsync_ReservedOrUnknownHost_ReturnsNull(string host)
    {
        var result = await _resolver.ResolveAsync(host, CancellationToken.None);
        Assert.Null(result);
        _orgService.Verify(
            s => s.GetPublicBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
