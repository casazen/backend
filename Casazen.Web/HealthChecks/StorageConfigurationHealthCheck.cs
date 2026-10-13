using Casazen.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Object storage (FD-07). The S3 configuration is validated at startup (<see cref="StorageOptionsValidator"/>), so
/// here S3 is <c>healthy</c>; the local-disk provider, allowed only in Development and Testing, is <c>degraded</c>
/// because files there do not survive a redeploy. No call to the bucket is made on each probe.
/// </summary>
public sealed class StorageConfigurationHealthCheck(IOptions<StorageOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        StorageOptions storage;
        try
        {
            storage = options.Value;
        }
        catch (OptionsValidationException ex)
        {
            // Failures name the Storage:* keys, never their values.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Object storage is not configured: " + string.Join(" ", ex.Failures)));
        }

        return Task.FromResult(storage.UsesS3
            ? HealthCheckResult.Healthy("Object storage: S3 (Supabase Storage).")
            : HealthCheckResult.Degraded("Object storage on the local disk (Development/Testing only): files do not survive a redeploy."));
    }
}
