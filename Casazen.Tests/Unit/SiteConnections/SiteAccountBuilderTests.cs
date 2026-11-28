using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Orgs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

public sealed class SiteAccountBuilderTests : IDisposable
{
    private const string OwnDomain = "prenota.quokka360.com";

    private readonly SiteApiHarness _harness = new(FeatureFlags.SiteEmbed);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Build_OrgServedOnItsVerifiedCustomDomain_GivesItsOrigin_AndTheDomainOfTheConsole()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);
        var site = SiteApiHarness.Site(org.Id);

        var result = await _harness.Accounts.BuildAsync(site);

        Assert.Equal(SiteAccountOutcome.Success, result.Outcome);
        var account = Assert.IsType<SiteAccount>(result.Account);
        Assert.Equal($"https://{OwnDomain}", account.PublicAddress);
        Assert.Equal(SitePublicAddressKind.CustomDomain, account.PublicAddressKind);
        Assert.Equal((site.ConnectionId, org.Id, "quokka360", "Quokka360"), (account.ConnectionId, account.Org.Id, account.Org.Slug, account.Org.DisplayName));
        Assert.True(account.EmbedEnabled);

        var consoleConfig = await _harness.OrgDomain.GetDomainConfigAsync(org.Id);
        Assert.Equal(
            SiteApiHarness.Serialize(OrgDomainMapper.ToDto(consoleConfig!, _harness.Localizer)),
            SiteApiHarness.Serialize(OrgDomainMapper.ToDto(account.Domain, _harness.Localizer)));
    }

    [Fact]
    public async Task Build_OrgWithoutADomain_GivesTheCasazenAddress_AndTheKindCasazen()
    {
        var org = _harness.SeedOrg("altra-agenzia", "Altra Agenzia");

        var result = await _harness.Accounts.BuildAsync(SiteApiHarness.Site(org.Id));

        var account = Assert.IsType<SiteAccount>(result.Account);
        Assert.Equal(SiteApiHarness.CasazenAddress, account.PublicAddress);
        Assert.Equal(SitePublicAddressKind.Casazen, account.PublicAddressKind);
        Assert.Equal(PublicHostMode.CasazenPath, account.Domain.PublicHostMode);
        Assert.True(account.EmbedEnabled);
    }

    [Theory]
    [InlineData(DomainVerificationStatus.Pending, true)]
    [InlineData(DomainVerificationStatus.Failed, true)]
    [InlineData(DomainVerificationStatus.Verified, false)]
    public async Task Build_ADomainThatIsNotServedNow_IsStillTheCasazenAddress(DomainVerificationStatus status, bool paid)
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain, paid, status);

        var result = await _harness.Accounts.BuildAsync(SiteApiHarness.Site(org.Id));

        var account = Assert.IsType<SiteAccount>(result.Account);
        Assert.Equal(SiteApiHarness.CasazenAddress, account.PublicAddress);
        Assert.Equal(SitePublicAddressKind.Casazen, account.PublicAddressKind);
        Assert.Equal(OwnDomain, account.Domain.CustomDomain);
        Assert.Null(account.Domain.PublicUrls.CustomDomainUrl);
    }

    [Fact]
    public async Task Build_OrgOnAPlatformSubdomain_IsTheCasazenAddress()
    {
        var org = _harness.SeedOrg(
            "quokka360",
            "Quokka360",
            configure: o =>
            {
                o.PublicHostMode = PublicHostMode.CasazenSubdomain;
                o.Subdomain = "quokka";
            });

        var result = await _harness.Accounts.BuildAsync(SiteApiHarness.Site(org.Id));

        var account = Assert.IsType<SiteAccount>(result.Account);
        Assert.Equal(SiteApiHarness.CasazenAddress, account.PublicAddress);
        Assert.Equal(SitePublicAddressKind.Casazen, account.PublicAddressKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Build_EmbedEnabled_IsWhatResolveHostAnswersForTheOrg(bool siteEmbed)
    {
        using var harness = siteEmbed ? new SiteApiHarness(FeatureFlags.SiteEmbed) : new SiteApiHarness();
        var withDomain = harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);
        var withoutDomain = harness.SeedOrg("altra-agenzia", "Altra Agenzia");

        var orgs = new Mock<IOrgService>();
        orgs.Setup(o => o.GetByVerifiedCustomDomainAsync(OwnDomain, It.IsAny<CancellationToken>())).ReturnsAsync(withDomain);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new PublicHostResolver(
            orgs.Object,
            harness.Entitlements.Object,
            Options.Create(new PublicHostOptions { BaseDomain = "sites.example.test" }),
            cache,
            featureFlags: harness.Flags);
        var resolved = await resolver.ResolveAsync(OwnDomain);
        Assert.NotNull(resolved);

        var accountWithDomain = (await harness.Accounts.BuildAsync(SiteApiHarness.Site(withDomain.Id))).Account!;
        var accountWithoutDomain = (await harness.Accounts.BuildAsync(SiteApiHarness.Site(withoutDomain.Id))).Account!;

        Assert.Equal(resolved.EmbedEnabled, accountWithDomain.EmbedEnabled);
        Assert.Equal(siteEmbed, accountWithDomain.EmbedEnabled);
        Assert.Equal(resolved.EmbedEnabled, accountWithoutDomain.EmbedEnabled);
    }

    [Fact]
    public async Task Build_AConfigurationOfAnotherOrg_IsNeverUsed_TheOneOfTheCredentialIsRead()
    {
        var orgA = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);
        var orgB = _harness.SeedOrgWithDomain("altra-agenzia", "Altra Agenzia", "prenota.altra.example");
        var configOfB = await _harness.OrgDomain.GetDomainConfigAsync(orgB.Id);

        var result = await _harness.Accounts.BuildAsync(SiteApiHarness.Site(orgA.Id), configOfB);

        var account = Assert.IsType<SiteAccount>(result.Account);
        Assert.Equal(orgA.Id, account.Org.Id);
        Assert.Equal(orgA.Id, account.Domain.OrgId);
        Assert.Equal(OwnDomain, account.Domain.CustomDomain);
        Assert.Equal($"https://{OwnDomain}", account.PublicAddress);
    }

    [Fact]
    public async Task Build_ASavedConfiguration_IsUsedAsItIs_WithoutReadingTheDomainAgain()
    {
        var org = _harness.SeedOrg("quokka360", "Quokka360");
        var saved = await _harness.OrgDomain.SetDomainAsync(org.Id, PublicHostMode.CustomDomain, OwnDomain, subdomain: null);
        Assert.Equal(SetOrgDomainOutcome.Success, saved.Outcome);

        var result = await _harness.Accounts.BuildAsync(SiteApiHarness.Site(org.Id), saved.Config);

        var account = Assert.IsType<SiteAccount>(result.Account);
        Assert.Same(saved.Config, account.Domain);
        Assert.Equal(SitePublicAddressKind.Casazen, account.PublicAddressKind);
        Assert.Equal(DomainVerificationStatus.Pending, account.Domain.DomainVerificationStatus);
    }

    [Fact]
    public async Task Build_UnknownOrInactiveOrg_IsNotFound()
    {
        var inactive = _harness.SeedOrg("spenta", "Spenta", configure: o => o.IsActive = false);

        Assert.Equal(SiteAccountOutcome.NotFound, (await _harness.Accounts.BuildAsync(SiteApiHarness.Site(Guid.NewGuid()))).Outcome);
        Assert.Equal(SiteAccountOutcome.NotFound, (await _harness.Accounts.BuildAsync(SiteApiHarness.Site(inactive.Id))).Outcome);
    }

    [Fact]
    public async Task Build_WithoutTheAddressOfCasazen_AnOrgThatNeedsItGetsNoAddress_AnOrgOnItsOwnDomainStillDoes()
    {
        var withoutAddress = new SiteAccountBuilder(
            _harness.Db,
            _harness.OrgDomain,
            _harness.SiteUrls,
            Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = null }),
            _harness.Flags);
        var needsIt = _harness.SeedOrg("altra-agenzia", "Altra Agenzia");
        var ownDomain = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);

        var refused = await withoutAddress.BuildAsync(SiteApiHarness.Site(needsIt.Id));
        var served = await withoutAddress.BuildAsync(SiteApiHarness.Site(ownDomain.Id));

        Assert.Equal(SiteAccountOutcome.PublicAddressNotConfigured, refused.Outcome);
        Assert.Null(refused.Account);
        Assert.Equal(SiteAccountOutcome.Success, served.Outcome);
        Assert.Equal($"https://{OwnDomain}", served.Account!.PublicAddress);
    }
}
