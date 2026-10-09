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
        ["OrgInvitationAcceptanceController.Accept"] = "The invited person joins the org that invited its own email (AM-02): it has no org, role or permission yet, so no context policy can exist. What decides is the secret token, a verified account email equal to the invited one, not being a platform admin, and the consents of the org it joins.",

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

    /// <summary>
    /// The anonymous endpoints of the booking from the supplier showcase (SP-10), each with the reason it is public: the customer
    /// has no account, and the first call only holds a slot until the customer's e-mail is checked.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PublicSupplierBooking = new Dictionary<string, string>
    {
        ["PublicSupplierBookingController.Create"] = "A customer without an account holds a slot of an active supplier and leaves the data of the request; nothing reaches the supplier until the e-mail is checked (SP-10, behind SupplierShowcaseBooking, rate limited per IP and per address).",
        ["PublicSupplierBookingController.ConfirmEmail"] = "The customer follows the link of the verification e-mail: the token in the body is the secret, and only then the request exists (SP-10, behind SupplierShowcaseBooking, rate limited).",
    };

    /// <summary>
    /// The anonymous endpoints of the customer's own area of a booking made from the supplier showcase (SP-11), each with the reason it
    /// is public: the customer has no account, and proves it is the one who booked with the code of the booking and its e-mail address.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PublicSupplierBookingManagement = new Dictionary<string, string>
    {
        ["PublicSupplierBookingManagementController.Lookup"] = "A customer without an account finds its booking again with the code and the e-mail address of the booking: one 404 for everything that does not identify it (SP-11, behind SupplierShowcaseBooking, rate limited per IP and per address).",
        ["PublicSupplierBookingManagementController.Cancel"] = "The customer cancels its own booking, proving it with the code and the e-mail address in the body (SP-11, behind SupplierShowcaseBooking, rate limited per IP and per address).",
        ["PublicSupplierBookingManagementController.Reschedule"] = "The customer moves its own new request to another free slot, proving it with the code and the e-mail address in the body (SP-11, behind SupplierShowcaseBooking, rate limited per IP and per address).",
        ["PublicSupplierBookingManagementController.AcceptProposal"] = "The customer accepts the other time the supplier proposed for its own booking, proving it with the code and the e-mail address in the body (SP-11, behind SupplierShowcaseBooking, rate limited per IP and per address).",
        ["PublicSupplierBookingManagementController.RejectProposal"] = "The customer turns down the other time the supplier proposed for its own booking, proving it with the code and the e-mail address in the body (SP-11, behind SupplierShowcaseBooking, rate limited per IP and per address).",
    };

    private static IEnumerable<MethodInfo> PublicBookingManagementActions() =>
        typeof(PublicSupplierBookingManagementController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any());

    private static IEnumerable<MethodInfo> PublicBookingActions() =>
        typeof(PublicSupplierBookingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any());

    [Fact]
    public void PublicSupplierBookingManagement_EveryAnonymousAction_IsListedWithItsReason()
    {
        var anonymous = Actions()
            .Where(a => a.IsAnonymous && a.Key.StartsWith(nameof(PublicSupplierBookingManagementController) + ".", StringComparison.Ordinal))
            .Select(a => a.Key)
            .Order()
            .ToList();

        Assert.Equal(PublicSupplierBookingManagement.Keys.Order(), anonymous);
        Assert.All(PublicSupplierBookingManagement, entry => Assert.True(entry.Value.Length >= 40, $"{entry.Key}: say why it is public"));
    }

    [Fact]
    public void PublicSupplierBookingManagement_EveryAction_IsBehindTheFlag_RateLimitedPerIpAndPerAddress_AndBoundedInSize()
    {
        var registered = RateLimitingServiceCollectionExtensions.Policies.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(PublicSupplierBookingManagement.Count, PublicBookingManagementActions().Count());
        foreach (var action in PublicBookingManagementActions())
        {
            var gates = action.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag).ToList();
            Assert.Equal(new[] { FeatureFlags.SupplierShowcaseBooking }, gates);

            // The policy of "Le mie prenotazioni" per IP, and the limit per address and supplier next to it, on every endpoint.
            Assert.Equal(RateLimitPolicies.PublicGuestBookingLookup, RateLimitPolicyOf(action));
            Assert.Contains(RateLimitPolicyOf(action)!, registered);
            Assert.Single(action.GetCustomAttributes<SupplierBookingManageRateLimitAttribute>());

            var limit = action.GetCustomAttribute<RequestSizeLimitAttribute>();
            Assert.NotNull(limit);
            Assert.Equal((long?)ShowcaseBookingLimits.ManageMaxBodyBytes, GetBytes(limit));
            Assert.True(GetBytes(limit) <= 64 * 1024, $"{action.Name}: the body of an anonymous call is small");
        }
    }

    [Fact]
    public void PublicSupplierBookingManagement_TheCodeAndTheAddressAreInTheBody_NeverInTheUrl()
    {
        var controllerRoute = typeof(PublicSupplierBookingManagementController).GetCustomAttribute<RouteAttribute>();

        Assert.Equal("api/public/supplier-bookings", controllerRoute?.Template);
        Assert.NotNull(typeof(PublicSupplierBookingManagementController).GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var action in PublicBookingManagementActions())
        {
            var verbs = action.GetCustomAttributes<HttpMethodAttribute>().ToList();
            var template = Assert.Single(verbs).Template ?? string.Empty;

            // POST only (a GET would put the credentials where logs and caches read them), and no route parameter at all.
            Assert.Equal(new[] { "POST" }, Assert.Single(verbs).HttpMethods);
            Assert.DoesNotContain("{", template);
            Assert.DoesNotContain(action.GetParameters(), p => p.GetCustomAttributes<FromQueryAttribute>().Any() || p.GetCustomAttributes<FromRouteAttribute>().Any());
            Assert.All(
                action.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)),
                p => Assert.NotEmpty(p.GetCustomAttributes<FromBodyAttribute>()));
        }
    }

    [Fact]
    public void PublicSupplierBookingManagement_TheBodiesAreCountedByTheLimitPerAddress()
    {
        foreach (var action in PublicBookingManagementActions())
        {
            var body = Assert.Single(action.GetParameters(), p => p.GetCustomAttributes<FromBodyAttribute>().Any());
            Assert.True(
                typeof(Casazen.Web.DTOs.IPerEmailRateLimitedRequest).IsAssignableFrom(body.ParameterType),
                $"{action.Name}: its body has to carry the address the limit counts");
        }
    }

    [Fact]
    public void PublicSupplierBookingManagement_TheFlagMakesThemAnswerLikeARouteThatDoesNotExist_BeforeAuthenticationAndBinding()
    {
        // The flag is read by the middleware that runs before authentication and model binding: every action carries the attribute.
        Assert.All(
            PublicBookingManagementActions(),
            action => Assert.NotEmpty(action.GetCustomAttributes<FeatureGateAttribute>(inherit: true)));
        Assert.Empty(typeof(PublicSupplierBookingManagementController).GetCustomAttributes<FeatureGateAttribute>(inherit: true));
    }

    [Fact]
    public void PublicSupplierBooking_EveryAnonymousAction_IsListedWithItsReason()
    {
        var anonymous = Actions()
            .Where(a => a.IsAnonymous && a.Key.StartsWith(nameof(PublicSupplierBookingController) + ".", StringComparison.Ordinal))
            .Select(a => a.Key)
            .Order()
            .ToList();

        Assert.Equal(PublicSupplierBooking.Keys.Order(), anonymous);
        Assert.All(PublicSupplierBooking, entry => Assert.True(entry.Value.Length >= 40, $"{entry.Key}: say why it is public"));
    }

    [Fact]
    public void PublicSupplierBooking_EveryAction_IsBehindTheFlag_RateLimited_AndBoundedInSize()
    {
        var registered = RateLimitingServiceCollectionExtensions.Policies.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(PublicSupplierBooking.Count, PublicBookingActions().Count());
        foreach (var action in PublicBookingActions())
        {
            var gates = action.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag).ToList();
            Assert.Equal(new[] { FeatureFlags.SupplierShowcaseBooking }, gates);

            var policy = RateLimitPolicyOf(action);
            Assert.False(string.IsNullOrEmpty(policy), $"{action.Name} has no rate limit");
            Assert.Contains(policy!, registered);

            var limit = action.GetCustomAttribute<RequestSizeLimitAttribute>();
            Assert.NotNull(limit);
            Assert.True(GetBytes(limit) <= 64 * 1024, $"{action.Name}: the body of an anonymous call is small");
        }
    }

    [Fact]
    public void PublicSupplierBooking_TheCreateHasTheTightestLimitsOfThePublicSite_TheCheckUsesTheLookupOne()
    {
        var create = typeof(PublicSupplierBookingController).GetMethod(nameof(PublicSupplierBookingController.Create))!;
        var confirm = typeof(PublicSupplierBookingController).GetMethod(nameof(PublicSupplierBookingController.ConfirmEmail))!;

        Assert.Equal(RateLimitPolicies.PublicSupplierBookingCreate, RateLimitPolicyOf(create));
        // The second limit, per e-mail address and supplier, runs next to the one per IP.
        Assert.Single(create.GetCustomAttributes<SupplierBookingEmailRateLimitAttribute>());
        Assert.Equal(RateLimitPolicies.PublicBookingLookup, RateLimitPolicyOf(confirm));
        Assert.Empty(confirm.GetCustomAttributes<SupplierBookingEmailRateLimitAttribute>());
    }

    [Fact]
    public void PublicSupplierBooking_TheRoutesAreUnderThePublicSuppliersPrefix_SoTheProxyAndTheRobotsRulesCoverThem()
    {
        var route = typeof(PublicSupplierBookingController).GetCustomAttribute<RouteAttribute>();

        Assert.Equal("api/public/suppliers/{slug}/bookings", route?.Template);
        Assert.NotNull(typeof(PublicSupplierBookingController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

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
