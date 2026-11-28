using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class OrgHostCorsOriginSourceTests
{
    private const string AgencyA = "prenota.agenzia-a.it";
    private const string AgencyB = "prenota.agenzia-b.it";
    private const string SurfaceOff = "www.agenzia-c.it";
    private const string Subdomain = "villa.casazen.it";

    private readonly Mock<IPublicHostResolver> _resolver = new();
    private readonly OrgHostCorsOriginSource _source;

    public OrgHostCorsOriginSourceTests()
    {
        _resolver.Setup(r => r.ResolveAsync(AgencyA, It.IsAny<CancellationToken>())).ReturnsAsync(Host(PublicHostMode.CustomDomain, ownerPortal: true));
        _resolver.Setup(r => r.ResolveAsync(AgencyB, It.IsAny<CancellationToken>())).ReturnsAsync(Host(PublicHostMode.CustomDomain, ownerPortal: false));
        _resolver.Setup(r => r.ResolveAsync(SurfaceOff, It.IsAny<CancellationToken>())).ReturnsAsync(Host(PublicHostMode.CustomDomain, ownerPortal: false));
        _resolver.Setup(r => r.ResolveAsync(Subdomain, It.IsAny<CancellationToken>())).ReturnsAsync(Host(PublicHostMode.CasazenSubdomain, ownerPortal: false));
        _resolver.Setup(r => r.ResolveAsync("pending.agenzia-d.it", It.IsAny<CancellationToken>())).ReturnsAsync((ResolveHostResponseDto?)null);
        _source = new OrgHostCorsOriginSource(_resolver.Object);
    }

    [Theory]
    [InlineData("/api/owner-portal/properties")]
    [InlineData("/api/owner-portal/conversations/00000000-0000-0000-0000-000000000001/messages")]
    [InlineData("/api/owner-invitations/accept")]
    [InlineData("/api/owner-contacts/accept")]
    public async Task OwnerPortalPath_OwnSurfaceOn_Allowed(string path)
    {
        Assert.True(await AllowedAsync($"https://{AgencyA}", path));
    }

    [Fact]
    public async Task OwnerPortalPath_OtherOrgsVerifiedDomain_Refused()
    {
        Assert.False(await AllowedAsync($"https://{AgencyB}", "/api/owner-portal/properties"));
        Assert.False(await AllowedAsync($"https://{AgencyB}", "/api/owner-invitations/accept"));
    }

    [Fact]
    public async Task OwnerPortalPath_SurfaceOff_Refused()
    {
        Assert.False(await AllowedAsync($"https://{SurfaceOff}", "/api/owner-portal/properties"));
        Assert.False(await AllowedAsync($"https://{SurfaceOff}", "/api/owner-contacts/accept"));
    }

    [Theory]
    [InlineData("https://pending.agenzia-d.it")]
    [InlineData("https://unknown.example.test")]
    [InlineData("https://villa.casazen.it")]
    [InlineData("http://prenota.agenzia-a.it")]
    [InlineData("https://prenota.agenzia-a.it:8443")]
    [InlineData("null")]
    public async Task OwnerPortalPath_UnverifiedHost_Refused(string origin)
    {
        Assert.False(await AllowedAsync(origin, "/api/owner-portal/properties"));
    }

    [Theory]
    [InlineData("/api/properties")]
    [InlineData("/api/orgs/00000000-0000-0000-0000-000000000001/domain")]
    [InlineData("/api/admin/users")]
    [InlineData("/api/owner-accounting/statements")]
    [InlineData("/api/owner-invoices")]
    [InlineData("/api/owners/00000000-0000-0000-0000-000000000001/invite")]
    [InlineData("/api/owner-invitations")]
    [InlineData("/api/owner-invitations/accepted")]
    [InlineData("/api/onboarding/owners/invitations")]
    [InlineData("/api/owner-portalx")]
    public async Task HostOnlyPath_VerifiedHost_Refused(string path)
    {
        Assert.False(await AllowedAsync($"https://{AgencyA}", path));
    }

    [Theory]
    [InlineData("/api/public/orgs/anything")]
    [InlineData("/api/legal/privacy")]
    [InlineData("/api/public/guest/bookings")]
    [InlineData("/api/public/checkin/abc")]
    public async Task PublicAndGuestAreaPaths_EveryOrgHost_AllowedAsBefore(string path)
    {
        Assert.True(await AllowedAsync($"https://{AgencyA}", path));
        Assert.True(await AllowedAsync($"https://{SurfaceOff}", path));
        Assert.True(await AllowedAsync($"https://{Subdomain}", path));
    }

    [Fact]
    public void GuestAreaPrefixes_StayUnderThePublicPrefixes()
    {
        Assert.Contains(OrgHostCorsOriginSource.PublicPathPrefixes, prefix => new PathString("/api/public/guest").StartsWithSegments(prefix));
        Assert.Contains(OrgHostCorsOriginSource.PublicPathPrefixes, prefix => new PathString("/api/public/checkin").StartsWithSegments(prefix));
    }

    private ValueTask<bool> AllowedAsync(string origin, string path) =>
        _source.IsOriginAllowedAsync(origin, new PathString(path), CancellationToken.None);

    private static ResolveHostResponseDto Host(PublicHostMode mode, bool ownerPortal) => new()
    {
        OrgId = Guid.NewGuid(),
        Slug = "agency",
        PublicHostMode = mode,
        Surfaces = new ResolveHostSurfacesDto { GuestArea = ownerPortal, OwnerPortal = ownerPortal },
        OwnerLoginReady = ownerPortal,
    };
}
