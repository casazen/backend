using System.Reflection;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// TN-3 architecture guard for endpoint authorization. Every action of the API is either explicitly anonymous or
/// protected by a policy that says <i>who</i> may call it (a context permission, a role, supplier, org admin). An action
/// whose only requirement is being signed in (<see cref="CasazenPolicies.Authenticated"/> or a bare
/// <c>[Authorize]</c>) is allowed only when listed in <see cref="AuthenticatedOnly"/> with the reason. The policies used
/// by the attributes and the policies registered at startup must be the same set.
/// </summary>
public class EndpointAuthorizationArchitectureTests
{
    /// <summary>Actions that only require a signed-in user, each with its reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AuthenticatedOnly = new Dictionary<string, string>
    {
        // The caller's own identity, profile and onboarding: every user has them, whatever the context.
        ["AuthController.GetProfile"] = "Profile of the caller, read from the token.",
        ["AuthController.Logout"] = "Ends the caller's own session.",
        ["MeController.GetContexts"] = "Lists the contexts the caller may enter (how the client picks host, landlord or supplier UI).",
        ["OnboardingController.GetStatus"] = "Onboarding state of the caller, needed before any context exists.",
        ["UsersController.GetMe"] = "Profile of the caller.",
        ["UsersController.UpdateMe"] = "Updates the caller's own profile.",
        ["UsersController.PostOnboarding"] = "First onboarding: creates the caller's org and memberships, so no context permission exists yet.",
        ["UsersController.PutOnboarding"] = "Onboarding update of the caller's own account (same reason as PostOnboarding).",
        ["DevicesController.Register"] = "Push token of the caller's own device (hosts and suppliers alike); rows keyed by the caller's UserId.",
        ["DevicesController.Unregister"] = "Removes the caller's own push token.",

        // Catalogs without tenant data.
        ["BillingController.GetPlans"] = "Public plan catalog from configuration; every billing change is RequireOrgBillingAdmin.",
        ["TouristTaxRatesController.GetAll"] = "Platform reference data (tourist tax rates per comune), not tenant data; writes are AdminOnly.",
        ["TouristTaxRatesController.GetById"] = "Platform reference data, see GetAll.",
        ["TouristTaxRatesController.GetByCity"] = "Platform reference data, see GetAll.",
        ["ServiceCategoriesController.GetAll"] = "Fixed catalog of service category codes (SU-03) used by suppliers, hosts and admins alike; no tenant data.",

        // Endpoints shared by two roles: the action evaluates the policy of the branch it takes.
        ["ServiceRequestsController.List"] = "Host list (PropertyRead + HostScope) or supplier inbox with view=supplier (RequireSupplier), each checked with IAuthorizationService inside the action.",
        ["ServiceRequestsController.GetById"] = "Supplier branch (RequireSupplier + linked supplier org) or host branch (PropertyRead + HostScope), each checked with IAuthorizationService inside the action.",
    };

    [Fact]
    public void EveryAction_IsAnonymousOrHasAnAuthorizationDecision()
    {
        var undecided = Actions()
            .Where(a => !a.IsAnonymous && a.Policies.Count == 0)
            .Select(a => a.Key)
            .ToList();

        Assert.True(undecided.Count == 0,
            "Actions without [Authorize] nor [AllowAnonymous] (there is no fallback policy): " + string.Join(", ", undecided));
    }

    [Fact]
    public void EveryProtectedAction_HasAPolicyBeyondAuthenticated_OrIsAllowListed()
    {
        var authenticatedOnly = Actions()
            .Where(a => !a.IsAnonymous && a.Policies.Count > 0 && a.Policies.All(IsAuthenticatedOnly))
            .Select(a => a.Key)
            .ToHashSet();

        var missing = authenticatedOnly.Where(k => !AuthenticatedOnly.ContainsKey(k)).Order().ToList();
        Assert.True(missing.Count == 0,
            "Actions protected only by authentication: give them a context/role policy (CasazenPolicies) or add them, " +
            "with the reason, to AuthenticatedOnly: " + string.Join(", ", missing));

        var stale = AuthenticatedOnly.Keys.Where(k => !authenticatedOnly.Contains(k)).Order().ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer match an authentication-only action: " + string.Join(", ", stale));
    }

    [Fact]
    public void AllowList_EntriesHaveAReason()
    {
        Assert.All(AuthenticatedOnly, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
    }

    [Fact]
    public async Task EveryPolicyUsedByAnAction_IsRegistered()
    {
        var provider = BuildPolicyProvider();
        var used = Actions()
            .SelectMany(a => a.Policies)
            .Where(p => p is not null && !p.StartsWith(RolesPrefix, StringComparison.Ordinal))
            .Select(p => p!)
            .Distinct();

        foreach (var policy in used)
            Assert.True(await provider.GetPolicyAsync(policy) is not null, $"Policy '{policy}' is used but not registered.");
    }

    [Fact]
    public async Task EveryRegisteredPolicy_IsUsedByAnAction()
    {
        var provider = BuildPolicyProvider();
        var used = Actions().SelectMany(a => a.Policies).Where(p => p is not null).ToHashSet();

        foreach (var policy in RegisteredPolicyNames())
        {
            Assert.True(await provider.GetPolicyAsync(policy) is not null, $"CasazenPolicies.{policy} is not registered.");
            Assert.True(used.Contains(policy), $"Policy '{policy}' is registered but no action uses it: use it or remove it.");
        }
    }

    [Fact]
    public async Task LegacyPolicies_AreNotRegistered()
    {
        var provider = BuildPolicyProvider();

        // PropertyOwner was RequireAuthenticatedUser() under a misleading name; LongTermLandlord and
        // PropertyManagerOrAdmin were JWT-role policies outside the context model (TN-3, A9 section 1).
        foreach (var legacy in new[] { "PropertyOwner", "PropertyManagerOrAdmin", "LongTermLandlord" })
            Assert.Null(await provider.GetPolicyAsync(legacy));
    }

    private const string RolesPrefix = "roles:";

    private static bool IsAuthenticatedOnly(string? policy) =>
        policy is null || policy == CasazenPolicies.Authenticated;

    private static IAuthorizationPolicyProvider BuildPolicyProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();
    }

    private static IEnumerable<string> RegisteredPolicyNames() =>
        typeof(CasazenPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) } && f.Name != nameof(CasazenPolicies.ContextPolicyPrefix))
            .Select(f => (string)f.GetRawConstantValue()!);

    private sealed record ActionInfo(string Key, bool IsAnonymous, IReadOnlyList<string?> Policies);

    /// <summary>
    /// Every routed action of the API. A bare <c>[Authorize]</c> counts as a <c>null</c> policy (default policy =
    /// authenticated user); <c>[Authorize(Roles = ...)]</c> counts as a role requirement.
    /// </summary>
    private static IEnumerable<ActionInfo> Actions()
    {
        var controllers = typeof(PaymentsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true });

        foreach (var controller in controllers)
        {
            var controllerAttributes = controller.GetCustomAttributes(inherit: true);
            var controllerAnonymous = controllerAttributes.OfType<IAllowAnonymous>().Any();
            var controllerAuthorize = controllerAttributes.OfType<IAuthorizeData>().ToList();

            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
                .Where(m => m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any());

            foreach (var action in actions)
            {
                var attributes = action.GetCustomAttributes(inherit: true);
                var authorize = controllerAuthorize.Concat(attributes.OfType<IAuthorizeData>()).ToList();
                var policies = authorize
                    .Select(a => a.Policy ?? (string.IsNullOrWhiteSpace(a.Roles) ? null : RolesPrefix + a.Roles))
                    .ToList();

                yield return new ActionInfo(
                    $"{controller.Name}.{action.Name}",
                    controllerAnonymous || attributes.OfType<IAllowAnonymous>().Any(),
                    policies);
            }
        }
    }
}
