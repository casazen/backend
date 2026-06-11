using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Seeds SEO pages on first deploy when the sitemap would otherwise be empty.
/// </summary>
public class SeoBootstrapHostedService(
    IServiceProvider serviceProvider,
    IOptions<SeoBootstrapOptions> options,
    ILogger<SeoBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.BootstrapOnStartup)
            return;

        await using var scope = serviceProvider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISeoContentRepository>();
        var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();

        var existingCount = await repository.CountAllPagesAsync(cancellationToken);
        if (existingCount > 0)
        {
            logger.LogInformation("SEO bootstrap skipped: {Count} pages already exist", existingCount);
            return;
        }

        var codes = ItalianComuneRegistry.AllCodes;
        logger.LogInformation("SEO bootstrap: enqueueing generation for {Count} comuni", codes.Count);

        jobClient.Enqueue<SeoPageGenerationJob>(job =>
            job.ExecuteAsync(codes, options.Value.AutoApproveAfterBootstrap));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
