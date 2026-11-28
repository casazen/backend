using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

internal sealed class SiteApiHarness : IDisposable
{
    public const string CasazenAddress = EmailTestHelpers.PublicSiteBaseUrl;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HashSet<Guid> _paid = [];
    private readonly IOptions<PublicHostOptions> _hostOptions = Options.Create(new PublicHostOptions { BaseDomain = "sites.example.test" });
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();

    public SiteApiHarness(params string[] enabledFlags)
    {
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Flags = new TestFeatureFlags(enabledFlags);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        Localizer = _services.GetRequiredService<IStringLocalizer<SharedResources>>();

        Entitlements.Setup(e => e.CanUseCustomDomainAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid orgId, CancellationToken _) => _paid.Contains(orgId));
        Entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>()))
            .Returns((OrgEntity org) => _paid.Contains(org.Id) ? PlanTier.Pro : PlanTier.Starter);

        var links = EmailTestHelpers.Links();
        SiteUrls = new PublicOrgSiteUrls(links, _hostOptions, Entitlements.Object);
        OrgDomain = new OrgDomainService(
            Db,
            Entitlements.Object,
            Verification.Object,
            Resolver.Object,
            _hostOptions,
            links,
            SiteUrls,
            Mock.Of<IVercelDomainsClient>(),
            Clock,
            Flags);
        Accounts = new SiteAccountBuilder(Db, OrgDomain, SiteUrls, Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = CasazenAddress }), Flags);
    }

    public AppDbContext Db { get; }

    public TestFeatureFlags Flags { get; }

    public FakeTimeProvider Clock { get; }

    public IStringLocalizer<SharedResources> Localizer { get; }

    public Mock<IEntitlementService> Entitlements { get; } = new();

    public Mock<IDomainVerificationService> Verification { get; } = new();

    public Mock<IPublicHostResolver> Resolver { get; } = new();

    public PublicOrgSiteUrls SiteUrls { get; }

    public OrgDomainService OrgDomain { get; }

    public SiteAccountBuilder Accounts { get; }

    public void Dispose()
    {
        Db.Dispose();
        _services.Dispose();
    }

    public OrgEntity SeedOrg(string slug, string name, bool paid = true, Action<OrgEntity>? configure = null)
    {
        var org = new OrgEntity { Name = name, DisplayName = name, Slug = slug, ContactEmail = $"info@{slug}.example", IsActive = true };
        configure?.Invoke(org);
        Db.Orgs.Add(org);
        Db.SaveChanges();
        SetPaid(org.Id, paid);
        return org;
    }

    public OrgEntity SeedOrgWithDomain(
        string slug,
        string name,
        string domain,
        bool paid = true,
        DomainVerificationStatus status = DomainVerificationStatus.Verified) =>
        SeedOrg(
            slug,
            name,
            paid,
            org =>
            {
                org.PublicHostMode = PublicHostMode.CustomDomain;
                org.CustomDomain = domain;
                org.DomainVerificationStatus = status;
                org.DomainVerificationToken = "verify-token";
            });

    public void SetPaid(Guid orgId, bool paid)
    {
        if (paid)
            _paid.Add(orgId);
        else
            _paid.Remove(orgId);
    }

    public async Task<OrgEntity> ReadAsync(Guid orgId) => await Db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);

    public static SiteConnectionPrincipal Site(Guid orgId) => new(Guid.NewGuid(), orgId);

    public SiteApiController Plugin(SiteConnectionPrincipal site) => PluginAs(SitePrincipal(site));

    public SiteApiController PluginAs(ClaimsPrincipal user) =>
        new(Mock.Of<ISiteConnectionService>(), Accounts, OrgDomain, Localizer) { ControllerContext = Context(user) };

    public OrgDomainController Console(Guid orgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|billing-admin"), new Claim(ClaimTypes.Role, "PropertyOwner")], "Bearer"));
        return new OrgDomainController(resolver.Object, OrgDomain, Localizer) { ControllerContext = Context(admin) };
    }

    public static ClaimsPrincipal SitePrincipal(SiteConnectionPrincipal site) =>
        new(new ClaimsIdentity(
            [
                new Claim(SiteTokenDefaults.ConnectionIdClaim, site.ConnectionId.ToString("D")),
                new Claim(SiteTokenDefaults.OrgIdClaim, site.OrgId.ToString("D")),
                new Claim(SiteTokenDefaults.ScopeClaim, SiteTokenDefaults.DomainScope),
            ],
            SiteTokenDefaults.AuthenticationScheme));

    public ControllerContext Context(ClaimsPrincipal user) => new()
    {
        HttpContext = new DefaultHttpContext { User = user, RequestServices = _services },
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Json);

    public static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value, Json);

    public static (int Status, string? Code) Refusal(IActionResult? result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        var code = objectResult.Value is ProblemDetails problem
            ? problem.Extensions["code"]?.ToString()
            : ToJson(objectResult.Value).GetProperty("code").GetString();
        return (objectResult.StatusCode ?? 0, code);
    }
}
