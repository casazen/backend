using Casazen.Core.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Vercel Domains API (BK-17, D9): without <c>Vercel__ApiToken</c> and <c>Vercel__ProjectId</c> the platform cannot add a
/// host's custom domain to the Vercel project, so none can be activated and the settings page says so. Custom domains are an
/// optional feature, so it is <c>degraded</c>, never a startup failure. The description names the variables, never their
/// values. Setup: <c>docs/runbooks/seo-domain.md</c> section 10.
/// </summary>
public sealed class VercelDomainsConfigurationHealthCheck(IOptions<VercelDomainsOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var missing = options.Value.MissingVariables();
        return Task.FromResult(missing.Count == 0
            ? HealthCheckResult.Healthy("Vercel Domains API configured: custom domains can be activated.")
            : HealthCheckResult.Degraded(
                "Custom domains cannot be activated: missing " + string.Join(", ", missing) + " (runbook seo-domain.md, section 10)."));
    }
}
