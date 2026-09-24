using System.Reflection;
using System.Security.Claims;
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
/// LT-05 (A7-06): a long-term landlord (only the long-rent context) reaches the property core it needs to write leases
/// (list, record, documents such as the APE) and nothing about short stays. The plan entitlement moved to the org policy
/// <see cref="CasazenPolicies.OrgBillingAdmin"/> (PL-16).
/// </summary>
public class SharedPropertyPolicyTests
{
    /// <summary>Every action a caller with only the long-rent <c>property.*</c> permissions may pass the policies of.</summary>
    private static readonly string[] SharedPropertyActions =
    [
        "PropertiesController.Create",
        "PropertiesController.DeleteDocument",
        "PropertiesController.DownloadDocument",
        "PropertiesController.GetAll",
        "PropertiesController.GetById",
        "PropertiesController.GetDocumentSignedUrl",
        "PropertiesController.GetDocuments",
        "PropertiesController.Update",
        "PropertiesController.UploadDocument",
    ];

    [Fact]
    public void ParseContextPolicy_SharedPropertyPolicy_HasBothRentalContexts()
    {
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.SharedPropertyWrite);

        Assert.Equal(["short-rent", "long-rent"], contexts);
        Assert.Equal("property.write", permission);
    }

    [Fact]
    public void ParseContextPolicy_ShortRentPropertyPolicy_HasOnlyShortRent()
    {
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.PropertyRead);

        Assert.Equal(["short-rent"], contexts);
        Assert.Equal("property.read", permission);
    }

    [Theory]
    [InlineData("RequireContext:short-rent|:property.read")]
    [InlineData("RequireContext:|long-rent:property.read")]
    [InlineData("RequireContext:short-rent:")]
    public void ParseContextPolicy_MalformedName_Throws(string policyName)
    {
        Assert.Throws<ArgumentException>(() => CasazenPolicies.ParseContextPolicy(policyName));
    }

    [Theory]
    [InlineData("long-rent", true)]
    [InlineData("short-rent", true)]
    [InlineData("supplier", false)]
    public async Task SharedPropertyPolicy_PassesWithThePermissionInEitherRentalContext(string grantingContext, bool expected)
    {
        var passes = await PassesAsync(CasazenPolicies.SharedPropertyRead, (context, permission) =>
            context == grantingContext && permission == "property.read");

        Assert.Equal(expected, passes);
    }

    [Fact]
    public async Task ShortRentPropertyPolicy_WithLongRentPermissionOnly_Fails()
    {
        static bool LongRentOnly(string context, string _) => context == "long-rent";

        Assert.False(await PassesAsync(CasazenPolicies.PropertyRead, LongRentOnly));
        Assert.False(await PassesAsync(CasazenPolicies.PropertyWrite, LongRentOnly));
        Assert.False(await PassesAsync(CasazenPolicies.BookingRead, LongRentOnly));
        Assert.True(await PassesAsync(CasazenPolicies.SharedPropertyWrite, LongRentOnly));
    }

    [Fact]
    public void ActionsOpenToLongRentPropertyPermissions_AreExactlyThePropertyCore()
    {
        // Policies a long-term landlord passes: the shared property ones and its own lease permissions.
        var landlordPolicies = new HashSet<string>(StringComparer.Ordinal)
        {
            CasazenPolicies.SharedPropertyRead,
            CasazenPolicies.SharedPropertyWrite,
            CasazenPolicies.LeaseRead,
            CasazenPolicies.LeaseCreate,
            CasazenPolicies.LeaseSign,
            CasazenPolicies.LeaseRegister,
        };

        var open = Actions()
            .Where(a => !a.IsAnonymous)
            .Where(a => a.Policies.Any(p => p is CasazenPolicies.SharedPropertyRead or CasazenPolicies.SharedPropertyWrite))
            .Where(a => a.Policies.All(p => p is not null && landlordPolicies.Contains(p)))
            .Select(a => a.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(SharedPropertyActions.Order(StringComparer.Ordinal).ToArray(), open);
    }

    [Theory]
    [InlineData(nameof(PropertiesController.GetDetail))]
    [InlineData(nameof(PropertiesController.GetImages))]
    [InlineData(nameof(PropertiesController.UploadImages))]
    [InlineData(nameof(PropertiesController.GetIcalStatus))]
    [InlineData(nameof(PropertiesController.SetIcalImportUrl))]
    [InlineData(nameof(PropertiesController.GetIcalExportUrl))]
    [InlineData(nameof(PropertiesController.GetComplianceActivation))]
    [InlineData(nameof(PropertiesController.GetCinCompliance))]
    [InlineData(nameof(PropertiesController.Delete))]
    public void ShortStayPropertyActions_RequireTheShortRentContext(string action)
    {
        var policies = Actions().Single(a => a.Key == $"{nameof(PropertiesController)}.{action}").Policies;

        Assert.True(
            policies.Contains(CasazenPolicies.PropertyRead) || policies.Contains(CasazenPolicies.PropertyWrite),
            $"{action} must stay short-rent only (calendars, photos, CIN, listing).");
    }

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim("sub", "auth0|landlord")], "TestAuth"));

    /// <summary>
    /// Evaluates the policy registered by <c>AddCasazenAuthorization</c> with the production context handler, the
    /// caller's context permissions coming from <paramref name="hasPermission"/>.
    /// </summary>
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
            .Setup(c => c.HasPermissionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string context, string permission, CancellationToken _) =>
                hasPermission(context, permission));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        var onboarding = new Mock<IHostOnboardingGate>();
        onboarding.Setup(g => g.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Casazen.Core.Authorization.HostOnboardingStatus(true, true));
        services.AddSingleton<IAuthorizationHandler>(new ContextAuthorizationHandler(contexts.Object, onboarding.Object));
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(User(), policy)).Succeeded;
    }

    private sealed record ActionInfo(string Key, bool IsAnonymous, IReadOnlyList<string?> Policies);

    private static IEnumerable<ActionInfo> Actions()
    {
        var controllers = typeof(PropertiesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true });

        foreach (var controller in controllers)
        {
            var controllerAuthorize = controller.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any());

            foreach (var action in actions)
            {
                var attributes = action.GetCustomAttributes(inherit: true);
                var policies = controllerAuthorize
                    .Concat(attributes.OfType<IAuthorizeData>())
                    .Select(a => a.Policy)
                    .ToList();
                yield return new ActionInfo(
                    $"{controller.Name}.{action.Name}", attributes.OfType<IAllowAnonymous>().Any(), policies);
            }
        }
    }
}
