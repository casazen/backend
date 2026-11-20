using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E40/F01/US01/T01 (NF-140..NF-142, NF-144): the one rule of the surfaces of an org's host. Nothing changes for an org without a verified
/// custom domain or with the flags off; the owner sign-in and the owner links need the Auth0 state <c>Ready</c>.
/// </summary>
public class HostSurfaceRulesTests
{
    [Theory]
    [InlineData(PublicHostMode.CustomDomain)]
    [InlineData(PublicHostMode.CasazenSubdomain)]
    [InlineData(PublicHostMode.CasazenPath)]
    public void Evaluate_FlagOff_AllSurfacesFalse(PublicHostMode servedMode)
    {
        var surfaces = HostSurfaceRules.Evaluate(EverythingOn(), servedMode, whiteLabelHost: false, siteEmbed: false);

        Assert.Equal(new HostSurfaces(false, false, false, false, false, false, false), surfaces);
    }

    [Theory]
    [InlineData(PublicHostMode.CasazenSubdomain)]
    [InlineData(PublicHostMode.CasazenPath)]
    public void Evaluate_SubdomainMode_NeverSurfaces(PublicHostMode servedMode)
    {
        // A platform subdomain (or the platform path, e.g. a custom domain not verified or not paid for) never serves a surface.
        var surfaces = HostSurfaceRules.Evaluate(EverythingOn(), servedMode, whiteLabelHost: true, siteEmbed: true);

        Assert.False(surfaces.GuestArea);
        Assert.False(surfaces.OwnerPortal);
        Assert.False(surfaces.OwnerLoginReady);
        Assert.False(surfaces.UseOwnDomainForLinks);
        Assert.False(surfaces.GuestLinksOnOwnDomain);
        Assert.False(surfaces.OwnerLinksOnOwnDomain);
        Assert.True(surfaces.EmbedEnabled);
    }

    [Theory]
    [InlineData(OwnerLoginHostState.NotRequested, false)]
    [InlineData(OwnerLoginHostState.Pending, false)]
    [InlineData(OwnerLoginHostState.Failed, false)]
    [InlineData(OwnerLoginHostState.Ready, true)]
    public void Evaluate_OwnerLoginReadyNeedsReadyState(OwnerLoginHostState state, bool expected)
    {
        var org = EverythingOn();
        org.OwnerLoginHostState = state;

        var surfaces = HostSurfaceRules.Evaluate(org, PublicHostMode.CustomDomain, whiteLabelHost: true, siteEmbed: false);

        Assert.True(surfaces.OwnerPortal);
        Assert.Equal(expected, surfaces.OwnerLoginReady);
        // An owner is never sent to a domain where the sign-in is not enabled.
        Assert.Equal(expected, surfaces.OwnerLinksOnOwnDomain);
    }

    [Fact]
    public void Evaluate_OwnerPortalOff_OwnerLoginNotReadyEvenWithReadyState()
    {
        var org = EverythingOn();
        org.OwnerPortalOnOwnDomain = false;

        var surfaces = HostSurfaceRules.Evaluate(org, PublicHostMode.CustomDomain, whiteLabelHost: true, siteEmbed: false);

        Assert.False(surfaces.OwnerPortal);
        Assert.False(surfaces.OwnerLoginReady);
        Assert.False(surfaces.OwnerLinksOnOwnDomain);
        Assert.True(surfaces.GuestArea);
    }

    [Fact]
    public void Evaluate_ServedCustomDomainAndEverythingOn_EverySurfaceOn()
    {
        var surfaces = HostSurfaceRules.Evaluate(EverythingOn(), PublicHostMode.CustomDomain, whiteLabelHost: true, siteEmbed: true);

        Assert.Equal(new HostSurfaces(true, true, true, true, true, true, true), surfaces);
    }

    [Fact]
    public void Evaluate_SettingsOff_NoSurfaceAndNoLink()
    {
        var org = EverythingOn();
        org.GuestAreaOnOwnDomain = false;
        org.OwnerPortalOnOwnDomain = false;

        var surfaces = HostSurfaceRules.Evaluate(org, PublicHostMode.CustomDomain, whiteLabelHost: true, siteEmbed: false);

        Assert.False(surfaces.GuestArea);
        Assert.False(surfaces.OwnerPortal);
        Assert.False(surfaces.GuestLinksOnOwnDomain);
        Assert.False(surfaces.OwnerLinksOnOwnDomain);
    }

    [Fact]
    public void Evaluate_UseOwnDomainForLinksOff_SurfacesStayLinksDoNot()
    {
        var org = EverythingOn();
        org.UseOwnDomainForLinks = false;

        var surfaces = HostSurfaceRules.Evaluate(org, PublicHostMode.CustomDomain, whiteLabelHost: true, siteEmbed: false);

        Assert.True(surfaces.GuestArea);
        Assert.True(surfaces.OwnerLoginReady);
        Assert.False(surfaces.UseOwnDomainForLinks);
        Assert.False(surfaces.GuestLinksOnOwnDomain);
        Assert.False(surfaces.OwnerLinksOnOwnDomain);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Evaluate_EmbedEnabled_FollowsSiteEmbedAlone(bool siteEmbed, bool expected)
    {
        var bare = new OrgEntity { Slug = "bare", PublicHostMode = PublicHostMode.CasazenPath };

        Assert.Equal(expected, HostSurfaceRules.Evaluate(bare, PublicHostMode.CasazenPath, whiteLabelHost: false, siteEmbed).EmbedEnabled);
        Assert.Equal(expected, HostSurfaceRules.Evaluate(EverythingOn(), PublicHostMode.CustomDomain, whiteLabelHost: true, siteEmbed).EmbedEnabled);
    }

    private static OrgEntity EverythingOn() => new()
    {
        Slug = "villa-mare",
        PublicHostMode = PublicHostMode.CustomDomain,
        CustomDomain = "prenota.villa-mare.it",
        DomainVerificationStatus = DomainVerificationStatus.Verified,
        GuestAreaOnOwnDomain = true,
        OwnerPortalOnOwnDomain = true,
        UseOwnDomainForLinks = true,
        OwnerLoginHostState = OwnerLoginHostState.Ready,
    };
}
