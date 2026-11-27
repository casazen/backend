using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Loads the official Swiss localities and municipalities (the swisstopo directory, E29/F01/US01/T01, NF-124) from the seed file of the deploy at
/// startup (<see cref="SwissMunicipalitySeed"/>): the two tables are made equal to the file when the database does not have that file yet (its
/// SHA-256 in <c>OfficialSourceFetches</c>), so a database that already has it is left alone and one with the old hand-written list is corrected.
/// A municipality the file no longer has stays while a property still carries its number (a warning says how many, to be confirmed by the hosts).
/// A build without the file only logs a warning, a file that does not parse or a failure is logged as an error: the API starts anyway.
/// <c>OfficialReferenceData__SeedSwissPlacesOnStartup=false</c> turns it off.
/// </summary>
public class SwissPlacesSeedHostedService(
    IServiceProvider serviceProvider,
    IOptions<OfficialReferenceDataOptions> options,
    ILogger<SwissPlacesSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedSwissPlacesOnStartup)
        {
            logger.LogInformation("Swiss places seed on startup is off (OfficialReferenceData__SeedSwissPlacesOnStartup)");
            return;
        }

        await SeedAsync(SwissMunicipalitySeed.ReadEmbedded(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Loads <paramref name="seed"/> (the file of the build, <c>null</c> when the build has none) and logs what happened; never throws but on cancellation.</summary>
    public async Task SeedAsync(byte[]? seed, CancellationToken cancellationToken)
    {
        if (seed is null)
        {
            logger.LogWarning(
                "Swiss places seed file is not part of this build: the Swiss localities and municipalities are not loaded (Data/Seeds/swiss/README.md)");
            return;
        }

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            var result = await SwissMunicipalitySeed.ApplyAsync(db, seed, clock, cancellationToken);
            logger.LogInformation(
                "Swiss places seed on startup: imported={Imported} municipalities={Municipalities} localities={Localities} removedMunicipalities={RemovedMunicipalities} removedLocalities={RemovedLocalities} sha256={Sha256}",
                result.Imported, result.Municipalities, result.Localities, result.MunicipalitiesRemoved, result.LocalitiesRemoved, result.Sha256);

            // A municipality the directory dropped (a merger) that a property still carries is kept: its host has to confirm the new one.
            if (result.MunicipalitiesKept > 0)
            {
                logger.LogWarning(
                    "Swiss places seed on startup: {KeptMunicipalities} municipality number(s) the directory no longer has are kept because {Properties} property(ies) still carry them: to be confirmed by the hosts (docs/compliance/swiss-municipalities.md)",
                    result.MunicipalitiesKept, result.PropertiesOnKept);
            }
        }
        catch (FormatException ex)
        {
            logger.LogError(ex, "Swiss places seed file is not valid and was not loaded: rebuild it (Data/Seeds/swiss/README.md)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Swiss places seed on startup failed: the next startup tries again (Data/Seeds/swiss/README.md)");
        }
    }
}
