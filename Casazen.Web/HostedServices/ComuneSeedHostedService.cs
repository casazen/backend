using Casazen.Core.Services;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Loads the official ISTAT comuni list from the seed file of the deploy (<c>Data/Seeds/comuni-istat.csv</c>) at startup, when
/// the build has the file and the database has no list or an older one (SU-04). Registered before the SEO bootstrap, which
/// needs the list: the import (one second) completes before the next hosted service starts. A failure is logged and never
/// stops the API: the list can still be imported by an admin (<c>docs/runbooks/comuni-istat.md</c>). Replicas starting
/// together import once (advisory lock inside the import, and the reference date check).
/// </summary>
public class ComuneSeedHostedService(
    IServiceProvider serviceProvider,
    IOptions<ComuniOptions> options,
    ILogger<ComuneSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedOnStartup)
        {
            logger.LogInformation("Comuni seed on startup is off (Comuni__SeedOnStartup)");
            return;
        }

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var importService = scope.ServiceProvider.GetRequiredService<IComuneImportService>();
            var outcome = await importService.ImportSeedIfNewerAsync(cancellationToken);
            logger.LogInformation("Comuni seed on startup: {Outcome}", outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The API starts anyway: without the list the pickers say so and the health check is degraded.
            logger.LogError(ex, "Comuni seed on startup failed: import the official list as admin (docs/runbooks/comuni-istat.md)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
