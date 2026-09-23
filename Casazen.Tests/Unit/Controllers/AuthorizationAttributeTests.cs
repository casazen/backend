using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// Verifies that security-critical [Authorize] attributes are present on all protected controllers.
/// Guards against accidental removal or commenting-out of authorization during debugging.
/// See GitHub issues #95, #99-#104.
/// </summary>
public class AuthorizationAttributeTests
{
    [Theory]
    [InlineData(typeof(PropertiesController))]
    [InlineData(typeof(BookingsController))]
    [InlineData(typeof(GuestsController))]
    [InlineData(typeof(PaymentsController))]
    [InlineData(typeof(OtaController))]
    [InlineData(typeof(OtaIntegrationsController))]
    public void Controller_MustHaveAuthorizeAttribute(Type controllerType)
    {
        // Arrange & Act
        var authorizeAttr = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .FirstOrDefault();

        // Assert
        Assert.True(
            authorizeAttr is not null,
            $"{controllerType.Name} is missing [Authorize] attribute. " +
            "All protected controllers must require authentication. " +
            "See GitHub issues #95, #99-#104 for context."
        );
    }

    [Theory]
    [InlineData(typeof(PropertiesController))]
    [InlineData(typeof(BookingsController))]
    [InlineData(typeof(GuestsController))]
    [InlineData(typeof(PaymentsController))]
    [InlineData(typeof(OtaController))]
    [InlineData(typeof(OtaIntegrationsController))]
    public void Controller_AuthorizePolicyMustBePropertyOwner(Type controllerType)
    {
        // Arrange & Act
        var authorizeAttr = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .FirstOrDefault();

        // Assert
        Assert.NotNull(authorizeAttr);
        Assert.True(
            authorizeAttr.Policy == "PropertyOwner",
            $"{controllerType.Name} must use [Authorize(Policy = \"PropertyOwner\")]. " +
            $"Current policy: '{authorizeAttr.Policy ?? "(none)"}'. " +
            "Do not use bare [Authorize] without a policy."
        );
    }

    [Fact]
    public void HealthController_ShouldNotHaveAuthorizeAttribute()
    {
        // HealthController is intentionally public — verify it stays that way
        var authorizeAttr = typeof(HealthController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .FirstOrDefault();

        Assert.Null(authorizeAttr);
    }

    [Theory]
    [InlineData(typeof(GdprController), nameof(GdprController.ExportGuestData), "RequireContext:short-rent:guest.read")]
    [InlineData(typeof(GdprController), nameof(GdprController.DeleteGuestData), "RequireContext:short-rent:guest.write")]
    [InlineData(typeof(GdprController), nameof(GdprController.AnonymizeGuestData), "RequireContext:short-rent:guest.write")]
    [InlineData(typeof(GdprController), nameof(GdprController.UpdateConsent), "RequireContext:short-rent:guest.write")]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Process), "RequireContext:short-rent:payment.write")]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Refund), "RequireContext:short-rent:payment.write")]
    public void SensitiveAction_MustRequireExpectedContextPolicy(
        Type controllerType,
        string actionName,
        string expectedPolicy)
    {
        var action = controllerType.GetMethod(actionName);

        Assert.NotNull(action);
        var authorizeAttr = action
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .FirstOrDefault(attr => attr.Policy == expectedPolicy);

        Assert.NotNull(authorizeAttr);
    }
}
