using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Middleware;

/// <summary>
/// S022 (PLG-BE-CONNECT), review fix 1: the commercial module gates only a new connection of a site. An agency of a commercial sign-up without the add-on
/// <c>direct-booking</c> gets 403 <c>commercial_module_required</c> on <c>authorize</c>, but always lists and revokes its connected sites, and the plugin's own
/// disconnection (<c>DELETE api/site/v1/connection</c>) is never gated; with the add-on every console action passes.
/// </summary>
public sealed class CommercialAccessSiteConnectionsTests : IDisposable
{
    private const string Module = "direct-booking";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task InvokeAsync_AuthorizeWithoutTheModule_Returns403CommercialModuleRequiredAndDoesNotCallNext()
    {
        var orgId = SeedCommercialOrg(paidAddons: []);

        var (context, nextCalled) = await InvokeAsync(typeof(SiteConnectionsController), nameof(SiteConnectionsController.Authorize), orgId);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var body = await ReadAsync(context);
        Assert.Equal("commercial_module_required", body.GetProperty("code").GetString());
        Assert.Equal(Module, body.GetProperty("module").GetString());
    }

    [Theory]
    [InlineData(typeof(SiteConnectionsController), nameof(SiteConnectionsController.List))]
    [InlineData(typeof(SiteConnectionsController), nameof(SiteConnectionsController.Revoke))]
    [InlineData(typeof(SiteApiController), nameof(SiteApiController.Disconnect))]
    public async Task InvokeAsync_ListRevokeAndThePluginsDisconnectionWithoutTheModule_AreNotBlocked(Type controller, string action)
    {
        var orgId = SeedCommercialOrg(paidAddons: []);

        var (context, nextCalled) = await InvokeAsync(controller, action, orgId);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(nameof(SiteConnectionsController.Authorize))]
    [InlineData(nameof(SiteConnectionsController.List))]
    [InlineData(nameof(SiteConnectionsController.Revoke))]
    public async Task InvokeAsync_WithTheModule_EveryConsoleActionCallsNext(string action)
    {
        var orgId = SeedCommercialOrg(paidAddons: [Module]);

        var (context, nextCalled) = await InvokeAsync(typeof(SiteConnectionsController), action, orgId);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private Guid SeedCommercialOrg(string[] paidAddons)
    {
        var org = new OrgEntity
        {
            Name = "Quokka360",
            DisplayName = "Quokka360",
            Slug = "quokka-" + Guid.NewGuid().ToString("N")[..8],
            ContactEmail = "info@quokka.example",
            CommercialSignupJson = "{}",
            CommercialPaymentConfirmed = true,
            SubscriptionStatus = SubscriptionStatus.Active,
            CommercialPaidAddonsJson = JsonSerializer.Serialize(paidAddons),
        };
        _db.Orgs.Add(org);
        _db.SaveChanges();
        return org.Id;
    }

    private async Task<(DefaultHttpContext Context, bool NextCalled)> InvokeAsync(Type controller, string action, Guid orgId)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!;
        var descriptor = new ControllerActionDescriptor
        {
            ControllerName = controller.Name[..^"Controller".Length],
            ActionName = method.Name,
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = method,
        };
        var metadata = controller.GetCustomAttributes(inherit: true).Concat(method.GetCustomAttributes(inherit: true)).Append(descriptor);

        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), method.Name));
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|billing-admin")], "Test"));

        var tenant = new Mock<IRequestTenantContext>();
        tenant.SetupGet(t => t.OrgId).Returns(orgId);

        var nextCalled = false;
        var middleware = new CommercialAccessMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context, tenant.Object, _db);
        return (context, nextCalled);
    }

    private static async Task<JsonElement> ReadAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }
}
