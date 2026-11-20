using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E40/F01/US01/T03 (NF-142): the links CasaZen sends for an org move to the agency's own domain only when the flag, a verified domain the org pays
/// for, the agency's settings and the surface of the link all say so; in every other case they are the platform links, byte for byte. An owner is
/// never sent to a domain where its sign-in does not work, the host comes from the org record only, and a link carries only its token or code.
/// </summary>
public sealed class AgencyDomainLinksTests : IDisposable
{
    private const string Domain = "prenota.agenzia.test";
    private const string Token = "a1b2c3";
    private const string Code = "AB12CD";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"agency-domain-links-{Guid.NewGuid():N}")
        .Options);

    private readonly TestFeatureFlags _flags = new(FeatureFlags.WhiteLabelHost);
    private readonly PublicSiteLinks _platform = EmailTestHelpers.Links();
    private readonly Mock<IEntitlementService> _entitlements = new();

    public AgencyDomainLinksTests()
    {
        _entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns((OrgEntity o) => o.PlanTier);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GuestLinks_OnlyWhenFlagDomainSettingAndSurfaceAreOn()
    {
        var org = await SeedAsync(o => o.OwnerPortalOnOwnDomain = false);

        var links = await Builder().ForOrgAsync(org.Id);

        Assert.Equal(Domain, links.GuestHost);
        Assert.Equal($"https://{Domain}/checkin/{Token}", links.GuestCheckIn(Token));
        Assert.Equal($"https://{Domain}/my-bookings?code={Code}", links.GuestBookings(org.Slug, Code));
        Assert.Equal($"https://{Domain}/my-bookings", links.GuestBookings(org.Slug));
        // The owner portal is not on the domain: the owner links stay on the CasaZen domain.
        Assert.Null(links.OwnerHost);
        Assert.Equal(_platform.OwnerInvitationAccept(Token), links.OwnerInvitationAccept(Token));
    }

    public static TheoryData<string> FailingConditions => new()
    {
        "flag off",
        "domain not verified",
        "subdomain mode",
        "path mode",
        "plan not paid",
        "guest area off",
        "links off",
        "org missing",
    };

    [Theory]
    [MemberData(nameof(FailingConditions))]
    public async Task PlatformLinks_ByteForByte_WhenAnyConditionFails(string condition)
    {
        var org = await SeedAsync(o =>
        {
            switch (condition)
            {
                case "domain not verified":
                    o.DomainVerificationStatus = DomainVerificationStatus.Pending;
                    break;
                case "subdomain mode":
                    o.PublicHostMode = PublicHostMode.CasazenSubdomain;
                    o.Subdomain = "agenzia";
                    break;
                case "path mode":
                    o.PublicHostMode = PublicHostMode.CasazenPath;
                    break;
                case "plan not paid":
                    o.PlanTier = PlanTier.Starter;
                    break;
                case "guest area off":
                    o.GuestAreaOnOwnDomain = false;
                    o.OwnerPortalOnOwnDomain = false;
                    break;
                case "links off":
                    o.UseOwnDomainForLinks = false;
                    break;
            }
        });
        if (condition == "flag off")
            _flags.Set(FeatureFlags.WhiteLabelHost, false);
        var orgId = condition == "org missing" ? Guid.NewGuid() : org.Id;

        var links = await Builder().ForOrgAsync(orgId);

        Assert.Null(links.GuestHost);
        Assert.Null(links.OwnerHost);
        AssertPlatform(links, org.Slug);
    }

    [Theory]
    [InlineData(OwnerLoginHostState.NotRequested, false)]
    [InlineData(OwnerLoginHostState.Pending, false)]
    [InlineData(OwnerLoginHostState.Failed, false)]
    [InlineData(OwnerLoginHostState.Ready, true)]
    public async Task OwnerLinks_NeedOwnerLoginReady(OwnerLoginHostState state, bool onTheDomain)
    {
        var conversation = Guid.Parse("c0000000-0000-4000-8000-000000000001");
        var org = await SeedAsync(o => o.OwnerLoginHostState = state);

        var links = await Builder().ForOrgAsync(org.Id);

        if (onTheDomain)
        {
            Assert.Equal(Domain, links.OwnerHost);
            Assert.Equal($"https://{Domain}/portale-proprietari/invito?token={Token}", links.OwnerInvitationAccept(Token));
            Assert.Equal($"https://{Domain}/portale-proprietari/contatto?token={Token}", links.OwnerContactAccept(Token));
            Assert.Equal($"https://{Domain}/portale-proprietari/messaggi/{conversation:D}", links.OwnerPortalConversation(conversation));
            Assert.Equal($"https://{Domain}/portale-proprietari/contabilita", links.OwnerPortalAccounting());
        }
        else
        {
            // «Not yet enabled» on the domain: the owner is sent to the CasaZen domain, where the sign-in works.
            Assert.Null(links.OwnerHost);
            Assert.Equal(_platform.OwnerInvitationAccept(Token), links.OwnerInvitationAccept(Token));
            Assert.Equal(_platform.OwnerContactAccept(Token), links.OwnerContactAccept(Token));
            Assert.Equal(_platform.OwnerPortalConversation(conversation), links.OwnerPortalConversation(conversation));
            Assert.Equal(_platform.OwnerPortalAccounting(), links.OwnerPortalAccounting());
        }

        // The guest links follow the guest area alone.
        Assert.Equal($"https://{Domain}/checkin/{Token}", links.GuestCheckIn(Token));
    }

    [Fact]
    public async Task Host_ComesFromOrgRecordOnly()
    {
        // Nothing of a request reaches the builder: no HTTP context, no header, no body. The host is the org's own verified domain.
        var parameters = typeof(AgencyDomainLinks).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(typeof(IHttpContextAccessor), parameters);
        Assert.DoesNotContain(typeof(HttpContext), parameters);
        Assert.DoesNotContain(parameters, t => t.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);

        var org = await SeedAsync();
        var other = await SeedAsync(o =>
        {
            o.Slug = "altra-agenzia";
            o.CustomDomain = "prenota.altra-agenzia.test";
        });

        Assert.Equal(Domain, (await Builder().ForOrgAsync(org.Id)).GuestHost);
        Assert.Equal("prenota.altra-agenzia.test", (await Builder().ForOrgAsync(other.Id)).GuestHost);
    }

    [Fact]
    public async Task Links_CarryOnlyTokenOrCode()
    {
        var org = await SeedAsync();
        var links = await Builder().ForOrgAsync(org.Id);
        const string odd = "a b/c?d";

        // The same escaping as the platform links, and nothing but the token or the code after the path.
        Assert.Equal($"https://{Domain}/checkin/a%20b%2Fc%3Fd", links.GuestCheckIn(odd));
        Assert.Equal(_platform.GuestCheckIn(odd)[EmailTestHelpers.PublicSiteBaseUrl.Length..], links.GuestCheckIn(odd)[$"https://{Domain}".Length..]);
        Assert.Equal($"https://{Domain}/my-bookings?code=a%20b%2Fc%3Fd", links.GuestBookings(org.Slug, odd));
        Assert.Equal($"https://{Domain}/portale-proprietari/invito?token=a%20b%2Fc%3Fd", links.OwnerInvitationAccept(odd));
        Assert.DoesNotContain(org.Slug, links.GuestBookings(org.Slug, Code), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadError_GivesThePlatformLinks()
    {
        var org = await SeedAsync();
        var builder = Builder();
        await _db.DisposeAsync();

        var links = await builder.ForOrgAsync(org.Id);

        Assert.Null(links.GuestHost);
        AssertPlatform(links, org.Slug);
    }

    [Fact]
    public async Task PlatformNotConfigured_KeepsTheFailureOfToday()
    {
        // Without App:PublicSiteBaseUrl the platform links throw as they always did: the agency domain never hides a configuration error.
        var org = await SeedAsync();
        var unconfigured = EmailTestHelpers.Links(null);
        var builder = new AgencyDomainLinks(_db, unconfigured, SiteUrls(unconfigured), _flags, NullLogger<AgencyDomainLinks>.Instance);

        var links = await builder.ForOrgAsync(org.Id);

        Assert.Null(links.GuestHost);
        Assert.Throws<EmailConfigurationException>(() => links.GuestCheckIn(Token));
    }

    [Fact]
    public async Task NoBuilder_GivesThePlatformLinks()
    {
        var links = await ((AgencyDomainLinks?)null).ForOrgOrPlatformAsync(_platform, Guid.NewGuid());

        AssertPlatform(links, "villa-org");
    }

    private void AssertPlatform(AgencyLinks links, string slug)
    {
        var conversation = Guid.Parse("c0000000-0000-4000-8000-000000000002");
        Assert.Equal(_platform.GuestCheckIn(Token), links.GuestCheckIn(Token));
        Assert.Equal(_platform.GuestBookings(slug, Code), links.GuestBookings(slug, Code));
        Assert.Equal(_platform.GuestBookings(slug), links.GuestBookings(slug));
        Assert.Equal(_platform.OwnerInvitationAccept(Token), links.OwnerInvitationAccept(Token));
        Assert.Equal(_platform.OwnerContactAccept(Token), links.OwnerContactAccept(Token));
        Assert.Equal(_platform.OwnerPortalConversation(conversation), links.OwnerPortalConversation(conversation));
        Assert.Equal(_platform.OwnerPortalAccounting(), links.OwnerPortalAccounting());
    }

    private AgencyDomainLinks Builder() =>
        new(_db, _platform, SiteUrls(_platform), _flags, NullLogger<AgencyDomainLinks>.Instance);

    private PublicOrgSiteUrls SiteUrls(PublicSiteLinks links) =>
        new(links, Options.Create(new PublicHostOptions { BaseDomain = "sites.casazen.test" }), _entitlements.Object);

    /// <summary>An org whose verified domain serves everything; <paramref name="change"/> takes one condition away.</summary>
    private async Task<OrgEntity> SeedAsync(Action<OrgEntity>? change = null)
    {
        var org = new OrgEntity
        {
            Name = "Agenzia",
            Slug = $"agenzia-{Guid.NewGuid():N}"[..20],
            DisplayName = "Agenzia",
            ContactEmail = "host@example.com",
            PlanTier = PlanTier.Pro,
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = Domain,
            DomainVerificationStatus = DomainVerificationStatus.Verified,
            GuestAreaOnOwnDomain = true,
            OwnerPortalOnOwnDomain = true,
            UseOwnDomainForLinks = true,
            OwnerLoginHostState = OwnerLoginHostState.Ready,
        };
        change?.Invoke(org);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return org;
    }
}
