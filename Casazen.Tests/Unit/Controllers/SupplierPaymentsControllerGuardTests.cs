using System.Reflection;
using Casazen.Core.Features;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// SP-14: the shape of the supplier's payments routes is pinned. Every action needs the supplier policy and is behind
/// <c>SupplierOnlinePayments</c> (404 while off, before authentication), none is anonymous, and the whole surface is the
/// account, its state and the two links: no route creates a payment (SP-15), so a payment endpoint added here by mistake fails
/// this test and has to be a deliberate decision.
/// </summary>
public class SupplierPaymentsControllerGuardTests
{
    private static readonly Type Controller = typeof(SupplierPaymentsController);

    [Fact]
    public void Controller_IsBehindTheOnlinePaymentsFlag_AndTheSupplierPolicy()
    {
        var gates = Controller.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag).ToList();
        var authorize = Assert.Single(Controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        Assert.Equal([FeatureFlags.SupplierOnlinePayments], gates);
        Assert.Equal(CasazenPolicies.Supplier, authorize.Policy);
        Assert.Equal("RequireSupplier", authorize.Policy);
        Assert.Empty(Controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void Actions_NoneIsAnonymous_AndNoneOverridesTheController()
    {
        Assert.All(Actions(), action =>
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        });
    }

    [Fact]
    public void Routes_AreTheAccountItsStateAndTheTwoLinks_NothingThatCreatesAPayment()
    {
        var prefix = Assert.Single(Controller.GetCustomAttributes<RouteAttribute>(inherit: true)).Template;

        var routes = Actions()
            .SelectMany(action => action.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                .Select(attribute => $"{string.Join(",", attribute.HttpMethods)} {prefix}/{attribute.Template}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                "GET api/supplier/payments/account",
                "POST api/supplier/payments/account",
                "POST api/supplier/payments/dashboard-link",
                "POST api/supplier/payments/onboarding-link",
            },
            routes);
    }

    private static IEnumerable<MethodInfo> Actions() =>
        Controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any());
}
