using Casazen.Web.Authorization;
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

    /// <summary>
    /// TN-3: host controllers carry a context read permission at class level, never the old "PropertyOwner" policy
    /// (which only required a signed-in user, suppliers included).
    /// </summary>
    [Theory]
    [InlineData(typeof(PropertiesController), CasazenPolicies.SharedPropertyRead)]
    [InlineData(typeof(BookingsController), CasazenPolicies.BookingRead)]
    [InlineData(typeof(BookingCancellationController), CasazenPolicies.BookingRead)]
    [InlineData(typeof(BookingLifecycleController), CasazenPolicies.BookingRead)]
    [InlineData(typeof(GuestsController), CasazenPolicies.GuestRead)]
    [InlineData(typeof(PaymentsController), CasazenPolicies.PaymentRead)]
    [InlineData(typeof(OtaController), CasazenPolicies.OtaRead)]
    [InlineData(typeof(OtaIntegrationsController), CasazenPolicies.OtaRead)]
    [InlineData(typeof(PricingAdapterController), CasazenPolicies.PropertyRead)]
    [InlineData(typeof(AlloggiatiController), CasazenPolicies.BookingRead)]
    [InlineData(typeof(ComplianceController), CasazenPolicies.BookingRead)]
    [InlineData(typeof(FiscalController), CasazenPolicies.PropertyRead)]
    [InlineData(typeof(LeasesController), CasazenPolicies.LeaseRead)]
    [InlineData(typeof(CanoneConcordatoController), CasazenPolicies.LeaseRead)]
    public void HostController_ClassPolicyIsAContextReadPermission(Type controllerType, string expectedPolicy)
    {
        var classPolicies = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToArray();

        Assert.Contains(expectedPolicy, classPolicies);
        Assert.DoesNotContain("PropertyOwner", classPolicies);
        Assert.DoesNotContain("LongTermLandlord", classPolicies);
        Assert.DoesNotContain(classPolicies, policy => policy is null);
    }

    [Theory]
    [InlineData(typeof(GdprController), nameof(GdprController.ExportGuestData), "RequireContext:short-rent:guest.read")]
    [InlineData(typeof(GdprController), nameof(GdprController.DeleteGuestData), "RequireContext:short-rent:guest.write")]
    [InlineData(typeof(GdprController), nameof(GdprController.AnonymizeGuestData), "RequireContext:short-rent:guest.write")]
    [InlineData(typeof(GdprController), nameof(GdprController.UpdateConsent), "RequireContext:short-rent:guest.write")]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Create), "RequireContext:short-rent:payment.write")]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.Refund), "RequireContext:short-rent:payment.write")]
    [InlineData(typeof(BookingCancellationController), nameof(BookingCancellationController.Cancel), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(BookingLifecycleController), nameof(BookingLifecycleController.Update), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(BookingLifecycleController), nameof(BookingLifecycleController.CheckIn), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(BookingLifecycleController), nameof(BookingLifecycleController.CheckOut), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(BookingLifecycleController), nameof(BookingLifecycleController.StartCheckoutWizard), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(BookingLifecycleController), nameof(BookingLifecycleController.CompleteCheckoutWizard), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(BookingLifecycleController), nameof(BookingLifecycleController.Quote), "RequireContext:short-rent:booking.write")]
    [InlineData(typeof(PricingAdapterController), nameof(PricingAdapterController.SaveConfig), "RequireContext:short-rent:property.write")]
    [InlineData(typeof(PricingAdapterController), nameof(PricingAdapterController.DisableConfig), "RequireContext:short-rent:property.write")]
    [InlineData(typeof(PricingAdapterController), nameof(PricingAdapterController.TriggerSync), "RequireContext:short-rent:property.write")]
    [InlineData(typeof(ServiceRequestsController), nameof(ServiceRequestsController.Create), "RequireContext:short-rent:property.write")]
    [InlineData(typeof(ServiceRequestsController), nameof(ServiceRequestsController.MatchSupplier), "RequireContext:short-rent:property.write")]
    [InlineData(typeof(ServiceRequestsController), nameof(ServiceRequestsController.MarkPaid), "RequireContext:short-rent:property.write")]
    [InlineData(typeof(ServiceRequestsController), nameof(ServiceRequestsController.Take), "RequireSupplier")]
    [InlineData(typeof(ServiceRequestsController), nameof(ServiceRequestsController.Complete), "RequireSupplier")]
    [InlineData(typeof(ServiceRequestsController), nameof(ServiceRequestsController.Reject), "RequireSupplier")]
    [InlineData(typeof(SuppliersController), nameof(SuppliersController.GetSuppliers), "RequireContext:short-rent:property.read")]
    [InlineData(typeof(OtaIntegrationsController), nameof(OtaIntegrationsController.Create), "RequireContext:short-rent:ota.write")]
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
