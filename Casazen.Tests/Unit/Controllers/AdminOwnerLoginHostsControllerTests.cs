using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E40/F01/US02/T01 (NF-141): the queue of the owner sign-ins and the hand of a platform administrator while <c>Auth0HostSync</c> is off. Only a platform
/// administrator sets the state; Ready only for a verified custom domain the org pays for; nothing calls Auth0; the org's administrators are told.
/// </summary>
public sealed class AdminOwnerLoginHostsControllerTests : IDisposable
{
    private const string Domain = "prenota.agenzia-esempio.it";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IAuth0HostRegistrar> _registrar = new(MockBehavior.Strict);
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly Mock<IPublicHostResolver> _resolver = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task PutState_NonAdmin_Forbidden()
    {
        // Every action of the controller is behind AdminOnly and nothing else, and behind the flag of the surfaces.
        var controller = typeof(AdminOwnerLoginHostsController);
        Assert.Equal([CasazenPolicies.AdminOnly], controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Equal([FeatureFlags.WhiteLabelHost], controller.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag));
        var routed = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .ToList();
        Assert.Equal(["List", "SetState"], routed.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.All(routed, action =>
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        });

        // The registered policy refuses an agency (its owner included), a supplier and a guest; it lets the platform administrator through.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.AdminOnly);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        foreach (var role in new[] { "PropertyOwner", "LongTermLandlord", "PropertyManager", "Staff", "Supplier", "Guest" })
        {
            var asHost = new AuthorizationHandlerContext(policy.Requirements, Principal(role), resource: null);
            await ((IAuthorizationHandler)requirement).HandleAsync(asHost);
            Assert.False(asHost.HasSucceeded, $"A caller with the role {role} passed the policy of the owner sign-in states.");
        }

        var admin = new AuthorizationHandlerContext(policy.Requirements, Principal("Admin"), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(admin);
        Assert.True(admin.HasSucceeded);
    }

    [Theory]
    [InlineData(DomainVerificationStatus.Pending, PlanTier.Pro)]
    [InlineData(DomainVerificationStatus.Failed, PlanTier.Pro)]
    [InlineData(DomainVerificationStatus.Verified, PlanTier.Starter)]
    public async Task PutState_ReadyWithoutVerifiedDomain_422(DomainVerificationStatus domainStatus, PlanTier tier)
    {
        var org = await SeedAsync(OwnerLoginHostState.Pending, domainStatus);

        var result = await Controller(tier).SetState(org.Id, new OwnerLoginStateDto { State = OwnerLoginHostState.Ready }, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        Assert.Equal(
            OrgDomainController.DomainNotVerifiedCode,
            Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions[ApiProblemDetails.CodeExtension]);
        Assert.Equal(OwnerLoginHostState.Pending, await StoredStateAsync(org.Id));
        _push.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PutState_UnknownOrg_404()
    {
        var result = await Controller().SetState(Guid.NewGuid(), new OwnerLoginStateDto { State = OwnerLoginHostState.Failed }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task PutState_ReadyOnAVerifiedDomain_AnswersOnlyTheState_TellsTheOrgsAdministrators_AndCallsNoAuth0()
    {
        var org = await SeedAsync(OwnerLoginHostState.Pending, DomainVerificationStatus.Verified);

        var result = await Controller().SetState(org.Id, new OwnerLoginStateDto { State = OwnerLoginHostState.Ready }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(OwnerLoginHostState.Ready, Assert.IsType<OwnerLoginStateDto>(ok.Value).State);
        Assert.Equal(OwnerLoginHostState.Ready, await StoredStateAsync(org.Id));
        _resolver.Verify(r => r.InvalidateCacheForHost(Domain), Times.Once);
        _push.Verify(p => p.Enqueue(
            PushDeliveryKeys.OwnerLoginReady(org.Id, _clock.GetUtcNow().UtcDateTime),
            PushAudience.OrgAdmins(org.Id),
            It.Is<PushNotificationPayload>(payload => payload.Type == PushTypes.OwnerLoginReady)), Times.Once);
        _registrar.VerifyNoOtherCalls();

        // The same state again changes nothing and tells nobody again.
        await Controller().SetState(org.Id, new OwnerLoginStateDto { State = OwnerLoginHostState.Ready }, CancellationToken.None);
        _push.Verify(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()), Times.Once);
    }

    [Fact]
    public async Task List_IsTheQueueOfTheGivenState_PendingByDefault()
    {
        var pending = await SeedAsync(OwnerLoginHostState.Pending, DomainVerificationStatus.Verified, "Zeta Agenzia", "prenota.zeta.example");
        var pendingNotVerified = await SeedAsync(OwnerLoginHostState.Pending, DomainVerificationStatus.Pending, "Alfa Agenzia", "prenota.alfa.example");
        await SeedAsync(OwnerLoginHostState.Ready, DomainVerificationStatus.Verified, "Beta Agenzia", "prenota.beta.example");
        await SeedAsync(OwnerLoginHostState.Pending, DomainVerificationStatus.Verified, "Inattiva", "prenota.inattiva.example", isActive: false);

        var queue = Assert.IsType<OkObjectResult>((await Controller().List(state: null, CancellationToken.None)).Result);

        var items = Assert.IsAssignableFrom<IReadOnlyList<OwnerLoginHostDto>>(queue.Value);
        Assert.Equal([pendingNotVerified.Id, pending.Id], items.Select(i => i.OrgId));
        Assert.Equal(["Alfa Agenzia", "Zeta Agenzia"], items.Select(i => i.DisplayName));
        Assert.Equal(["prenota.alfa.example", "prenota.zeta.example"], items.Select(i => i.Domain));
        Assert.All(items, i => Assert.Equal(OwnerLoginHostState.Pending, i.State));
        Assert.Equal([false, true], items.Select(i => i.DomainVerified));

        var ready = Assert.IsType<OkObjectResult>((await Controller().List("ready", CancellationToken.None)).Result);
        Assert.Equal(["Beta Agenzia"], Assert.IsAssignableFrom<IReadOnlyList<OwnerLoginHostDto>>(ready.Value).Select(i => i.DisplayName));

        var invalid = Assert.IsType<ObjectResult>((await Controller().List("7", CancellationToken.None)).Result);
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private AdminOwnerLoginHostsController Controller(PlanTier tier = PlanTier.Pro)
    {
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.CanUseCustomDomainAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(tier != PlanTier.Starter);
        entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns(tier);
        var hostOptions = Options.Create(new PublicHostOptions { BaseDomain = "sites.example.test" });
        var links = EmailTestHelpers.Links();
        var flags = new TestFeatureFlags(FeatureFlags.WhiteLabelHost);
        var service = new OrgDomainService(
            _db,
            entitlements.Object,
            Mock.Of<IDomainVerificationService>(),
            _resolver.Object,
            hostOptions,
            links,
            new PublicOrgSiteUrls(links, hostOptions, entitlements.Object),
            Mock.Of<IVercelDomainsClient>(),
            _clock,
            flags,
            new OwnerLoginHostSync(_db, _registrar.Object, flags, _push.Object, _resolver.Object, _clock, NullLogger<OwnerLoginHostSync>.Instance));

        return new AdminOwnerLoginHostsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = Principal("Admin"),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(role => new Claim(ClaimTypes.Role, role)).Append(new Claim("sub", "auth0|platform-admin")),
            authenticationType: "test"));

    private async Task<OwnerLoginHostState> StoredStateAsync(Guid orgId) =>
        (await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId)).OwnerLoginHostState;

    private async Task<OrgEntity> SeedAsync(
        OwnerLoginHostState state,
        DomainVerificationStatus domainStatus,
        string name = "Agenzia",
        string domain = Domain,
        bool isActive = true)
    {
        var org = new OrgEntity
        {
            Name = name,
            DisplayName = name,
            Slug = $"agenzia-{Guid.NewGuid():N}"[..20],
            ContactEmail = "info@agenzia.example",
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = domain,
            DomainVerificationStatus = domainStatus,
            DomainVerificationToken = "verify-token",
            PlanTier = PlanTier.Pro,
            OwnerPortalOnOwnDomain = true,
            OwnerLoginHostState = state,
            IsActive = isActive,
        };
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        return org;
    }
}
