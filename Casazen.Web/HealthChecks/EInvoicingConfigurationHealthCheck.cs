using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Italian e-invoicing of the CasaZen SaaS invoices (PL-13, decision D9). This build integrates no SDI provider, so the
/// check is <c>degraded</c> (never a startup failure, like the other optional integrations): every paid invoice is
/// marked <c>manual_required</c> and must be issued by hand from the admin queue. The description says whether the
/// product owner accepted the manual issuance (<c>Sdi__ManualIssuanceAccepted</c>, which opens the live checkout with
/// <c>Billing__VatNumber</c>) and names missing variables, never values. Runbook: <c>docs/runbooks/billing-tax.md</c>.
/// </summary>
public sealed class EInvoicingConfigurationHealthCheck(
    IConfiguration configuration,
    ISdiEInvoiceProvider sdiProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        if (RequiredConfiguration.IsMissing(configuration["Billing:VatNumber"]))
            problems.Add("missing Billing__VatNumber (CasaZen VAT number): the live plan checkout stays closed.");

        if (sdiProvider.IsConfigured)
        {
            return Task.FromResult(problems.Count == 0
                ? HealthCheckResult.Healthy($"SDI e-invoicing provider '{sdiProvider.Name}' configured.")
                : HealthCheckResult.Degraded(string.Join(" ", problems)));
        }

        problems.Insert(
            0,
            configuration.GetValue(BillingEntryGate.ManualIssuanceAcceptedKey, false)
                ? "No SDI e-invoicing provider: paid platform invoices are marked manual_required and must be issued by hand (Sdi__ManualIssuanceAccepted=true)."
                : "No SDI e-invoicing provider and Sdi__ManualIssuanceAccepted is not set: the live plan checkout stays closed.");
        return Task.FromResult(HealthCheckResult.Degraded(string.Join(" ", problems)));
    }
}
