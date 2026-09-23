using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Email provider and public links (FD-13). Outside Development and Testing both are validated at startup, so this
/// check reports <c>degraded</c> only where the app may run without them (emails are skipped with a warning).
/// </summary>
public sealed class EmailConfigurationHealthCheck(IOptions<EmailOptions> emailOptions, PublicSiteLinks publicSiteLinks)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>(EmailOptionsValidator.GetErrors(emailOptions.Value));
        if (!publicSiteLinks.IsConfigured)
            problems.Add("App__PublicSiteBaseUrl is missing or invalid: links in emails cannot be built.");

        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy("Email provider and public links configured.")
            : HealthCheckResult.Degraded("Emails are not sent: " + string.Join(" ", problems)));
    }
}
