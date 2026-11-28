using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.DTOs.SiteConnections;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

/// <summary>
/// S023 (PLG-BE-SITEAPI, site integration plan §10.3-§10.4, NF-202, NF-204, AC 1 to 5) at the level of the controllers, with the real domain service, the real account
/// builder and the in-memory database: the account has the shape of the plan and carries the console's domain; <c>PUT domain</c> stores a domain as the console does
/// and takes it back to CasaZen with <c>null</c>; every refusal has the status and the <c>code</c> the console gives the same request; <c>verify</c> answers
/// <c>{ verify, account }</c>; <c>surfaces</c> changes the three choices; and the credential of one org never reads or changes another's, whatever a body says. The
/// pipeline (the schemes, the flags, the limits, the commercial gate) is proved over HTTP by <c>SiteApiIntegrationTests</c>.
/// </summary>
public sealed class SiteApiDomainEndpointsTests : IDisposable
{
    private const string OwnDomain = "prenota.quokka360.com";
    private const string OtherDomain = "prenota.altra.example";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SiteApiHarness _harness = new(FeatureFlags.SiteEmbed, FeatureFlags.WhiteLabelHost);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task GetAccount_HasTheShapeOfThePlan_AndItsDomainIsTheObjectTheConsoleGets()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);
        var site = SiteApiHarness.Site(org.Id);

        var answer = await _harness.Plugin(site).GetAccount(CancellationToken.None);

        var json = SiteApiHarness.ToJson(AccountOf(answer));
        Assert.Equal(
            new[] { "connectionId", "domain", "embedEnabled", "org", "publicAddress", "publicAddressKind" },
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(site.ConnectionId, json.GetProperty("connectionId").GetGuid());
        var jsonOrg = json.GetProperty("org");
        Assert.Equal(new[] { "displayName", "id", "slug" }, jsonOrg.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            (org.Id, "quokka360", "Quokka360"),
            (jsonOrg.GetProperty("id").GetGuid(), jsonOrg.GetProperty("slug").GetString(), jsonOrg.GetProperty("displayName").GetString()));
        Assert.True(json.GetProperty("embedEnabled").GetBoolean());
        Assert.Equal($"https://{OwnDomain}", json.GetProperty("publicAddress").GetString());
        Assert.Equal("customDomain", json.GetProperty("publicAddressKind").GetString());

        var consoleAnswer = await _harness.Console(org.Id).GetDomain(org.Id, CancellationToken.None);
        var consoleDomain = Assert.IsType<OrgDomainConfigDto>(Assert.IsType<OkObjectResult>(consoleAnswer.Result).Value);
        Assert.Equal(SiteApiHarness.Serialize(consoleDomain), json.GetProperty("domain").GetRawText());
        Assert.Equal("Verified", json.GetProperty("domain").GetProperty("domainVerificationStatus").GetString());
        Assert.Equal(OwnDomain, json.GetProperty("domain").GetProperty("customDomain").GetString());
    }

    [Fact]
    public async Task GetAccount_OrgWithoutADomain_IsTheCasazenAddress_WithTheSlugToKeep()
    {
        var org = _harness.SeedOrg("altra-agenzia", "Altra Agenzia");

        var account = AccountOf(await _harness.Plugin(SiteApiHarness.Site(org.Id)).GetAccount(CancellationToken.None));

        Assert.Equal((SiteAccountDto.CasazenKind, SiteApiHarness.CasazenAddress), (account.PublicAddressKind, account.PublicAddress));
        Assert.Equal("altra-agenzia", account.Org.Slug);
        Assert.Null(account.Domain.CustomDomain);
    }

    [Fact]
    public async Task GetAccount_WhileTheAddressOfCasazenIsNotConfigured_Is503_NeverAWrongAddress()
    {
        var accounts = new Mock<ISiteAccountBuilder>();
        accounts.Setup(a => a.BuildAsync(It.IsAny<SiteConnectionPrincipal>(), It.IsAny<OrgDomainConfig?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SiteAccountResult(SiteAccountOutcome.PublicAddressNotConfigured, null));
        var controller = new SiteApiController(Mock.Of<ISiteConnectionService>(), accounts.Object, _harness.OrgDomain, _harness.Localizer)
        {
            ControllerContext = _harness.Context(SiteApiHarness.SitePrincipal(SiteApiHarness.Site(Guid.NewGuid()))),
        };

        var answer = await controller.GetAccount(CancellationToken.None);

        Assert.Equal((503, "site_public_address_not_configured"), SiteApiHarness.Refusal(answer.Result));
    }

    [Fact]
    public async Task SetDomain_AValidDomain_IsStoredAsTheConsoleDoes_AndTheAnswerHasTheRecordsToCreate()
    {
        var org = _harness.SeedOrg("quokka360", "Quokka360");

        var answer = await _harness.Plugin(SiteApiHarness.Site(org.Id)).SetDomain(new SetSiteDomainRequest { CustomDomain = OwnDomain }, CancellationToken.None);

        var account = AccountOf(answer);
        var stored = await _harness.ReadAsync(org.Id);
        Assert.Equal((PublicHostMode.CustomDomain, OwnDomain, DomainVerificationStatus.Pending), (stored.PublicHostMode, stored.CustomDomain, stored.DomainVerificationStatus));
        Assert.False(string.IsNullOrEmpty(stored.DomainVerificationToken));

        var instructions = Assert.IsType<DnsInstructionsDto>(account.Domain.DnsInstructions);
        Assert.Equal(OwnDomain, instructions.CnameHost);
        Assert.EndsWith($".{OwnDomain}", instructions.TxtHost, StringComparison.Ordinal);
        Assert.Equal(stored.DomainVerificationToken, instructions.TxtValue);

        var console = Assert.IsType<OrgDomainConfigDto>(Assert.IsType<OkObjectResult>((await _harness.Console(org.Id).GetDomain(org.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(SiteApiHarness.Serialize(console), SiteApiHarness.Serialize(account.Domain));
        Assert.Equal((SiteAccountDto.CasazenKind, SiteApiHarness.CasazenAddress), (account.PublicAddressKind, account.PublicAddress));
    }

    [Theory]
    [InlineData("invalid", 400, "org_domain_invalid")]
    [InlineData("in-use", 409, "org_domain_in_use")]
    [InlineData("plan", 403, "plan_required")]
    public async Task SetDomain_ARefusal_HasTheStatusAndTheCodeTheConsoleGivesTheSameRequest(string scenario, int status, string code)
    {
        var other = _harness.SeedOrgWithDomain("altra-agenzia", "Altra Agenzia", OtherDomain);
        var org = _harness.SeedOrg("quokka360", "Quokka360", paid: scenario != "plan");
        var domain = scenario switch
        {
            "invalid" => "not a domain",
            "in-use" => OtherDomain,
            _ => OwnDomain,
        };

        var plugin = await _harness.Plugin(SiteApiHarness.Site(org.Id)).SetDomain(new SetSiteDomainRequest { CustomDomain = domain }, CancellationToken.None);
        var console = await _harness.Console(org.Id)
            .SetDomain(org.Id, new SetOrgDomainRequest { HostMode = PublicHostMode.CustomDomain, CustomDomain = domain }, CancellationToken.None);

        Assert.Equal((status, code), SiteApiHarness.Refusal(plugin.Result));
        Assert.Equal(SiteApiHarness.Refusal(console.Result), SiteApiHarness.Refusal(plugin.Result));
        if (scenario == "plan")
            Assert.Equal("Pro", SiteApiHarness.ToJson(Assert.IsType<ObjectResult>(plugin.Result).Value).GetProperty("requiredPlan").GetString());

        Assert.Null((await _harness.ReadAsync(org.Id)).CustomDomain);
        Assert.Equal(OtherDomain, (await _harness.ReadAsync(other.Id)).CustomDomain);
    }

    [Fact]
    public async Task SetDomain_Null_TakesTheOrgBackToTheCasazenAddress_AndAMissingMemberChangesNothing()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);
        var plugin = _harness.Plugin(SiteApiHarness.Site(org.Id));

        var missing = JsonSerializer.Deserialize<SetSiteDomainRequest>("{}", Web)!;
        Assert.False(missing.CustomDomainSpecified);
        var refused = await plugin.SetDomain(missing, CancellationToken.None);
        Assert.Equal((400, "org_domain_invalid"), SiteApiHarness.Refusal(refused.Result));
        Assert.Equal(OwnDomain, (await _harness.ReadAsync(org.Id)).CustomDomain);

        var explicitNull = JsonSerializer.Deserialize<SetSiteDomainRequest>("""{ "customDomain": null }""", Web)!;
        Assert.True(explicitNull.CustomDomainSpecified);
        var answer = await plugin.SetDomain(explicitNull, CancellationToken.None);

        var account = AccountOf(answer);
        var stored = await _harness.ReadAsync(org.Id);
        Assert.Equal(PublicHostMode.CasazenPath, stored.PublicHostMode);
        Assert.Null(stored.CustomDomain);
        Assert.Equal(PublicHostMode.CasazenPath, account.Domain.PublicHostMode);
        Assert.Null(account.Domain.CustomDomain);
        Assert.Equal((SiteAccountDto.CasazenKind, SiteApiHarness.CasazenAddress), (account.PublicAddressKind, account.PublicAddress));
    }

    [Fact]
    public async Task SetDomain_AnEmptyName_IsNotNull_ItIsAnInvalidDomain()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);

        var answer = await _harness.Plugin(SiteApiHarness.Site(org.Id)).SetDomain(new SetSiteDomainRequest { CustomDomain = " " }, CancellationToken.None);

        Assert.Equal((400, "org_domain_invalid"), SiteApiHarness.Refusal(answer.Result));
        Assert.Equal(OwnDomain, (await _harness.ReadAsync(org.Id)).CustomDomain);
    }

    [Fact]
    public async Task VerifyDomain_AnswersTheCheckAsTheConsoleDoes_AndTheAccountAsItIsAfterIt()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain, status: DomainVerificationStatus.Pending);
        _harness.Verification.Setup(v => v.VerifyAsync(It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity tracked, CancellationToken _) =>
            {
                tracked.DomainVerificationStatus = DomainVerificationStatus.Verified;
                _harness.Db.SaveChanges();
                return new DomainVerificationResult(DomainVerificationStatus.Verified, tracked.CustomDomain!, _harness.Clock.GetUtcNow().UtcDateTime, null);
            });

        var answer = await _harness.Plugin(SiteApiHarness.Site(org.Id)).VerifyDomain(CancellationToken.None);

        var response = Assert.IsType<SiteDomainVerifyResponse>(Assert.IsType<OkObjectResult>(answer.Result).Value);
        Assert.Equal((DomainVerificationStatus.Verified, OwnDomain, (string?)null), (response.Verify.DomainVerificationStatus, response.Verify.CustomDomain, response.Verify.Detail));
        Assert.Equal(DomainVerificationStatus.Verified, response.Account.Domain.DomainVerificationStatus);
        Assert.Equal((SiteAccountDto.CustomDomainKind, $"https://{OwnDomain}"), (response.Account.PublicAddressKind, response.Account.PublicAddress));
    }

    [Fact]
    public async Task VerifyDomain_ADomainThatIsNotThereYet_HasTheVerifyObjectOfTheConsole()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain, status: DomainVerificationStatus.Pending);
        _harness.Verification.Setup(v => v.VerifyAsync(It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainVerificationResult(
                DomainVerificationStatus.Pending,
                OwnDomain,
                _harness.Clock.GetUtcNow().UtcDateTime,
                DomainIssues.DnsNotPointing));

        var plugin = await _harness.Plugin(SiteApiHarness.Site(org.Id)).VerifyDomain(CancellationToken.None);
        var console = await _harness.Console(org.Id).VerifyDomain(org.Id, CancellationToken.None);

        var pluginVerify = Assert.IsType<SiteDomainVerifyResponse>(Assert.IsType<OkObjectResult>(plugin.Result).Value).Verify;
        var consoleVerify = Assert.IsType<OrgDomainVerifyResultDto>(Assert.IsType<OkObjectResult>(console.Result).Value);
        Assert.Equal(SiteApiHarness.Serialize(consoleVerify), SiteApiHarness.Serialize(pluginVerify));
        Assert.Equal(DomainIssues.DnsNotPointing, pluginVerify.Detail);
        Assert.Equal(DomainVerificationStatus.Pending, pluginVerify.DomainVerificationStatus);
    }

    [Theory]
    [InlineData("no-domain", 400, "org_domain_not_configured")]
    [InlineData("plan", 403, "plan_required")]
    public async Task VerifyDomain_ARefusal_HasTheStatusAndTheCodeTheConsoleGives(string scenario, int status, string code)
    {
        var org = scenario == "plan"
            ? _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain, paid: false, status: DomainVerificationStatus.Pending)
            : _harness.SeedOrg("quokka360", "Quokka360");

        var plugin = await _harness.Plugin(SiteApiHarness.Site(org.Id)).VerifyDomain(CancellationToken.None);
        var console = await _harness.Console(org.Id).VerifyDomain(org.Id, CancellationToken.None);

        Assert.Equal((status, code), SiteApiHarness.Refusal(plugin.Result));
        Assert.Equal(SiteApiHarness.Refusal(console.Result), SiteApiHarness.Refusal(plugin.Result));
        _harness.Verification.Verify(v => v.VerifyAsync(It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetSurfaces_ChangesTheThreeChoices_AndTheAnswerCarriesThem()
    {
        var org = _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain);

        var answer = await _harness.Plugin(SiteApiHarness.Site(org.Id)).SetSurfaces(
            new SetOrgDomainSurfacesRequest { GuestAreaOnOwnDomain = true, OwnerPortalOnOwnDomain = false, UseOwnDomainForLinks = true },
            CancellationToken.None);

        var account = AccountOf(answer);
        var surfaces = Assert.IsType<OrgDomainSurfacesDto>(account.Domain.Surfaces);
        Assert.Equal((true, false, true), (surfaces.GuestAreaOnOwnDomain, surfaces.OwnerPortalOnOwnDomain, surfaces.UseOwnDomainForLinks));
        Assert.True(surfaces.Effective.GuestArea);
        Assert.False(surfaces.Effective.OwnerPortal);
        var stored = await _harness.ReadAsync(org.Id);
        Assert.Equal((true, false, true), (stored.GuestAreaOnOwnDomain, stored.OwnerPortalOnOwnDomain, stored.UseOwnDomainForLinks));

        var again = AccountOf(await _harness.Plugin(SiteApiHarness.Site(org.Id)).SetSurfaces(
            new SetOrgDomainSurfacesRequest { GuestAreaOnOwnDomain = false, OwnerPortalOnOwnDomain = false, UseOwnDomainForLinks = false },
            CancellationToken.None));
        var cleared = Assert.IsType<OrgDomainSurfacesDto>(again.Domain.Surfaces);
        Assert.Equal((false, false, false), (cleared.GuestAreaOnOwnDomain, cleared.OwnerPortalOnOwnDomain, cleared.UseOwnDomainForLinks));
    }

    [Theory]
    [InlineData("no-domain", 400, "org_domain_not_configured")]
    [InlineData("plan", 403, "plan_required")]
    public async Task SetSurfaces_ARefusal_HasTheStatusAndTheCodeTheConsoleGives(string scenario, int status, string code)
    {
        var org = scenario == "plan"
            ? _harness.SeedOrgWithDomain("quokka360", "Quokka360", OwnDomain, paid: false)
            : _harness.SeedOrg("quokka360", "Quokka360");
        var request = new SetOrgDomainSurfacesRequest { GuestAreaOnOwnDomain = true, OwnerPortalOnOwnDomain = true, UseOwnDomainForLinks = true };

        var plugin = await _harness.Plugin(SiteApiHarness.Site(org.Id)).SetSurfaces(request, CancellationToken.None);
        var console = await _harness.Console(org.Id).SetSurfaces(org.Id, request, CancellationToken.None);

        Assert.Equal((status, code), SiteApiHarness.Refusal(plugin.Result));
        Assert.Equal(SiteApiHarness.Refusal(console.Result), SiteApiHarness.Refusal(plugin.Result));
        Assert.False((await _harness.ReadAsync(org.Id)).GuestAreaOnOwnDomain);
    }

    [Fact]
    public async Task Isolation_TheCredentialOfOrgA_NeverReadsOrChangesOrgB_AndABodyCarryingBsIdIsIgnored()
    {
        var orgA = _harness.SeedOrg("quokka360", "Quokka360");
        var orgB = _harness.SeedOrgWithDomain("altra-agenzia", "Altra Agenzia", OtherDomain);
        var pluginA = _harness.Plugin(SiteApiHarness.Site(orgA.Id));
        var stateOfB = StateOf(await _harness.ReadAsync(orgB.Id));

        var account = AccountOf(await pluginA.GetAccount(CancellationToken.None));
        Assert.Equal(orgA.Id, account.Org.Id);
        Assert.Equal(orgA.Id, account.Domain.OrgId);
        Assert.DoesNotContain("altra", SiteApiHarness.Serialize(account), StringComparison.OrdinalIgnoreCase);

        var domainBody = $$"""{ "customDomain": "{{OwnDomain}}", "orgId": "{{orgB.Id}}", "OrgId": "{{orgB.Id}}", "org": { "id": "{{orgB.Id}}" } }""";
        var changed = AccountOf(await pluginA.SetDomain(JsonSerializer.Deserialize<SetSiteDomainRequest>(domainBody, Web)!, CancellationToken.None));
        Assert.Equal(orgA.Id, changed.Domain.OrgId);
        Assert.Equal(OwnDomain, (await _harness.ReadAsync(orgA.Id)).CustomDomain);

        var surfacesBody = $$"""{ "guestAreaOnOwnDomain": true, "ownerPortalOnOwnDomain": true, "useOwnDomainForLinks": true, "orgId": "{{orgB.Id}}" }""";
        var surfaces = AccountOf(await pluginA.SetSurfaces(JsonSerializer.Deserialize<SetOrgDomainSurfacesRequest>(surfacesBody, Web)!, CancellationToken.None));
        Assert.Equal(orgA.Id, surfaces.Domain.OrgId);
        Assert.True((await _harness.ReadAsync(orgA.Id)).GuestAreaOnOwnDomain);

        var taken = await pluginA.SetDomain(new SetSiteDomainRequest { CustomDomain = OtherDomain }, CancellationToken.None);
        Assert.Equal((409, "org_domain_in_use"), SiteApiHarness.Refusal(taken.Result));

        _harness.Verification.Setup(v => v.VerifyAsync(It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity checkedOrg, CancellationToken _) =>
                new DomainVerificationResult(DomainVerificationStatus.Pending, checkedOrg.CustomDomain!, _harness.Clock.GetUtcNow().UtcDateTime, DomainIssues.DnsNotPointing));
        var verify = Assert.IsType<SiteDomainVerifyResponse>(Assert.IsType<OkObjectResult>((await pluginA.VerifyDomain(CancellationToken.None)).Result).Value);
        Assert.Equal(OwnDomain, verify.Verify.CustomDomain);
        _harness.Verification.Verify(v => v.VerifyAsync(It.Is<OrgEntity>(o => o.Id == orgB.Id), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal(stateOfB, StateOf(await _harness.ReadAsync(orgB.Id)));
    }

    [Fact]
    public async Task EveryAction_ForAPersonAndNotASite_IsUnauthorized_AndNothingIsCalled()
    {
        var services = new Mock<IOrgDomainService>(MockBehavior.Strict);
        var accounts = new Mock<ISiteAccountBuilder>(MockBehavior.Strict);
        var person = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|someone"), new Claim(ClaimTypes.Role, "PropertyOwner")], "Bearer"));
        var controller = new SiteApiController(Mock.Of<ISiteConnectionService>(MockBehavior.Strict), accounts.Object, services.Object, _harness.Localizer)
        {
            ControllerContext = _harness.Context(person),
        };

        Assert.IsType<UnauthorizedResult>((await controller.GetAccount(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.SetDomain(new SetSiteDomainRequest { CustomDomain = OwnDomain }, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.VerifyDomain(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(
            (await controller.SetSurfaces(new SetOrgDomainSurfacesRequest { GuestAreaOnOwnDomain = true, OwnerPortalOnOwnDomain = true, UseOwnDomainForLinks = true }, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(await controller.Disconnect(CancellationToken.None));
    }

    [Fact]
    public void Actions_ThePathsAndVerbsOfThePlan_TheCommercialGateOnAllButTheDisconnection_TheVerifyLimit_TheSurfacesFlag()
    {
        var actions = typeof(SiteApiController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .ToDictionary(m => m.Name, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "Disconnect", "GetAccount", "SetDomain", "SetSurfaces", "VerifyDomain" },
            actions.Keys.Order(StringComparer.Ordinal));

        (string Name, string Verb, string Template)[] contract =
        [
            (nameof(SiteApiController.GetAccount), "GET", "account"),
            (nameof(SiteApiController.SetDomain), "PUT", "domain"),
            (nameof(SiteApiController.VerifyDomain), "POST", "domain/verify"),
            (nameof(SiteApiController.SetSurfaces), "PUT", "domain/surfaces"),
            (nameof(SiteApiController.Disconnect), "DELETE", "connection"),
        ];
        foreach (var (name, verb, template) in contract)
        {
            var method = Assert.Single(actions[name].GetCustomAttributes<HttpMethodAttribute>(inherit: true));
            Assert.Equal((verb, template), (Assert.Single(method.HttpMethods), method.Template));
        }

        foreach (var (name, action) in actions)
            Assert.Equal(name != nameof(SiteApiController.Disconnect), action.GetCustomAttribute<SiteDomainAccessAttribute>() is not null);

        Assert.Equal(
            RateLimitPolicies.SiteDomainVerify,
            actions[nameof(SiteApiController.VerifyDomain)].GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.All(
            actions.Where(a => a.Key != nameof(SiteApiController.VerifyDomain)).Select(a => a.Value),
            action => Assert.Null(action.GetCustomAttribute<EnableRateLimitingAttribute>()));

        Assert.Equal(
            new[] { FeatureFlags.WhiteLabelHost },
            actions[nameof(SiteApiController.SetSurfaces)].GetCustomAttributes<FeatureGateAttribute>().Select(g => g.Flag));
        Assert.All(
            actions.Where(a => a.Key != nameof(SiteApiController.SetSurfaces)).Select(a => a.Value),
            action => Assert.Empty(action.GetCustomAttributes<FeatureGateAttribute>()));
    }

    private static SiteAccountDto AccountOf(ActionResult<SiteAccountDto> answer) =>
        Assert.IsType<SiteAccountDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);

    /// <summary>What an org stores of its domain and of its surfaces: it must not move when another org's credential acts.</summary>
    private static string StateOf(OrgEntity org) => SiteApiHarness.Serialize(new
    {
        org.PublicHostMode,
        org.Subdomain,
        org.CustomDomain,
        org.DomainVerificationStatus,
        org.DomainVerificationToken,
        org.DomainStatusDetail,
        org.GuestAreaOnOwnDomain,
        org.OwnerPortalOnOwnDomain,
        org.UseOwnDomainForLinks,
        org.OwnerLoginHostState,
        org.UpdatedAt,
    });
}
