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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// E05/F04/US01/T02 (AC-NF68-5): the endpoints of the approval queue are guarded by <c>approval.decide</c> and by nothing weaker. The policy is evaluated with the production
/// handler (<c>AddCasazenAuthorization</c>, <see cref="ContextAuthorizationHandler"/>) for the permissions each org role is seeded with, so a collaborator who fails it is the 403
/// the framework answers.
/// </summary>
public class ApprovalDecidePolicyTests
{
    private static readonly string[] Actions = [nameof(ApprovalQueueController.GetPendingItems), nameof(ApprovalQueueController.EditItem), nameof(ApprovalQueueController.ApproveItem), nameof(ApprovalQueueController.RejectItem)];

    /// <summary>
    /// Permission rule: list, edit, approve and reject need <c>approval.decide</c>. The owner, the administrator and the property manager pass; the collaborator, who held the broad
    /// <c>property.write</c> that used to guard the queue, and the accountant fail the policy (a 403), and no action of the controller is anonymous or guarded by a weaker policy.
    /// </summary>
    [Fact]
    public async Task Approval_WithoutDecidePermission_Forbidden_Test()
    {
        // Every action is behind the policy of the permission, as the controller declares it, and none escapes it.
        var methods = typeof(ApprovalQueueController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any())
            .ToList();
        Assert.Equal(Actions.Order(StringComparer.Ordinal), methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        var classPolicies = typeof(ApprovalQueueController).GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Select(a => a.Policy).ToList();
        Assert.Equal([CasazenPolicies.ApprovalDecide], classPolicies);
        foreach (var method in methods)
        {
            var attributes = method.GetCustomAttributes(inherit: true);
            Assert.DoesNotContain(attributes, a => a is IAllowAnonymous);
            Assert.All(attributes.OfType<IAuthorizeData>(), a => Assert.Equal(CasazenPolicies.ApprovalDecide, a.Policy));
        }

        // Who passes it: the permission rows each role is seeded with, in the short-rent context.
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
            // The owner's role is the one of the model's own seed (role 1), not of the catalog: it holds the permission through the seed and the token fallback.
            if (role == OrgRole.Owner)
                held.UnionWith(ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent).Permissions);

            Assert.Equal(passes, await PassesAsync(CasazenPolicies.ApprovalDecide, (context, permission) => context == OrgRoleCatalog.ShortRent && held.Contains(permission)));
        }
    }

    [Fact]
    public async Task ApprovalDecide_IsNotGrantedByTheOldBroadPermissions()
    {
        // The collaborator reads and writes bookings and guests in part, and holds property.read: none of it decides the queue.
        Assert.False(await PassesAsync(CasazenPolicies.ApprovalDecide, (_, permission) => permission is "property.read" or "property.write" or "booking.read" or "booking.write" or "guest.write"));
        Assert.True(await PassesAsync(CasazenPolicies.ApprovalDecide, (context, permission) => context == "short-rent" && permission == "approval.decide"));
        Assert.False(await PassesAsync(CasazenPolicies.ApprovalDecide, (context, permission) => context == "long-rent" && permission == "approval.decide"));
    }

    // ─── Evaluating a policy with the production handler ───

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("sub", "auth0|approver")], "TestAuth"));

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
