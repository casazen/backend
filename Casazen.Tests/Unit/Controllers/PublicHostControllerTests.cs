using System.Text.Json;
using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PublicHostControllerTests
{
    private readonly Mock<IPublicHostResolver> _resolver = new();
    private readonly PublicHostController _controller;

    public PublicHostControllerTests()
    {
        _controller = new PublicHostController(_resolver.Object);
    }

    [Fact]
    public async Task ResolveHost_WhenFound_Returns200()
    {
        var dto = new ResolveHostResponseDto
        {
            OrgId = Guid.NewGuid(),
            Slug = "villa-mare",
            PublicHostMode = PublicHostMode.CasazenSubdomain,
            Branding = new ResolveHostBrandingDto { Slug = "villa-mare", DisplayName = "Villa Mare" },
        };
        _resolver.Setup(r => r.ResolveAsync("villa-mare.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var result = await _controller.ResolveHost("villa-mare.casazen.it", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ResolveHostResponseDto>(ok.Value);
        Assert.Equal("villa-mare", body.Slug);
    }

    [Fact]
    public async Task ResolveHost_WhenUnknown_Returns404()
    {
        _resolver.Setup(r => r.ResolveAsync("missing.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolveHostResponseDto?)null);

        var result = await _controller.ResolveHost("missing.casazen.it", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ─── E40/F01/US01/T01: the surfaces of the agency's own domain ──────────────────────────────────────

    private const string AgencyHost = "prenota.villa-mare.it";

    [Fact]
    public async Task ResolveHost_FlagsOff_NewFieldsFalse_ExistingFieldsUnchanged()
    {
        // An org whose verified domain could serve everything: with every flag off nothing new is served and the old members are as before.
        var controller = new PublicHostController(RealResolver(new Dictionary<string, string?>()));

        var result = await controller.ResolveHost(AgencyHost, CancellationToken.None);

        var body = Assert.IsType<ResolveHostResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(AgencyOrgId, body.OrgId);
        Assert.Equal("villa-mare", body.Slug);
        Assert.Equal(PublicHostMode.CustomDomain, body.PublicHostMode);
        Assert.Equal("Pro", body.PlanTier);
        Assert.Equal("Villa Mare", body.Branding.DisplayName);
        Assert.False(body.Surfaces.GuestArea);
        Assert.False(body.Surfaces.OwnerPortal);
        Assert.False(body.OwnerLoginReady);
        Assert.False(body.EmbedEnabled);
    }

    [Fact]
    public async Task ResolveHost_FlagsOn_NewFieldsTrueInCamelCase()
    {
        var controller = new PublicHostController(RealResolver(new Dictionary<string, string?>
        {
            ["Features:WhiteLabelHost"] = "true",
            ["Features:SiteEmbed"] = "true",
        }));

        var result = await controller.ResolveHost(AgencyHost, CancellationToken.None);

        var body = Assert.IsType<OkObjectResult>(result.Result).Value;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        Assert.True(root.GetProperty("surfaces").GetProperty("guestArea").GetBoolean());
        Assert.True(root.GetProperty("surfaces").GetProperty("ownerPortal").GetBoolean());
        Assert.True(root.GetProperty("ownerLoginReady").GetBoolean());
        Assert.True(root.GetProperty("embedEnabled").GetBoolean());
        Assert.Equal("villa-mare", root.GetProperty("slug").GetString());
        Assert.Equal("Pro", root.GetProperty("planTier").GetString());
    }

    private static readonly Guid AgencyOrgId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa40");

    private static PublicHostResolver RealResolver(Dictionary<string, string?> flags)
    {
        var org = new OrgEntity
        {
            Id = AgencyOrgId,
            Slug = "villa-mare",
            DisplayName = "Villa Mare",
            IsActive = true,
            PlanTier = PlanTier.Pro,
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = AgencyHost,
            DomainVerificationStatus = DomainVerificationStatus.Verified,
            GuestAreaOnOwnDomain = true,
            OwnerPortalOnOwnDomain = true,
            UseOwnDomainForLinks = true,
            OwnerLoginHostState = OwnerLoginHostState.Ready,
        };
        var orgService = new Mock<IOrgService>();
        orgService.Setup(s => s.GetByVerifiedCustomDomainAsync(AgencyHost, It.IsAny<CancellationToken>())).ReturnsAsync(org);
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.CanUseCustomDomainAsync(AgencyOrgId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns(PlanTier.Pro);

        return new PublicHostResolver(
            orgService.Object,
            entitlements.Object,
            Options.Create(new PublicHostOptions { BaseDomain = "casazen.it" }),
            new MemoryCache(new MemoryCacheOptions()),
            featureFlags: new ConfigurationFeatureFlags(new ConfigurationBuilder().AddInMemoryCollection(flags).Build()));
    }
}
