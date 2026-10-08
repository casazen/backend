using Casazen.Core.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// The official ISTAT comuni list (SU-04, D9). It is a configuration of the platform that the product owner applies
/// (<c>docs/runbooks/comuni-istat.md</c>), so a missing list is <c>degraded</c>, not <c>unhealthy</c>: the API works, the comune
/// pickers say "list not available" and nothing is validated against it. Also degraded when a pilot comune of the supplier
/// registration (<c>Suppliers:PilotComuni</c>) is not in the list. The database is checked by <see cref="DatabaseHealthCheck"/>.
/// </summary>
public sealed class ComuniDatasetHealthCheck(
    IComuneDirectory directory,
    ISupplierPilotComuni pilotComuni,
    ILogger<ComuniDatasetHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await directory.GetStatusAsync(cancellationToken);
            if (!status.Available)
            {
                return HealthCheckResult.Degraded(
                    "The official ISTAT comuni list is not imported: comune pickers and validation are unavailable (docs/runbooks/comuni-istat.md).");
            }

            var invalidPilots = await pilotComuni.GetInvalidConfiguredCodesAsync(cancellationToken);
            if (invalidPilots.Count > 0)
            {
                return HealthCheckResult.Degraded(
                    $"{invalidPilots.Count} pilot comune(s) of Suppliers:PilotComuni are not active comuni of the ISTAT list and are not offered: {string.Join(", ", invalidPilots)}.");
            }

            var reference = status.LastImport?.ReferenceDate.ToString("yyyy-MM-dd");
            return HealthCheckResult.Healthy($"ISTAT comuni list imported: {status.ActiveRows} active comuni, reference date {reference}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken database is reported by the database check; this one must not hide it behind a second error.
            logger.LogWarning(ex, "The ISTAT comuni list could not be checked");
            return HealthCheckResult.Degraded("The ISTAT comuni list could not be checked.");
        }
    }
}
