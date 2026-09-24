using Casazen.Web.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Auth0: JWT validation settings and the Management API client used for the role sync (FD-14; <c>degraded</c>
/// without it or with the deprecated static token). Without Domain/Audience nobody can sign in: <c>unhealthy</c> where
/// the configuration is enforced (there the app does not even start), <c>degraded</c> in Development and Testing.
/// </summary>
public sealed class Auth0ConfigurationHealthCheck(IOptions<Auth0Options> options, IHostEnvironment environment) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var auth0 = options.Value;

        var errors = Auth0OptionsValidator.GetErrors(auth0);
        if (errors.Count > 0)
        {
            var description = "Authentication is not configured: " + string.Join(" ", errors);
            return Task.FromResult(RequiredConfiguration.IsEnforced(environment)
                ? HealthCheckResult.Unhealthy(description)
                : HealthCheckResult.Degraded(description));
        }

        if (auth0.UsesLegacyManagementToken)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Auth0 Management API uses the deprecated static Auth0__ManagementApiToken, which expires: set " +
                "Auth0__ManagementClientId and Auth0__ManagementClientSecret (docs/runbooks/auth0.md)."));
        }

        return Task.FromResult(auth0.HasManagementClient
            ? HealthCheckResult.Healthy("JWT validation and Auth0 Management API client configured.")
            : HealthCheckResult.Degraded(
                "Auth0 Management API client missing (Auth0__ManagementClientId, Auth0__ManagementClientSecret): " +
                "roles are not synchronized to Auth0 (docs/runbooks/auth0.md)."));
    }
}
