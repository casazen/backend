using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-00 (S1): the endpoints under the policy <c>OrgBillingAdmin</c> (Orgs, Billing, Connect, Branding, Domain,
/// SiteDocuments) are found by reflection, and each of them refuses a member of the org who has a DB membership and no
/// <c>Staff</c> claim, while the owner passes. Over the real pipeline see <c>OrgBillingAdminMembershipAccessTests</c>.
/// </summary>
public class OrgBillingAdminPolicyCoverageTests
{
    /// <summary>
    /// Actions that must stay under the policy, on every version of the API: the plan, the billing profile and the
    /// portal, the Connect onboarding, the plan entitlement and the custom domain (PL-16, A3-42).
    /// </summary>
    private static readonly string[] RequiredActions =
    [
        "BillingController.CreateCheckoutSession",
        "BillingController.CreatePortalSession",
        "BillingController.GetSubscription",
        "BillingController.UpdateBillingProfile",
        "ConnectController.CreateAccount",
        "ConnectController.CreateOnboardingLink",
        "OrgsController.GetMyEntitlement",
        "OrgsController.UpdateMyPlan",
        "OrgDomainController.GetDomain",
        "OrgDomainController.SetDomain",
        "OrgDomainController.VerifyDomain",
    ];

    /// <summary>
    /// Controllers of the same family that exist on some versions of the API (public-site branding, operator
    /// documents): when they exist every action of them is under the policy.
    /// </summary>
    private static readonly string[] OrgAdminControllers = ["OrgBrandingController", "OrgSiteDocumentsController"];

    /// <summary>Org settings actions of <c>OrgsController</c> (PL-04): under the policy when the version has them.</summary>
    private static readonly string[] OrgAdminActionsIfPresent =
    [
        "OrgsController.GetMySettings",
        "OrgsController.GetSlugAvailability",
        "OrgsController.UpdateMySettings",
    ];

    public static IEnumerable<object[]> Actions() =>
        OrgBillingAdminActions.All.Select(a => new object[] { a.ToString() });

    [Fact]
    public void Discovery_FindsTheBillingConnectOrgAndDomainActions()
    {
        var keys = OrgBillingAdminActions.All.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

        var missing = RequiredActions.Where(required => !keys.Contains(required)).ToList();

        Assert.True(missing.Count == 0, "Actions that lost the OrgBillingAdmin policy (or were renamed): " + string.Join(", ", missing));
    }

    [Fact]
    public void Discovery_BrandingSiteDocumentsAndOrgSettings_WhenTheyExist_AreUnderThePolicy()
    {
        var keys = OrgBillingAdminActions.All.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var assembly = typeof(OrgsController).Assembly;
        var ns = typeof(OrgsController).Namespace;
        var unprotected = new List<string>();

        foreach (var name in OrgAdminControllers)
        {
            var controller = assembly.GetType($"{ns}.{name}");
            if (controller is null)
                continue;

            var routed = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
                .Select(m => $"{name}.{m.Name}");
            unprotected.AddRange(routed.Where(key => !keys.Contains(key)));
        }

        foreach (var key in OrgAdminActionsIfPresent)
        {
            var parts = key.Split('.');
            var controller = assembly.GetType($"{ns}.{parts[0]}");
            if (controller?.GetMethod(parts[1]) is not null && !keys.Contains(key))
                unprotected.Add(key);
        }

        Assert.True(unprotected.Count == 0, "Org admin actions outside the OrgBillingAdmin policy: " + string.Join(", ", unprotected));
    }

    [Fact]
    public void Discovery_EveryAction_IsNotOverriddenByAllowAnonymous()
    {
        var anonymous = OrgBillingAdminActions.All.Where(a => a.IsAnonymous).Select(a => a.Key).ToList();

        Assert.True(anonymous.Count == 0, "Actions of the OrgBillingAdmin policy marked [AllowAnonymous]: " + string.Join(", ", anonymous));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Policy_DbMemberWithoutStaffClaim_IsRefused(string action)
    {
        var handler = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.None, ["short-rent/bk09_collaborator"]));

        foreach (var policy in await OrgPoliciesOfAsync(action))
        {
            var context = new AuthorizationHandlerContext(policy.Requirements, Principal(), resource: null);

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded, $"{action}: a member of the org with a DB membership and no Staff claim passed the policy.");
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Policy_PropertyManagerJwtRole_IsRefused(string action)
    {
        var handler = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyManager, []));

        foreach (var policy in await OrgPoliciesOfAsync(action))
        {
            var context = new AuthorizationHandlerContext(policy.Requirements, Principal("PropertyManager"), resource: null);

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded, $"{action}: a PropertyManager passed the policy (D12).");
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Policy_OwnerWithOwnerMembership_Passes(string action)
    {
        // The positive control: the refusals above are about the member, not about a policy nobody can pass.
        var handler = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyOwner, ["short-rent/property_owner"]));

        foreach (var policy in await OrgPoliciesOfAsync(action))
        {
            var context = new AuthorizationHandlerContext(policy.Requirements, Principal("PropertyOwner"), resource: null);

            await handler.HandleAsync(context);

            Assert.True(context.HasSucceeded, $"{action}: the owner of the org does not pass the policy.");
        }
    }

    /// <summary>The registered <c>OrgBillingAdmin</c> policy (the real <c>AddCasazenAuthorization</c> set) of an action.</summary>
    private static async Task<List<AuthorizationPolicy>> OrgPoliciesOfAsync(string action)
    {
        var found = OrgBillingAdminActions.All.Single(a => a.ToString() == action);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policies = new List<AuthorizationPolicy>();
        foreach (var name in found.Policies.Where(name => name == CasazenPolicies.OrgBillingAdmin))
        {
            policies.Add(await provider.GetPolicyAsync(name)
                ?? throw new InvalidOperationException($"Policy '{name}' of {action} is not registered."));
        }

        Assert.NotEmpty(policies);
        return policies;
    }

    /// <summary>The caller: an authenticated user with the given JWT roles (none for a user who has only DB memberships).</summary>
    private static ClaimsPrincipal Principal(params string[] jwtRoles) =>
        OrgBillingAdminAuthorizationHandlerTests.Context(jwtRoles.Select(role => new Claim(ClaimTypes.Role, role))).User;
}
