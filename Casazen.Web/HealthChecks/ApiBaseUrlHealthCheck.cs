using Casazen.Web.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Public URL of this API (<c>App__ApiBaseUrl</c>, DEPLOY-CFG): the base of the iCal export links that hosts paste into
/// Airbnb, Booking.com and the other channels (<c>PropertyICalSyncService.BuildExportUrl</c>). It has no default in
/// <c>appsettings.json</c> (decision D3: no domain in code); without it the links would be built on
/// <c>https://localhost:5001</c> and the channels would never receive the calendar, with no error anywhere. The API
/// still starts (the rest of it does not need the value), so a missing or wrong value is <c>degraded</c>, never a startup
/// failure. The value must be this environment's own Railway URL: the test environment must not publish the production
/// host, and the check cannot tell which is which, so the checklist says so (<c>docs/runbooks/deploy-checklist.md</c>).
/// </summary>
public sealed class ApiBaseUrlHealthCheck(IConfiguration configuration, IHostEnvironment environment) : IHealthCheck
{
    public const string Variable = "App__ApiBaseUrl";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problem = GetProblem(configuration["App:ApiBaseUrl"], RequiredConfiguration.IsEnforced(environment));
        return Task.FromResult(problem is null
            ? HealthCheckResult.Healthy("Public API URL configured.")
            : HealthCheckResult.Degraded(problem));
    }

    /// <summary>What is wrong with the value, naming the variable and never the value; null when it is usable.</summary>
    public static string? GetProblem(string? value, bool requireHttps)
    {
        if (RequiredConfiguration.IsMissing(value))
        {
            return $"{Variable} is missing: the iCal export links given to hosts cannot be built (they would point to " +
                   "https://localhost:5001). Set the public URL of this API (docs/runbooks/deploy-checklist.md).";
        }

        if (!Uri.TryCreate(value!.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return $"{Variable} is not an absolute http(s) URL without query string or credentials: the iCal export links are invalid.";
        }

        return requireHttps && uri.Scheme != Uri.UriSchemeHttps
            ? $"{Variable} must use https: hosts paste the iCal export link into other platforms."
            : null;
    }
}
