using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// A real <see cref="IAuthorizationService"/> wired with <see cref="HostResourceAuthorizationHandler"/> for controller
/// unit tests: the caller's org is fixed and the context permissions come from <c>hasPermission</c> (all granted by
/// default), so org and ownership decisions are the production ones. The property reach is decided by the real
/// <see cref="HostScopeResolver"/> over a snapshot of the caller (AM-03): by default the caller is unknown to the
/// database and in no org team, so the token decides as it always did (an org-wide role, otherwise the properties it created).
/// </summary>
public static class HostAuthorizationTestHarness
{
    public static IAuthorizationService Create(
        Guid? callerOrgId,
        Func<string, string, bool>? hasPermission = null,
        IHostScopeResolver? scopeResolver = null)
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
        services.AddSingleton(scopeResolver ?? ScopeResolver());
        services.AddSingleton<IAuthorizationHandler, HostResourceAuthorizationHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    /// <summary>
    /// The real scope resolver over <paramref name="snapshot"/> for every user (AM-03). Without a snapshot the user is unknown
    /// to the database, which is decided by the token roles: the rule of before the team.
    /// </summary>
    public static IHostScopeResolver ScopeResolver(UserAuthorizationSnapshot? snapshot = null)
    {
        var store = new Mock<IUserAuthorizationSnapshotStore>();
        store
            .Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot ?? UserAuthorizationSnapshot.Missing);
        return new HostScopeResolver(store.Object, NullLogger<HostScopeResolver>.Instance);
    }

    public static ClaimsPrincipal User(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
