using System.Reflection;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E40/F01/US01/T02 (NF-140): the CORS of an agency domain opens the owner portal and nothing else. Every action whose route starts with a prefix
/// of <see cref="OrgHostCorsOriginSource.OwnerPortalPathPrefixes"/> requires the <c>OwnerPortal</c> policy (the two acceptance endpoints of the
/// invitation pages excepted, which keep their secret-token check): a prefix that leads to a host endpoint fails here.
/// </summary>
public class OwnerPortalCorsArchitectureTests
{
    /// <summary>The acceptance endpoints the invitation pages call before the owner has an <c>agency_ids</c> claim.</summary>
    private static readonly string[] AcceptanceActions =
    [
        $"{nameof(OwnerInvitationAcceptanceController)}.{nameof(OwnerInvitationAcceptanceController.Accept)}",
        $"{nameof(OwnerContactAcceptanceController)}.{nameof(OwnerContactAcceptanceController.Accept)}",
    ];

    [Fact]
    public void AllowedPrefixes_AreTheOwnerPortalAndTheTwoAcceptanceEndpoints()
    {
        Assert.Equal(
            new[] { "/api/owner-portal", "/api/owner-invitations/accept", "/api/owner-contacts/accept" },
            OrgHostCorsOriginSource.OwnerPortalPathPrefixes);
    }

    [Fact]
    public void AllowedPrefixes_RequireOwnerPortalPolicy()
    {
        var reached = Actions()
            .Where(a => OrgHostCorsOriginSource.OwnerPortalPathPrefixes.Any(prefix => IsUnder(a.Route, prefix)))
            .ToList();

        var outsideThePolicy = reached
            .Where(a => !AcceptanceActions.Contains(a.Key) && !RequiresOwnerPortalPolicy(a.Controller, a.Method))
            .Select(a => $"{a.Key} ({a.Route})")
            .ToList();

        Assert.True(outsideThePolicy.Count == 0, "Actions reachable from an agency domain without the OwnerPortal policy: " + string.Join(", ", outsideThePolicy));
        // The prefixes really reach the portal and the two acceptance actions (a renamed route would make this test pass on nothing).
        Assert.Contains(reached, a => a.Controller == typeof(OwnerPortalController));
        Assert.All(AcceptanceActions, key => Assert.Contains(reached, a => a.Key == key));
    }

    [Fact]
    public void AcceptanceActions_KeepTheirAuthenticatedPolicy_AndAreNeverAnonymous()
    {
        foreach (var action in Actions().Where(a => AcceptanceActions.Contains(a.Key)))
        {
            Assert.Empty(action.Method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Contains(
                action.Method.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
                a => a.Policy == CasazenPolicies.Authenticated);
        }
    }

    [Fact]
    public void GuestAreaControllers_StayUnderThePublicPrefixes()
    {
        // The guest area of an agency domain needs no CORS rule of its own because its routes are under /api/public: pinned here.
        foreach (var controller in new[] { typeof(GuestAreaController), typeof(PublicGuestCheckInController) })
        {
            var routes = Actions().Where(a => a.Controller == controller).Select(a => a.Route).ToList();
            Assert.NotEmpty(routes);
            Assert.All(routes, route => Assert.Contains(OrgHostCorsOriginSource.PublicPathPrefixes, prefix => IsUnder(route, prefix)));
        }
    }

    private static bool RequiresOwnerPortalPolicy(Type controller, MethodInfo method)
    {
        if (method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()
            || controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            return false;

        return method.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Any(a => a.Policy == CasazenPolicies.OwnerPortal);
    }

    private static bool IsUnder(string route, string prefix) =>
        route.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || route.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every routed action of the web assembly with its full route template (<c>/api/...</c>, lower-case segments as written).</summary>
    private static IEnumerable<(string Key, string Route, Type Controller, MethodInfo Method)> Actions()
    {
        var controllers = typeof(OwnerPortalController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t));

        foreach (var controller in controllers)
        {
            var prefixes = controller.GetCustomAttributes<RouteAttribute>(inherit: true).Select(r => r.Template).DefaultIfEmpty(string.Empty).ToList();
            var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var templates = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Select(h => h.Template)
                    .Concat(method.GetCustomAttributes<RouteAttribute>(inherit: true).Select(r => (string?)r.Template))
                    .ToList();
                if (templates.Count == 0)
                    continue;

                foreach (var template in templates)
                {
                    foreach (var prefix in prefixes)
                        yield return ($"{controller.Name}.{method.Name}", Combine(prefix, template), controller, method);
                }
            }
        }
    }

    private static string Combine(string controllerTemplate, string? actionTemplate)
    {
        if (actionTemplate is not null && (actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/", StringComparison.Ordinal)))
            return "/" + actionTemplate.TrimStart('~', '/');

        var parts = new[] { controllerTemplate.Trim('/'), actionTemplate?.Trim('/') ?? string.Empty }.Where(p => p.Length > 0);
        return "/" + string.Join('/', parts);
    }
}
