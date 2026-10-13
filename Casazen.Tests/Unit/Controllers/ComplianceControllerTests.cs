using System.Reflection;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class ComplianceControllerTests
{
    [Fact]
    public void GetSummary_RequiresShortRentBookingReadContext()
    {
        var method = typeof(ComplianceController).GetMethod(nameof(ComplianceController.GetSummary))
            ?? throw new InvalidOperationException("GetSummary action not found.");

        var policies = method.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToList();

        Assert.Contains("RequireContext:short-rent:booking.read", policies);
    }
}
