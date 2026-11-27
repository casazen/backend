using System.Net;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

/// <summary>
/// S022 (PLG-BE-CONNECT, site integration plan §10.4, NF-204, AC 6 and 8): the scheme <c>SiteToken</c> reads only a credential of a connected site and
/// makes a principal with no person in it, the policy <c>SiteConnection</c> authenticates with that scheme only (a sign-in of a person is challenged: 401),
/// the JWT sign-in of the console leaves a credential alone (a console endpoint sees no sign-in: 401), the three controllers are behind <c>SiteEmbed</c>
/// (404 while it is off) with their rate limits, and the calls of the plugin count per credential.
/// </summary>
public class SiteTokenAuthenticationTests
{
    private static readonly Guid ConnectionId = Guid.Parse("8f0c2b1e-4a7d-4c55-9f2e-3b6a1d0e7c11");
    private static readonly Guid OrgId = Guid.Parse("2c9d4e6f-1a3b-4d5c-8e7f-0a1b2c3d4e5f");
    private static readonly string Credential = SiteConnectionTokens.NewCredential();

    [Fact]
    public async Task Scheme_AnActiveCredential_GivesTheConnectionItsOrgAndTheScope_AndTheRequestBelongsToThatOrg()
    {
        await using var provider = Services(out var connections);
        await using var scope = provider.CreateAsyncScope();
        var context = Request(scope.ServiceProvider, "Bearer " + Credential);

        var result = await context.AuthenticateAsync(SiteTokenDefaults.AuthenticationScheme);

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(new SiteConnectionPrincipal(ConnectionId, OrgId), principal.GetSiteConnection());
        Assert.Equal(SiteTokenDefaults.DomainScope, principal.FindFirstValue(SiteTokenDefaults.ScopeClaim));
        Assert.Null(principal.GetUserId());
        Assert.Empty(principal.GetRoles());
        Assert.Equal(OrgId, scope.ServiceProvider.GetRequiredService<IRequestTenantContext>().OrgId);
        connections.Verify(c => c.AuthenticateAsync(Credential, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhdXRoMHxzb21lb25lIn0.c2lnbmF0dXJl")]
    [InlineData("Test test")]
    [InlineData("Bearer gs1.a-guest-session")]
    [InlineData("Basic dXNlcjpzZWNyZXQ=")]
    public async Task Scheme_ASignInOfAPersonOrAnotherToken_IsNoResult_AndNoCredentialIsLookedUp(string? authorization)
    {
        await using var provider = Services(out var connections);
        await using var scope = provider.CreateAsyncScope();

        var result = await Request(scope.ServiceProvider, authorization).AuthenticateAsync(SiteTokenDefaults.AuthenticationScheme);

        Assert.True(result.None);
        connections.Verify(c => c.AuthenticateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(scope.ServiceProvider.GetRequiredService<IRequestTenantContext>().OrgId);
    }

    [Fact]
    public async Task Scheme_ARevokedUnknownOrMalformedCredential_Fails_WithoutEchoingIt()
    {
        await using var provider = Services(out _);
        var revoked = SiteConnectionTokens.NewCredential();

        foreach (var value in new[] { revoked, "czs_short" })
        {
            await using var scope = provider.CreateAsyncScope();
            var result = await Request(scope.ServiceProvider, "Bearer " + value).AuthenticateAsync(SiteTokenDefaults.AuthenticationScheme);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Failure);
            Assert.DoesNotContain(value, result.Failure.Message, StringComparison.Ordinal);
            Assert.Null(scope.ServiceProvider.GetRequiredService<IRequestTenantContext>().OrgId);
        }
    }

    [Fact]
    public async Task Scheme_ARequestThatBelongsToAnotherOrgAlready_Fails()
    {
        await using var provider = Services(out _);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IRequestTenantContext>().SetOrgId(Guid.NewGuid());

        var result = await Request(scope.ServiceProvider, "Bearer " + Credential).AuthenticateAsync(SiteTokenDefaults.AuthenticationScheme);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Policy_SiteConnection_AuthenticatesWithTheSiteTokenSchemeOnly_AndRequiresTheConnectionTheOrgAndTheDomainScope()
    {
        var policy = await RegisteredPolicyAsync(CasazenPolicies.SiteConnection);

        Assert.Equal([SiteTokenDefaults.AuthenticationScheme], policy.AuthenticationSchemes);
        Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
        var claims = policy.Requirements.OfType<ClaimsAuthorizationRequirement>().ToDictionary(r => r.ClaimType);
        string[] required = [SiteTokenDefaults.ConnectionIdClaim, SiteTokenDefaults.OrgIdClaim, SiteTokenDefaults.ScopeClaim];
        Assert.Equal(required.Order(StringComparer.Ordinal), claims.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([SiteTokenDefaults.DomainScope], claims[SiteTokenDefaults.ScopeClaim].AllowedValues);

        // The console's policy names no scheme: the default one (the JWT sign-in of people), never SiteToken.
        Assert.Empty((await RegisteredPolicyAsync(CasazenPolicies.OrgBillingAdmin)).AuthenticationSchemes);
    }

    [Fact]
    public async Task PolicyEvaluation_OnlyAnActiveCredentialPasses_ASignedInPersonIsChallenged()
    {
        var policy = await RegisteredPolicyAsync(CasazenPolicies.SiteConnection);
        await using var provider = Services(out _, withAuthorization: true);

        // A person signed in by the default scheme with its bearer JWT: the policy authenticates with SiteToken only, the person is dropped and challenged (401).
        await using (var scope = provider.CreateAsyncScope())
        {
            var person = Request(scope.ServiceProvider, "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhdXRoMHxzb21lb25lIn0.c2lnbmF0dXJl");
            person.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "auth0|someone"), new Claim(ClaimTypes.Role, "PropertyOwner")],
                JwtBearerDefaults.AuthenticationScheme));
            var evaluator = scope.ServiceProvider.GetRequiredService<IPolicyEvaluator>();

            var authentication = await evaluator.AuthenticateAsync(policy, person);
            var authorization = await evaluator.AuthorizeAsync(policy, authentication, person, resource: null);

            Assert.True(authorization.Challenged);
            Assert.False(person.User.Identity?.IsAuthenticated ?? false);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var site = Request(scope.ServiceProvider, "Bearer " + Credential);
            var evaluator = scope.ServiceProvider.GetRequiredService<IPolicyEvaluator>();

            var authentication = await evaluator.AuthenticateAsync(policy, site);

            Assert.True((await evaluator.AuthorizeAsync(policy, authentication, site, resource: null)).Succeeded);
            Assert.Equal(new SiteConnectionPrincipal(ConnectionId, OrgId), site.User.GetSiteConnection());
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var revoked = Request(scope.ServiceProvider, "Bearer " + SiteConnectionTokens.NewCredential());
            var evaluator = scope.ServiceProvider.GetRequiredService<IPolicyEvaluator>();

            var authentication = await evaluator.AuthenticateAsync(policy, revoked);

            Assert.True((await evaluator.AuthorizeAsync(policy, authentication, revoked, resource: null)).Challenged);
        }
    }

    [Fact]
    public async Task JwtBearer_LeavesASiteCredentialAlone_SoAConsoleEndpointSeesNoSignIn_AndSiteTokenIsNotTheDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Domain"] = "tenant.example.test",
                ["Auth0:Audience"] = "https://api.example.test",
            })
            .Build();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        services.AddCasazenAuthentication(configuration, environment.Object);
        services.AddSingleton(Mock.Of<ISiteConnectionService>(MockBehavior.Strict));
        await using var provider = services.BuildServiceProvider();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.NotNull(await schemes.GetSchemeAsync(SiteTokenDefaults.AuthenticationScheme));

        await using var scope = provider.CreateAsyncScope();
        var context = Request(scope.ServiceProvider, "Bearer " + Credential);

        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(result.None);
    }

    [Fact]
    public void Controllers_TheConsoleUsesTheDefaultSchemeAndTheBillingAdminPolicy_ThePluginOnlyTheSiteTokenScheme_TheExchangeIsAnonymous()
    {
        var console = typeof(SiteConnectionsController);
        var consoleAuthorize = Assert.Single(console.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(CasazenPolicies.OrgBillingAdmin, consoleAuthorize.Policy);
        Assert.Null(consoleAuthorize.AuthenticationSchemes);
        Assert.Equal("api/orgs/{orgId:guid}/site-connections", console.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Equal(["Authorize", "List", "Revoke"], Actions(console).Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            RateLimitPolicies.SiteConnectAuthorize,
            console.GetMethod(nameof(SiteConnectionsController.Authorize))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        var plugin = typeof(SiteApiController);
        var pluginAuthorize = Assert.Single(plugin.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(SiteTokenDefaults.AuthenticationScheme, pluginAuthorize.AuthenticationSchemes);
        Assert.Equal(CasazenPolicies.SiteConnection, pluginAuthorize.Policy);
        Assert.Equal("api/site/v1", plugin.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Equal(["Disconnect"], Actions(plugin).Select(a => a.Name));
        Assert.Equal(RateLimitPolicies.SiteApi, plugin.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        var exchange = typeof(PublicSiteConnectionsController);
        Assert.NotNull(exchange.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Empty(exchange.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal("api/public/site-connections", exchange.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Equal(["Token"], Actions(exchange).Select(a => a.Name));
        Assert.Equal(
            RateLimitPolicies.SiteConnectToken,
            exchange.GetMethod(nameof(PublicSiteConnectionsController.Token))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        foreach (var controller in new[] { console, plugin, exchange })
        {
            Assert.Equal([FeatureFlags.SiteEmbed], controller.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag));
            Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());
            Assert.All(Actions(controller), action =>
            {
                // No action changes the decision of its class.
                Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
                Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            });
        }
    }

    [Theory]
    [InlineData(typeof(SiteConnectionsController))]
    [InlineData(typeof(PublicSiteConnectionsController))]
    [InlineData(typeof(SiteApiController))]
    public async Task FeatureGate_SiteEmbedOff_EveryEndpointAnswers404BeforeAnythingElse_OnItRuns(Type controller)
    {
        foreach (var action in Actions(controller))
        {
            var metadata = controller.GetCustomAttributes(inherit: true).Concat(action.GetCustomAttributes(inherit: true));
            var flags = new TestFeatureFlags();
            var reached = false;
            var middleware = new FeatureGateMiddleware(
                _ =>
                {
                    reached = true;
                    return Task.CompletedTask;
                },
                flags,
                NullLogger<FeatureGateMiddleware>.Instance);

            var off = new DefaultHttpContext();
            off.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), action.Name));
            await middleware.InvokeAsync(off);
            Assert.Equal(StatusCodes.Status404NotFound, off.Response.StatusCode);
            Assert.False(reached, $"{controller.Name}.{action.Name} ran with SiteEmbed off.");

            flags.Set(FeatureFlags.SiteEmbed, true);
            var on = new DefaultHttpContext();
            on.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), action.Name));
            await middleware.InvokeAsync(on);
            Assert.True(reached, $"{controller.Name}.{action.Name} did not run with SiteEmbed on.");
        }
    }

    [Fact]
    public void RateLimits_HaveTheLimitsOfThePlan_AndTheCallsOfThePluginCountPerCredential()
    {
        var authorize = Policy(RateLimitPolicies.SiteConnectAuthorize);
        Assert.Equal((10, TimeSpan.FromMinutes(10), true, false), (authorize.DefaultPermitLimit, authorize.DefaultWindow, authorize.PartitionByUser, authorize.PartitionBySiteConnection));
        var token = Policy(RateLimitPolicies.SiteConnectToken);
        Assert.Equal((20, TimeSpan.FromMinutes(10), false, false), (token.DefaultPermitLimit, token.DefaultWindow, token.PartitionByUser, token.PartitionBySiteConnection));
        var api = Policy(RateLimitPolicies.SiteApi);
        Assert.Equal((120, TimeSpan.FromMinutes(1), false, true), (api.DefaultPermitLimit, api.DefaultWindow, api.PartitionByUser, api.PartitionBySiteConnection));

        // Two sites behind one hosting address have a budget each; a call with no credential counts by address.
        var siteA = SiteCall(Guid.NewGuid());
        var siteB = SiteCall(Guid.NewGuid());
        var keyA = RateLimitingServiceCollectionExtensions.GetPartitionKey(siteA, partitionByToken: false, partitionBySiteConnection: true);
        var keyB = RateLimitingServiceCollectionExtensions.GetPartitionKey(siteB, partitionByToken: false, partitionBySiteConnection: true);
        Assert.StartsWith("site:", keyA, StringComparison.Ordinal);
        Assert.NotEqual(keyA, keyB);
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        Assert.Equal("203.0.113.7", RateLimitingServiceCollectionExtensions.GetPartitionKey(anonymous, partitionByToken: false, partitionBySiteConnection: true));
        Assert.Equal("203.0.113.7", RateLimitingServiceCollectionExtensions.GetPartitionKey(siteA, partitionByToken: false));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static ServiceProvider Services(out Mock<ISiteConnectionService> connections, bool withAuthorization = false)
    {
        connections = new Mock<ISiteConnectionService>(MockBehavior.Strict);
        connections.Setup(c => c.AuthenticateAsync(Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SiteConnectionPrincipal(ConnectionId, OrgId));
        connections.Setup(c => c.AuthenticateAsync(It.Is<string?>(value => value != Credential), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteConnectionPrincipal?)null);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SiteTokenAuthenticationHandler>(SiteTokenDefaults.AuthenticationScheme, _ => { });
        if (withAuthorization)
            services.AddAuthorization();
        services.AddSingleton(connections.Object);
        services.AddScoped<IRequestTenantContext, RecordingTenant>();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Request(IServiceProvider services, string? authorization)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        return context;
    }

    private static DefaultHttpContext SiteCall(Guid connectionId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(SiteTokenDefaults.ConnectionIdClaim, connectionId.ToString("D")),
                    new Claim(SiteTokenDefaults.OrgIdClaim, OrgId.ToString("D")),
                    new Claim(SiteTokenDefaults.ScopeClaim, SiteTokenDefaults.DomainScope),
                ],
                SiteTokenDefaults.AuthenticationScheme)),
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        return context;
    }

    private static async Task<AuthorizationPolicy> RegisteredPolicyAsync(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(name)
            ?? throw new InvalidOperationException($"Policy {name} is not registered.");
    }

    private static RateLimitPolicyDefinition Policy(string name) =>
        RateLimitingServiceCollectionExtensions.Policies.Single(policy => policy.Name == name);

    private static List<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .ToList();

    /// <summary>The writer side of the tenant of a request, as <c>TenantContext</c> keeps it: from none to one org, never to another.</summary>
    private sealed class RecordingTenant : IRequestTenantContext
    {
        public Guid? OrgId { get; private set; }

        public bool FilterEnabled => OrgId is not null;

        public bool IsCallerInactive => false;

        public bool IsCallerOrgMemberDeactivated => false;

        public Task ResolveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void SetOrgId(Guid orgId)
        {
            if (OrgId is { } current && current != orgId)
                throw new InvalidOperationException("The request belongs to another org.");

            OrgId = orgId;
        }
    }
}
