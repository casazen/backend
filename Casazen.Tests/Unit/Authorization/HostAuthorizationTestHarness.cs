using System.Security.Claims;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// A real <see cref="IAuthorizationService"/> wired with <see cref="HostResourceAuthorizationHandler"/> for controller
/// unit tests: the caller's org is fixed and the context permissions come from <c>hasPermission</c> (all granted by
/// default), so org and ownership decisions are the production ones.
/// </summary>
public static class HostAuthorizationTestHarness
{
    public static IAuthorizationService Create(Guid? callerOrgId, Func<string, string, bool>? hasPermission = null)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.OrgId).Returns(callerOrgId);
        tenant.SetupGet(t => t.FilterEnabled).Returns(true);

        var contexts = new Mock<IContextAuthorizationService>();
        contexts
            .Setup(c => c.HasPermissionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string context, string permission, CancellationToken _) =>
                hasPermission?.Invoke(context, permission) ?? true);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton(tenant.Object);
        services.AddSingleton(contexts.Object);
        services.AddSingleton<IAuthorizationHandler, HostResourceAuthorizationHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    public static ClaimsPrincipal User(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
