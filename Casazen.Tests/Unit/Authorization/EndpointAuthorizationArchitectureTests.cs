using System.Reflection;
using Casazen.Core.Features;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
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
        ["UsersController.RecordSignupAttribution"] = "Signup attribution of the org the caller's own first onboarding created (SE-03); the service reads the org from the caller's user row.",
        ["DevicesController.Register"] = "Push token of the caller's own device (hosts and suppliers alike); rows keyed by the caller's UserId.",
        ["DevicesController.Unregister"] = "Removes the caller's own push token.",
        ["SuppliersController.Claim"] = "Links the caller's own account to the supplier profile it registered (claim token or verified email, SU-02): the Supplier role does not exist yet.",

        // Catalogs without tenant data.
        ["BillingController.GetPlans"] = "Public plan catalog from configuration; every billing change is RequireOrgBillingAdmin.",
        ["TouristTaxRatesController.GetAll"] = "Platform reference data (tourist tax rates per comune), not tenant data; writes are AdminOnly.",
        ["TouristTaxRatesController.GetById"] = "Platform reference data, see GetAll.",
        ["TouristTaxRatesController.GetByCity"] = "Platform reference data, see GetAll.",
        ["ServiceCategoriesController.GetAll"] = "Fixed catalog of service category codes (SU-03) used by suppliers, hosts and admins alike; no tenant data.",

        // Endpoints shared by two roles: the action evaluates the policy of the branch it takes.
        ["ServiceRequestsController.List"] = "Host list (PropertyRead + HostScope) or supplier inbox with view=supplier (RequireSupplier), each checked with IAuthorizationService inside the action.",
        ["ServiceRequestsController.GetById"] = "Supplier branch (RequireSupplier + linked supplier org) or host branch (PropertyRead + HostScope), each checked with IAuthorizationService inside the action.",
        ["ServiceRequestsController.Cancel"] = "SP-04: the supplier it was sent to cancels before the work starts (RequireSupplier + linked supplier org), or the host up to the work in progress (PropertyWrite + HostResource); each branch is checked with IAuthorizationService inside the action.",
        ["ServiceRequestsController.GetPhoto"] = "SP-04: a photo of the work, private file: the supplier it was sent to (RequireSupplier + linked supplier org) or the host (PropertyRead + HostScope), each checked with IAuthorizationService inside the action.",
    };

    /// <summary>
    /// The anonymous endpoints of the supplier showcase (SP-09), each with the reason it is public. The showcase is read by
    /// customers who have no account: they publish only what the supplier chose to publish, never a person, never the
    /// supplier's private calendar; every one is rate limited, never indexable, and (but the page) behind a feature flag.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PublicSupplierShowcase = new Dictionary<string, string>
    {
        ["PublicSupplierController.GetBySlug"] = "The public page of an active supplier (SU-13): the supplier's choice to be found by a customer without an account; unknown, pending and suspended answer the same 404.",
        ["PublicSupplierController.ListServices"] = "The published services and prices of an active supplier: the price list the supplier chose to show (SP-09, behind SupplierShowcaseBooking).",
        ["PublicSupplierController.GetService"] = "One published service with its supplements: what the customer needs to price a booking (SP-09, behind SupplierShowcaseBooking).",
        ["PublicSupplierController.GetSlots"] = "The free slots of a published service, with no reason and no detail of the supplier's agenda: what a customer needs to pick a time (SP-09, behind SupplierShowcaseBooking).",
        ["PublicSupplierController.Quote"] = "The price estimate of a published service: computed from the supplier's own price list, nothing stored, nothing personal (SP-09, behind SupplierShowcaseBooking).",
    };

    [Fact]
    public void PublicSupplierShowcase_EveryAnonymousAction_IsListedWithItsReason()
    {
        var anonymous = Actions()
            .Where(a => a.IsAnonymous && a.Key.StartsWith(nameof(PublicSupplierController) + ".", StringComparison.Ordinal))
            .Select(a => a.Key)
            .Order()
            .ToList();

        Assert.Equal(PublicSupplierShowcase.Keys.Order(), anonymous);
        Assert.All(PublicSupplierShowcase, entry => Assert.True(entry.Value.Length >= 40, $"{entry.Key}: say why it is public"));
    }

    [Fact]
    public void PublicSupplierShowcase_EveryAction_IsRateLimited_WithAPolicyOfTheRegistry()
    {
        var registered = RateLimitingServiceCollectionExtensions.Policies.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var (name, policy) in PublicSupplierActions().Select(m => (m.Name, Policy: RateLimitPolicyOf(m))))
        {
            Assert.False(string.IsNullOrEmpty(policy), $"{name} has no rate limit");
            Assert.Contains(policy!, registered);
        }
    }

    [Fact]
    public void PublicSupplierShowcase_TheSlotsAndTheEstimate_HaveALimitOfTheirOwn_TheReadsShareThePublicOne()
    {
        var policies = PublicSupplierActions().ToDictionary(m => m.Name, RateLimitPolicyOf);

        Assert.Equal(RateLimitPolicies.PublicSupplierSlots, policies[nameof(PublicSupplierController.GetSlots)]);
        Assert.Equal(RateLimitPolicies.PublicSupplierQuote, policies[nameof(PublicSupplierController.Quote)]);
        Assert.Equal(RateLimitPolicies.PublicRead, policies[nameof(PublicSupplierController.GetBySlug)]);
        Assert.Equal(RateLimitPolicies.PublicRead, policies[nameof(PublicSupplierController.ListServices)]);
        Assert.Equal(RateLimitPolicies.PublicRead, policies[nameof(PublicSupplierController.GetService)]);
    }

    [Fact]
    public void PublicSupplierShowcase_TheEndpointsOfSp09_AreBehindTheFlag_ThePageIsNot()
    {
        foreach (var action in PublicSupplierActions())
        {
            var gates = action.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag).ToList();
            if (action.Name == nameof(PublicSupplierController.GetBySlug))
                Assert.Empty(gates);
            else
                Assert.Equal(new[] { FeatureFlags.SupplierShowcaseBooking }, gates);
        }

        // Nothing at the class level either: the flag gates the new endpoints one by one, and the page keeps answering.
        Assert.Empty(typeof(PublicSupplierController).GetCustomAttributes<FeatureGateAttribute>(inherit: true));
    }

    [Fact]
    public void PublicSupplierShowcase_TheEstimateBody_HasASmallSizeLimit()
    {
        var limit = typeof(PublicSupplierController).GetMethod(nameof(PublicSupplierController.Quote))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(limit);
        Assert.Equal((long?)PublicShowcaseLimits.QuoteMaxBodyBytes, GetBytes(limit));
        Assert.True(PublicShowcaseLimits.QuoteMaxBodyBytes <= 16 * 1024);
    }

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

    /// <summary>The routed actions declared by the public supplier controller.</summary>
    private static IEnumerable<MethodInfo> PublicSupplierActions() =>
        typeof(PublicSupplierController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any());

    /// <summary>The rate limit of an action: its own attribute, else the controller's.</summary>
    private static string? RateLimitPolicyOf(MethodInfo action) =>
        (action.GetCustomAttribute<EnableRateLimitingAttribute>()
         ?? action.DeclaringType!.GetCustomAttribute<EnableRateLimitingAttribute>())?.PolicyName;

    /// <summary>The limit a <see cref="RequestSizeLimitAttribute"/> sets, as the endpoint metadata says it.</summary>
    private static long? GetBytes(RequestSizeLimitAttribute attribute) => ((IRequestSizeLimitMetadata)attribute).MaxRequestBodySize;

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
