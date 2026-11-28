using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

public class ApprovalDecidePolicyTests
{
    private static readonly string[] Actions = [nameof(ApprovalQueueController.GetPendingItems), nameof(ApprovalQueueController.EditItem), nameof(ApprovalQueueController.ApproveItem), nameof(ApprovalQueueController.RejectItem)];

    [Fact]
    public async Task Approval_WithoutDecidePermission_Forbidden_Test()
    {
        var methods = typeof(ApprovalQueueController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any())
            .ToList();
        Assert.Equal(Actions.Order(StringComparer.Ordinal), methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        var classPolicies = typeof(ApprovalQueueController).GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Select(a => a.Policy).ToList();
        Assert.Equal([CasazenPolicies.ApprovalQueueAccess], classPolicies);
        foreach (var method in methods)
        {
            var attributes = method.GetCustomAttributes(inherit: true);
            Assert.DoesNotContain(attributes, a => a is IAllowAnonymous);
            Assert.All(attributes.OfType<IAuthorizeData>(), a => Assert.Equal(CasazenPolicies.ApprovalQueueAccess, a.Policy));
        }

        foreach (var (role, passes) in new[]
                 {
                     (OrgRole.Owner, true),
                     (OrgRole.Admin, true),
                     (OrgRole.PropertyManager, true),
                     (OrgRole.Collaborator, false),
                     (OrgRole.Accountant, false),
                 })
        {
            var held = OrgRoleCatalog.SeededRoles
                .Where(r => r.ContextKey == OrgRoleCatalog.ShortRent && r.RoleKey == OrgRoleCatalog.HostRoleKey(role, OrgRoleCatalog.ShortRent))
                .SelectMany(r => r.Permissions)
                .ToHashSet(StringComparer.Ordinal);
            if (role == OrgRole.Owner)
                held.UnionWith(ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent).Permissions);

            Assert.Equal(passes, await PassesAsync(CasazenPolicies.ApprovalDecide, (context, permission) => context == OrgRoleCatalog.ShortRent && held.Contains(permission)));
        }
    }

    [Fact]
    public async Task ApprovalDecide_IsNotGrantedByTheOldBroadPermissions()
    {
        Assert.False(await PassesAsync(CasazenPolicies.ApprovalDecide, (_, permission) => permission is "property.read" or "property.write" or "booking.read" or "booking.write" or "guest.write"));
        Assert.True(await PassesAsync(CasazenPolicies.ApprovalDecide, (context, permission) => context == "short-rent" && permission == "approval.decide"));
        Assert.False(await PassesAsync(CasazenPolicies.ApprovalDecide, (context, permission) => context == "long-rent" && permission == "approval.decide"));
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("sub", "auth0|approver")], "TestAuth"));

    [Theory]
    [InlineData("marketing.manage", true)]
    [InlineData("approval.decide", true)]
    [InlineData("guest.write", false)]
    public async Task QueueAccessAcceptsEitherRelevantShortRentPermission(string permission, bool expected)
    {
        Assert.Equal(expected, await PassesAsync(CasazenPolicies.ApprovalQueueAccess, (context, key) =>
            context == OrgRoleCatalog.ShortRent && key == permission));
    }

    private static async Task<bool> PassesAsync(string policyName, Func<string, string, bool> hasPermission)
    {
        var registered = new ServiceCollection();
        registered.AddLogging();
        registered.AddCasazenAuthorization();
        var policy = await registered.BuildServiceProvider()
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(policyName);
        Assert.NotNull(policy);

        var contexts = new Mock<IContextAuthorizationService>();
        contexts
            .Setup(c => c.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string context, string permission, CancellationToken _) => hasPermission(context, permission));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        var onboarding = new Mock<IHostOnboardingGate>();
        onboarding.Setup(g => g.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostOnboardingStatus(true, true));
        services.AddSingleton<IAuthorizationHandler>(new ContextAuthorizationHandler(contexts.Object, onboarding.Object));
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(User(), policy)).Succeeded;
    }
}
