using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// A1-20: the request tenant is loaded asynchronously once per request, fails closed before that, and can be
/// set by the org resolver after provisioning so the rest of the request is scoped to the new org.
/// </summary>
public class TenantContextTests
{
    private const string Sub = "auth0|tenant-context-user";

    private readonly string _databaseName = $"tenant-context-{Guid.NewGuid()}";

    [Fact]
    public async Task ResolveAsync_AuthenticatedUserWithOrg_LoadsOrgIdOncePerRequest()
    {
        var orgId = Guid.NewGuid();
        var services = BuildServices();
        await SeedUserAsync(services, orgId);
        var tenant = NewTenantContext(services, authenticated: true);

        await tenant.ResolveAsync();
        await SetUserOrgAsync(services, Guid.NewGuid());
        await tenant.ResolveAsync();

        Assert.True(tenant.FilterEnabled);
        Assert.Equal(orgId, tenant.OrgId);
    }

    [Fact]
    public async Task OrgId_ReadBeforeResolveAsync_IsNullFailClosed()
    {
        var services = BuildServices();
        await SeedUserAsync(services, Guid.NewGuid());
        var tenant = NewTenantContext(services, authenticated: true);

        Assert.True(tenant.FilterEnabled);
        Assert.Null(tenant.OrgId);
    }

    [Fact]
    public async Task ResolveAsync_AnonymousRequest_LeavesFilterDisabledWithoutOrg()
    {
        var services = BuildServices();
        await SeedUserAsync(services, Guid.NewGuid());
        var tenant = NewTenantContext(services, authenticated: false);

        await tenant.ResolveAsync();

        Assert.False(tenant.FilterEnabled);
        Assert.Null(tenant.OrgId);
    }

    [Fact]
    public async Task SetOrgId_AfterResolvingAUserWithoutOrg_ScopesTheRequestToTheNewOrg()
    {
        var services = BuildServices();
        await SeedUserAsync(services, orgId: null);
        var tenant = NewTenantContext(services, authenticated: true);
        await tenant.ResolveAsync();
        Assert.Null(tenant.OrgId);

        var provisioned = Guid.NewGuid();
        tenant.SetOrgId(provisioned);
        await tenant.ResolveAsync(); // already resolved: the provisioned org is kept

        Assert.Equal(provisioned, tenant.OrgId);
    }

    [Fact]
    public async Task SetOrgId_DifferentFromTheResolvedOrg_Throws()
    {
        var orgId = Guid.NewGuid();
        var services = BuildServices();
        await SeedUserAsync(services, orgId);
        var tenant = NewTenantContext(services, authenticated: true);
        await tenant.ResolveAsync();

        tenant.SetOrgId(orgId); // same org: no-op
        Assert.Throws<InvalidOperationException>(() => tenant.SetOrgId(Guid.NewGuid()));
        Assert.Equal(orgId, tenant.OrgId);
    }

    [Fact]
    public async Task InvokeAsync_Middleware_ResolvesTheTenantBeforeTheNextComponent()
    {
        var orgId = Guid.NewGuid();
        var services = BuildServices();
        await SeedUserAsync(services, orgId);
        var tenant = NewTenantContext(services, authenticated: true, out var httpContext);
        Guid? seenByNext = null;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            seenByNext = tenant.OrgId;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext, tenant);

        Assert.Equal(orgId, seenByNext);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_databaseName));
        return services.BuildServiceProvider();
    }

    private static async Task SeedUserAsync(ServiceProvider services, Guid? orgId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User { Id = Sub, Email = "tenant@example.com", FirstName = "T", LastName = "C", OrgId = orgId });
        await db.SaveChangesAsync();
    }

    private static async Task SetUserOrgAsync(ServiceProvider services, Guid orgId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == Sub);
        user.OrgId = orgId;
        await db.SaveChangesAsync();
    }

    private static TenantContext NewTenantContext(ServiceProvider services, bool authenticated) =>
        NewTenantContext(services, authenticated, out _);

    private static TenantContext NewTenantContext(ServiceProvider services, bool authenticated, out HttpContext httpContext)
    {
        var identity = authenticated
            ? new ClaimsIdentity([new Claim("sub", Sub)], "Test")
            : new ClaimsIdentity();
        httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return new TenantContext(
            accessor,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TenantContext>.Instance);
    }
}
