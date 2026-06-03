using Hangfire.Dashboard;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Authorization filter for Hangfire Dashboard
/// Restricts access to Development environment only for security
/// </summary>
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly bool _isDevelopment;

    public HangfireAuthorizationFilter(bool isDevelopment)
    {
        _isDevelopment = isDevelopment;
    }

    public bool Authorize(DashboardContext context)
    {
        // Only allow access in Development environment
        // In production, implement proper authentication (Auth0, API keys, etc.)
        return _isDevelopment;
    }
}
