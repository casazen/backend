using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// A property manager may approve a maintenance request (<c>lease.create</c>) and must not debit the org's linked account.
/// That debit is <c>OrgBillingAdmin</c>, like the other bank-link payments.
/// </summary>
public class LeaseMaintenancePaymentAuthorizationTests
{
    [Fact]
    public async Task PropertyManager_WhoCanApprove_CannotPay()
    {
        var decide = typeof(LeaseMaintenanceRequestsController).GetMethod(nameof(LeaseMaintenanceRequestsController.Decide))!;
        var pay = typeof(LeaseMaintenanceRequestsController).GetMethod(nameof(LeaseMaintenanceRequestsController.Pay))!;

        Assert.Contains(CasazenPolicies.LeaseCreate, Policies(decide));
        Assert.Contains(CasazenPolicies.OrgBillingAdmin, Policies(pay));
        Assert.DoesNotContain(CasazenPolicies.LeaseCreate, Policies(pay));

        var handler = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyManager, ["long-rent/property_manager"]));
        var context = OrgBillingAdminAuthorizationHandlerTests.Context([new Claim(ClaimTypes.Role, "PropertyManager")]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static List<string> Policies(MethodInfo action) =>
        action.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(attribute => attribute.Policy)
            .Where(policy => policy is not null)
            .Select(policy => policy!)
            .ToList();
}
