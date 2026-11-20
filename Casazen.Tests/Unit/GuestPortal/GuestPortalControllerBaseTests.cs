using System.Reflection;
using Casazen.Core.Features;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

/// <summary>
/// E50/F01/US01/T02 (NF-152, NF-153, AC-NF153-2): a controller of the portal derives from <see cref="GuestPortalControllerBase"/> and inherits, with no
/// attribute of its own, the flag, the anonymity, the rate limit, the private answers, the token filter and the route of the token; no endpoint of the
/// portal reads a booking id from the client.
/// </summary>
public class GuestPortalControllerBaseTests
{
    /// <summary>
    /// Permission and privacy rules: a controller written by a later epic (here one of the tests) and the overview get the gate of
    /// <c>Features:GuestPortal</c>, the limiter <c>GuestPortal</c> (per IP and token hash), <c>no-store</c>, the token filter after it and the route
    /// <c>api/public/guest-portal/{token}</c>, and ask for no sign-in.
    /// </summary>
    [Fact]
    public void DerivedController_InheritsGateLimiterFilter()
    {
        foreach (var controller in new[] { typeof(SampleGuestPortalController), typeof(GuestPortalOverviewController) })
        {
            var attributes = controller.GetCustomAttributes(inherit: true);

            Assert.Contains(attributes, a => a is AllowAnonymousAttribute);
            Assert.Empty(attributes.OfType<AuthorizeAttribute>());
            Assert.Equal([FeatureFlags.GuestPortal], attributes.OfType<FeatureGateAttribute>().Select(g => g.Flag));
            Assert.Equal(RateLimitPolicies.GuestPortal, Assert.Single(attributes.OfType<EnableRateLimitingAttribute>()).PolicyName);
            Assert.Single(attributes.OfType<PrivateAnswerAttribute>());
            var filter = Assert.Single(attributes.OfType<ServiceFilterAttribute>());
            Assert.Equal(typeof(GuestPortalTokenFilter), filter.ServiceType);
            Assert.True(filter.Order > 0, "The token filter runs after the private answer, so its 404 is private too.");
            Assert.Equal("api/public/guest-portal/{token}", Assert.Single(attributes.OfType<RouteAttribute>()).Template);
            Assert.Contains(attributes, a => a is ApiControllerAttribute);
        }

        // The two policies of the portal: per client IP and a hash of the token, 120 and 30 a minute.
        var read = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.GuestPortal);
        var write = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.GuestPortalWrite);
        Assert.Equal((120, TimeSpan.FromMinutes(1), true, false), (read.DefaultPermitLimit, read.DefaultWindow, read.PartitionByToken, read.PartitionByUser));
        Assert.Equal((30, TimeSpan.FromMinutes(1), true, false), (write.DefaultPermitLimit, write.DefaultWindow, write.PartitionByToken, write.PartitionByUser));
        Assert.Equal(GuestPortalTokenFilter.TokenRouteValue, read.TokenRouteValue);
    }

    /// <summary>
    /// Permission rule (AC-NF153-3 of the analysis, decision A3): every action of every controller of the portal takes the stay from the token only. No
    /// parameter and no route value names a booking.
    /// </summary>
    [Fact]
    public void PortalActions_NeverReadABookingIdFromTheClient()
    {
        var controllers = typeof(GuestPortalControllerBase).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(GuestPortalControllerBase)))
            .ToList();
        Assert.Contains(typeof(GuestPortalOverviewController), controllers);

        foreach (var action in controllers.SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
        {
            var templates = action.GetCustomAttributes<HttpMethodAttribute>().Select(a => a.Template ?? string.Empty).ToList();
            Assert.DoesNotContain(templates, t => t.Contains("booking", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(action.GetParameters(), p => p.Name!.Contains("booking", StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>What a later epic writes: a controller of the portal with one action and no attribute of its own. Not public: MVC never serves it.</summary>
internal sealed class SampleGuestPortalController : GuestPortalControllerBase
{
    [HttpGet("sample")]
    public IActionResult Get() => Ok(Portal.BookingId);
}
