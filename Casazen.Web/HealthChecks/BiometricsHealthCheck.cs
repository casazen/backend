using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// The model files of the identity verification (E22/F01/US01/T01, ADR-013-R8): a non-blocking entry <c>biometrics</c>, like <c>einvoicing</c>.
/// <c>Healthy</c> while <c>Features:CheckInIdentityVerification</c> is off or every model is ready; <c>Degraded</c> while the models load and, with
/// the flag on, when one cannot be used, the description naming it with the failure code of the model store (<c>hash_not_configured</c>,
/// <c>file_missing</c>, <c>hash_mismatch</c>, ...). <b>Never <c>Unhealthy</c></b>: without the models every verification is the operator path,
/// which keeps working, so a missing model must not take the instance out of rotation (503). The description names models and codes, never
/// a path, a hash or an address. Runbook: <c>docs/runbooks/biometric-models.md</c>.
/// </summary>
public sealed class BiometricsHealthCheck(IBiometricModelStatusReader statusReader) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        BiometricModelsStatus status;
        try
        {
            status = statusReader.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A configuration that cannot even be read is a reason to look, not to stop serving: the operator path does not need the models.
            return Task.FromResult(HealthCheckResult.Degraded(
                "The state of the identity verification models could not be read (check the Biometrics__* and MrzOcr__* variables): every verification goes to the operator."));
        }

        return Task.FromResult(status.State switch
        {
            BiometricModelStatusStates.Disabled =>
                HealthCheckResult.Healthy("Identity verification is off (Features__CheckInIdentityVerification): no model is loaded."),
            BiometricModelStatusStates.Ready =>
                HealthCheckResult.Healthy("Identity verification models are ready."),
            BiometricModelStatusStates.Loading =>
                HealthCheckResult.Degraded("Identity verification models are still loading: until they are ready every verification goes to the operator."),
            _ => HealthCheckResult.Degraded(Describe(status)),
        });
    }

    private static string Describe(BiometricModelsStatus status)
    {
        var failures = status.Models
            .Where(model => model.State == BiometricModelStatusStates.Failed)
            .Select(model => $"{model.Role}: {model.Code}");
        return "Identity verification is on but some models cannot be used (" + string.Join("; ", failures)
               + "): every verification goes to the operator. Fix: docs/runbooks/biometric-models.md.";
    }
}
