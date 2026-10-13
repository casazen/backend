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

        // TN-3: the context permission alone; the old "PropertyOwner" policy only required a signed-in user.
        Assert.DoesNotContain("PropertyOwner", classPolicies);
        Assert.Contains("RequireContext:short-rent:guest.read", classPolicies);
    }

    [Theory]
    [InlineData(nameof(GuestsController.Create), "RequireContext:short-rent:guest.write")]
    [InlineData(nameof(GuestsController.Update), "RequireContext:short-rent:guest.write")]
    // AM-03: deleting a guest is guest.manage (the collaborator registers and corrects guests, it does not erase them).
    [InlineData(nameof(GuestsController.Delete), "RequireContext:short-rent:guest.manage")]
    public void GuestsController_WriteEndpointsRequireTheirContextPermission(string actionName, string expectedPolicy)
    {
        var methodPolicies = typeof(GuestsController)
            .GetMethods()
            .Single(m => m.Name == actionName)
            .GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToArray();

        Assert.Contains(expectedPolicy, methodPolicies);
        if (actionName == nameof(GuestsController.Delete))
            Assert.DoesNotContain("RequireContext:short-rent:guest.write", methodPolicies);
    }
}
