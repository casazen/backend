using Casazen.Core.Services;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Loads the admin AI override once at startup, so the subprocessor list and the first generation see it. A database
/// that does not have the table yet keeps the server configuration.
/// </summary>
public sealed class AiSettingsWarmupHostedService(
    IAiRuntimeSettings settings,
    ILogger<AiSettingsWarmupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await settings.GetEffectiveAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI settings were not loaded from the database; the server configuration stays in effect.");
        }
    }
}
