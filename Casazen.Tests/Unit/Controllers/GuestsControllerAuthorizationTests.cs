using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class GuestsControllerAuthorizationTests
{
    [Fact]
    public void GuestsController_ReadEndpointsRequireGuestReadContext()
    {
        var classPolicies = typeof(GuestsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToArray();

        Assert.Contains("PropertyOwner", classPolicies);
        Assert.Contains("RequireContext:short-rent:guest.read", classPolicies);
    }

    [Theory]
    [InlineData(nameof(GuestsController.Create))]
    [InlineData(nameof(GuestsController.Update))]
    [InlineData(nameof(GuestsController.Delete))]
    public void GuestsController_WriteEndpointsRequireGuestWriteContext(string actionName)
    {
        var methodPolicies = typeof(GuestsController)
            .GetMethods()
            .Single(m => m.Name == actionName)
            .GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToArray();

        Assert.Contains("RequireContext:short-rent:guest.write", methodPolicies);
    }
}
