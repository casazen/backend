using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

/// <summary>
/// S023 (PLG-BE-SITEAPI, decision of the coordinator after the review of S022): the domain operations of the plugin (<c>account</c>, <c>domain</c>, <c>verify</c>,
/// <c>surfaces</c>) need the module and the commercial standing the console's <c>OrgDomain</c> endpoints need, and the plugin's own disconnection never does. The
/// middleware of the console cannot gate a credential (it runs before the scheme of the plugin authenticates), so a resource filter applies the same rule
/// (<see cref="CommercialAccessRules"/>), and gives the same answer the console gives.
/// </summary>
public sealed class SiteDomainAccessFilterTests : IDisposable
{
    private const string Module = "direct-booking";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Module_IsTheOneOfTheConsolesDomainEndpoints_NotAModuleOfItsOwn()
    {
        Assert.Equal(Module, SiteDomainAccessFilter.Module);
        Assert.Equal(CommercialAccessMiddleware.ModuleForController("OrgDomain"), SiteDomainAccessFilter.Module);
    }

    [Fact]
    public async Task Filter_CommercialOrgWithoutTheModule_Answers403CommercialModuleRequired_AndTheActionDoesNotRun()
    {
        var orgId = SeedOrg("without-module");

        var (context, nextCalled) = await InvokeFilterAsync(orgId);

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("commercial_module_required", body.GetProperty("code").GetString());
        Assert.Equal(Module, body.GetProperty("module").GetString());
    }

    [Fact]
    public async Task Filter_CommercialOrgThatHasNotPaid_Answers403OnboardingRequired()
    {
        var orgId = SeedOrg("unpaid");

        var (context, nextCalled) = await InvokeFilterAsync(orgId);

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("onboarding_required", Assert.IsAssignableFrom<ProblemDetails>(result.Value).Extensions["code"]);
    }

    [Theory]
    [InlineData("with-module")]
    [InlineData("not-commercial")]
    public async Task Filter_AnOrgWithTheModule_OrOneThatDidNotSignUpCommercially_GoesOn(string state)
    {
        var orgId = SeedOrg(state);

        var (context, nextCalled) = await InvokeFilterAsync(orgId);

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Filter_ThePrincipalIsTheCredentialsOrg_NeverAnotherOnesModule()
    {
        // The org of the request is the org of the credential: the module of another org is no help.
        var withoutModule = SeedOrg("without-module");
        var withModule = SeedOrg("with-module");

        var (context, nextCalled) = await InvokeFilterAsync(withoutModule);
        var (otherContext, otherNextCalled) = await InvokeFilterAsync(withModule);

        Assert.False(nextCalled);
        Assert.NotNull(context.Result);
        Assert.True(otherNextCalled);
        Assert.Null(otherContext.Result);
    }

    /// <summary>
    /// «The answer the console endpoints give»: for every commercial state the middleware on <c>OrgDomainController</c> and the filter on the plugin's action refuse or
    /// pass together, with the same status, the same <c>code</c> and, for a module, the same module.
    /// </summary>
    [Theory]
    [InlineData("without-module")]
    [InlineData("unpaid")]
    [InlineData("with-module")]
    [InlineData("not-commercial")]
    public async Task TheConsolesMiddleware_AndThePluginsFilter_GiveTheSameAnswer(string state)
    {
        var orgId = SeedOrg(state);

        var (middlewareContext, middlewareNextCalled) = await InvokeMiddlewareAsync(orgId);
        var (filterContext, filterNextCalled) = await InvokeFilterAsync(orgId);

        Assert.Equal(middlewareNextCalled, filterNextCalled);
        if (middlewareNextCalled)
        {
            Assert.Null(filterContext.Result);
            return;
        }

        var (middlewareStatus, middlewareBody) = await ReadMiddlewareAnswerAsync(middlewareContext);
        var filterResult = Assert.IsType<ObjectResult>(filterContext.Result);
        var filterBody = JsonSerializer.SerializeToElement(filterResult.Value is ProblemDetails problem ? ProblemBody(problem) : filterResult.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(middlewareStatus, filterResult.StatusCode);
        Assert.Equal(middlewareBody.GetProperty("code").GetString(), filterBody.GetProperty("code").GetString());
        if (middlewareBody.TryGetProperty("module", out var module))
            Assert.Equal(module.GetString(), filterBody.GetProperty("module").GetString());
    }

    [Fact]
    public void Rules_AreTheOnesOfTheMiddleware_OnTheStateOfTheOrg()
    {
        Assert.Equal(CommercialAccessDecision.Allowed, CommercialAccessRules.Evaluate(null, Module));
        Assert.Equal(CommercialAccessDecision.Allowed, CommercialAccessRules.Evaluate(OrgOf("not-commercial"), Module));
        Assert.Equal(CommercialAccessDecision.OnboardingRequired, CommercialAccessRules.Evaluate(OrgOf("unpaid"), Module));
        Assert.Equal(CommercialAccessDecision.OnboardingRequired, CommercialAccessRules.Evaluate(OrgOf("unpaid"), module: null));
        Assert.Equal(CommercialAccessDecision.ModuleRequired, CommercialAccessRules.Evaluate(OrgOf("without-module"), Module));
        Assert.Equal(CommercialAccessDecision.Allowed, CommercialAccessRules.Evaluate(OrgOf("without-module"), module: null));
        Assert.Equal(CommercialAccessDecision.Allowed, CommercialAccessRules.Evaluate(OrgOf("with-module"), Module));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static OrgEntity OrgOf(string state)
    {
        var org = new OrgEntity
        {
            Name = "Quokka360",
            DisplayName = "Quokka360",
            Slug = "quokka-" + Guid.NewGuid().ToString("N")[..8],
            ContactEmail = "info@quokka.example",
        };

        switch (state)
        {
            case "not-commercial":
                break;

            case "unpaid":
                org.CommercialSignupJson = "{}";
                org.CommercialPaymentConfirmed = false;
                org.SubscriptionStatus = SubscriptionStatus.Incomplete;
                org.CommercialPaidAddonsJson = JsonSerializer.Serialize(new[] { Module });
                break;

            case "without-module":
                org.CommercialSignupJson = "{}";
                org.CommercialPaymentConfirmed = true;
                org.SubscriptionStatus = SubscriptionStatus.Active;
                org.CommercialPaidAddonsJson = "[]";
                break;

            case "with-module":
                org.CommercialSignupJson = "{}";
                org.CommercialPaymentConfirmed = true;
                org.SubscriptionStatus = SubscriptionStatus.Active;
                org.CommercialPaidAddonsJson = JsonSerializer.Serialize(new[] { Module });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state of the test.");
        }

        return org;
    }

    private Guid SeedOrg(string state)
    {
        var org = OrgOf(state);
        _db.Orgs.Add(org);
        _db.SaveChanges();
        return org.Id;
    }

    /// <summary>The filter, as a call of the connection of <paramref name="orgId"/> reaches it.</summary>
    private async Task<(ResourceExecutingContext Context, bool NextCalled)> InvokeFilterAsync(Guid orgId)
    {
        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = SiteApiHarness.SitePrincipal(new SiteConnectionPrincipal(Guid.NewGuid(), orgId)),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        var nextCalled = false;
        await new SiteDomainAccessFilter(_db).OnResourceExecutionAsync(
            context,
            () =>
            {
                nextCalled = true;
                return Task.FromResult(new ResourceExecutedContext(actionContext, new List<IFilterMetadata>()));
            });

        return (context, nextCalled);
    }

    /// <summary>The middleware of the console on <c>OrgDomainController.GetDomain</c>, for a signed-in billing administrator of <paramref name="orgId"/>.</summary>
    private async Task<(DefaultHttpContext Context, bool NextCalled)> InvokeMiddlewareAsync(Guid orgId)
    {
        var controller = typeof(OrgDomainController);
        var method = controller.GetMethod(nameof(OrgDomainController.GetDomain), BindingFlags.Public | BindingFlags.Instance)!;
        var descriptor = new ControllerActionDescriptor
        {
            ControllerName = "OrgDomain",
            ActionName = method.Name,
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = method,
        };
        var metadata = controller.GetCustomAttributes(inherit: true).Concat(method.GetCustomAttributes(inherit: true)).Append(descriptor);

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider() };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), method.Name));
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|billing-admin")], "Test"));

        var tenant = new Mock<IRequestTenantContext>();
        tenant.SetupGet(t => t.OrgId).Returns(orgId);

        var nextCalled = false;
        await new CommercialAccessMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }).InvokeAsync(context, tenant.Object, _db);
        return (context, nextCalled);
    }

    private static async Task<(int Status, JsonElement Body)> ReadMiddlewareAnswerAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var json = JsonDocument.Parse(text);
        return (context.Response.StatusCode, json.RootElement.Clone());
    }

    /// <summary>A problem as it goes on the wire: its extensions (the stable <c>code</c>) at the top.</summary>
    private static Dictionary<string, object?> ProblemBody(ProblemDetails problem) =>
        problem.Extensions.ToDictionary(extension => extension.Key, extension => (object?)extension.Value);
}
