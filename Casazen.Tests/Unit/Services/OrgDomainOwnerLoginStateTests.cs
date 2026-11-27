using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E40/F01/US01/T01 (NF-141): the registration of the owner sign-in in Auth0 belongs to ONE domain. A new custom domain forgets the one of the previous domain (the state
/// goes back to <c>NotRequested</c>) and, when the agency keeps its choice to serve the owner portal on its domain, asks for the registration of the new one at once
/// (<c>Pending</c>), as saving the choice does; with the choice off nothing is asked. The same domain saved again keeps its registration, and a switch to the platform
/// path leaves no domain to register.
/// </summary>
public class OrgDomainOwnerLoginStateTests
{
    private const string FirstDomain = "prenota.primo-dominio.it";
    private const string NewDomain = "prenota.nuovo-dominio.it";

    [Theory]
    [InlineData(true, OwnerLoginHostState.Pending)]
    [InlineData(false, OwnerLoginHostState.NotRequested)]
    public async Task SetDomain_AnotherCustomDomain_ForgetsTheRegistration_AndAsksAgainOnlyWithTheOwnerPortalOn(bool ownerPortalOn, OwnerLoginHostState expected)
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, ownerPortalOn);

        var result = await Service(db).SetDomainAsync(org.Id, PublicHostMode.CustomDomain, NewDomain, subdomain: null);

        Assert.Equal(SetOrgDomainOutcome.Success, result.Outcome);
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Equal(NewDomain, stored.CustomDomain);
        Assert.Equal(expected, stored.OwnerLoginHostState);
        // The choices of the agency stay.
        Assert.Equal(ownerPortalOn, stored.OwnerPortalOnOwnDomain);
        Assert.True(stored.GuestAreaOnOwnDomain);
        Assert.False(stored.UseOwnDomainForLinks);
    }

    [Fact]
    public async Task SetDomain_TheSameVerifiedDomainSavedAgain_KeepsItsRegistration()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, ownerPortalOn: true);

        var result = await Service(db).SetDomainAsync(org.Id, PublicHostMode.CustomDomain, FirstDomain, subdomain: null);

        Assert.Equal(SetOrgDomainOutcome.Success, result.Outcome);
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Equal(FirstDomain, stored.CustomDomain);
        Assert.Equal(OwnerLoginHostState.Ready, stored.OwnerLoginHostState);
    }

    [Fact]
    public async Task SetDomain_SwitchToThePlatformPath_ForgetsTheRegistration_AndAsksForNone()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, ownerPortalOn: true);

        var result = await Service(db).SetDomainAsync(org.Id, PublicHostMode.CasazenPath, customDomain: null, subdomain: null);

        Assert.Equal(SetOrgDomainOutcome.Success, result.Outcome);
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Null(stored.CustomDomain);
        Assert.Equal(OwnerLoginHostState.NotRequested, stored.OwnerLoginHostState);
        Assert.True(stored.OwnerPortalOnOwnDomain);
    }

    private static OrgDomainService Service(AppDbContext db)
    {
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.CanUseCustomDomainAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var hostOptions = Options.Create(new PublicHostOptions { BaseDomain = "sites.example.test" });
        var links = EmailTestHelpers.Links();
        var siteUrls = new PublicOrgSiteUrls(links, hostOptions, entitlements.Object);

        return new OrgDomainService(
            db,
            entitlements.Object,
            Mock.Of<IDomainVerificationService>(),
            Mock.Of<IPublicHostResolver>(),
            hostOptions,
            links,
            siteUrls,
            Mock.Of<IVercelDomainsClient>(),
            TimeProvider.System);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>An org with a verified custom domain whose owner sign-in is registered (<c>Ready</c>), the guest area on and the links off.</summary>
    private static async Task<OrgEntity> SeedOrgAsync(AppDbContext db, bool ownerPortalOn)
    {
        var org = new OrgEntity
        {
            Name = "Agenzia",
            DisplayName = "Agenzia",
            Slug = "agenzia",
            ContactEmail = "info@agenzia.test",
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = FirstDomain,
            DomainVerificationStatus = DomainVerificationStatus.Verified,
            DomainVerificationToken = "verify-token",
            GuestAreaOnOwnDomain = true,
            OwnerPortalOnOwnDomain = ownerPortalOn,
            UseOwnDomainForLinks = false,
            OwnerLoginHostState = OwnerLoginHostState.Ready,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }
}
