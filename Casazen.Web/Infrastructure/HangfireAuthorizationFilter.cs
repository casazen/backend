using System.Security.Claims;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Authorization filter for Hangfire Dashboard.
/// Allows access via <c>X-Hangfire-ApiKey</c> header or authenticated Admin role.
/// </summary>
public class HangfireAuthorizationFilter(IConfiguration configuration) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        AuthorizeRequest(context.GetHttpContext(), configuration);

    public static bool AuthorizeRequest(HttpContext? httpContext, IConfiguration configuration)
    {
        if (httpContext is null)
            return false;

        var dashboardApiKey = configuration["Hangfire:DashboardApiKey"];
        if (!string.IsNullOrEmpty(dashboardApiKey))
        {
            var providedKey = httpContext.Request.Headers["X-Hangfire-ApiKey"].FirstOrDefault();
            if (string.Equals(providedKey, dashboardApiKey, StringComparison.Ordinal))
                return true;
        }

        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        var roles = Auth0RolesClaimParser.Parse(
            user.FindAll("https://casazen.app/roles").Select(c => c.Value));
        if (roles.Count == 0)
            roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        return roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
    }
}
