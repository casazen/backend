using System.Globalization;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Options;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Seeds SEO pages on first deploy when the sitemap would otherwise be empty. The generation is queued <b>once per
/// environment</b> (A8-26): under a Hangfire distributed lock, so several replicas starting together queue it once,
/// and with a marker in the Hangfire storage of this environment, so a failed or empty generation is not queued
/// (and paid) again at every deploy. Rerun it from the admin SEO dashboard ("Genera"); runbook
/// <c>docs/runbooks/ai.md</c>.
/// </summary>
public class SeoBootstrapHostedService(
    IServiceProvider serviceProvider,
    IOptions<SeoBootstrapOptions> options,
    ILogger<SeoBootstrapHostedService> logger) : IHostedService
{
    /// <summary>Hangfire hash holding the bootstrap marker (field <see cref="EnqueuedAtField"/>).</summary>
    public const string MarkerKey = "casazen:seo-bootstrap";

    public const string EnqueuedAtField = "EnqueuedAt";

    public const string JobIdField = "JobId";

    private const string LockResource = "casazen:seo-bootstrap:lock";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.BootstrapOnStartup)
            return;

        var storage = serviceProvider.GetService<JobStorage>();
        if (storage is null)
        {
            logger.LogInformation("SEO bootstrap skipped: Hangfire is not configured");
            return;
        }

        try
        {
            using var connection = storage.GetConnection();
            using var distributedLock = connection.AcquireDistributedLock(LockResource, LockTimeout);

            var marker = connection.GetAllEntriesFromHash(MarkerKey);
            if (marker is not null && marker.TryGetValue(EnqueuedAtField, out var enqueuedAt))
            {
                logger.LogInformation("SEO bootstrap skipped: already queued on {EnqueuedAt}", enqueuedAt);
                return;
            }

            await using var scope = serviceProvider.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISeoContentRepository>();
            var existingCount = await repository.CountAllPagesAsync(cancellationToken);
            if (existingCount > 0)
            {
                logger.LogInformation("SEO bootstrap skipped: {Count} pages already exist", existingCount);
                return;
            }

            var codes = ItalianComuneRegistry.AllCodes;
            var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var jobId = jobClient.Enqueue<SeoPageGenerationJob>(job =>
                job.ExecuteAsync(codes, options.Value.AutoApproveAfterBootstrap));

            connection.SetRangeInHash(MarkerKey,
            [
                new KeyValuePair<string, string>(EnqueuedAtField, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(JobIdField, jobId ?? string.Empty),
            ]);
            logger.LogInformation("SEO bootstrap: queued generation for {Count} comuni (job {JobId})", codes.Count, jobId);
        }
        catch (DistributedLockTimeoutException)
        {
            logger.LogInformation("SEO bootstrap skipped: another instance holds the bootstrap lock");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
