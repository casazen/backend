using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Loads the official Alloggiati Web code tables from the seed files of the deploy when the database has none
/// (CO-12). A later scheduled download replaces them when the portal file changed. A failure is logged and never
/// stops the API.
/// </summary>
public class AlloggiatiCodeTableSeedHostedService(
    IServiceProvider serviceProvider,
    IOptions<OfficialReferenceDataOptions> options,
    ILogger<AlloggiatiCodeTableSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedAlloggiatiOnStartup)
        {
            logger.LogInformation("Alloggiati code-table seed on startup is off (OfficialReferenceData__SeedAlloggiatiOnStartup)");
            return;
        }

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var tables = scope.ServiceProvider.GetRequiredService<IAlloggiatiCodeTableService>();
            var results = await tables.ImportSeedIfEmptyAsync(cancellationToken);
            foreach (var result in results)
            {
                logger.LogInformation(
                    "Alloggiati {Table} seed on startup: success={Success} unchanged={Unchanged} rows={Rows}",
                    result.Table, result.Success, result.Unchanged, result.RowCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Alloggiati code-table seed on startup failed: import the official tables as admin (docs/runbooks/alloggiati.md)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
