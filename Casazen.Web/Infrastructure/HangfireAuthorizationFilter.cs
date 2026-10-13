using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
            if (providedKey is not null && ApiKeysMatch(providedKey, dashboardApiKey))
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

    /// <summary>
    /// Constant-time comparison of the dashboard API key (FD-17, A1-34): an ordinal string comparison stops at the
    /// first different character, so its timing reveals how much of a guess is right. Both keys are hashed first
    /// because <see cref="CryptographicOperations.FixedTimeEquals"/> returns early when the lengths differ.
    /// </summary>
    public static bool ApiKeysMatch(string providedKey, string expectedKey)
    {
        Span<byte> provided = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(providedKey), provided);
        SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey), expected);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
