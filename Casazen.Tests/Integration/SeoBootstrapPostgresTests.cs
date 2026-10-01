using Casazen.Core.Repositories;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.Configuration;
using Casazen.Web.HostedServices;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-21 (A8-26) on the real Hangfire PostgreSQL storage: two starts of the application (a redeploy) and two replicas
/// starting together queue the SEO bootstrap generation only once, even while no page exists yet.
/// </summary>
public class SeoBootstrapPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync("seoboot");

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task StartAsync_RedeployAndConcurrentReplicas_QueueGenerationOnce()
    {
        var settings = new HangfireStorageSettings("hangfire_seo_bootstrap", HangfireStorageSettings.DefaultDistributedLockTimeout);
        var options = settings.CreateStorageOptions();
        var storage = new PostgreSqlStorage(new NpgsqlConnectionFactory(_database!.ConnectionString, options, null), options);

        // First deploy: two replicas start at the same time.
        await Task.WhenAll(
            Task.Run(() => CreateService(storage).StartAsync(CancellationToken.None)),
            Task.Run(() => CreateService(storage).StartAsync(CancellationToken.None)));
        // Redeploy: the generation produced no page (e.g. it failed), the page count is still zero.
        await CreateService(storage).StartAsync(CancellationToken.None);

        Assert.Equal(1, storage.GetMonitoringApi().EnqueuedCount("default"));
        using var connection = storage.GetConnection();
        Assert.Contains(SeoBootstrapHostedService.EnqueuedAtField, connection.GetAllEntriesFromHash(SeoBootstrapHostedService.MarkerKey)!.Keys);
    }

    private static SeoBootstrapHostedService CreateService(JobStorage storage)
    {
        var repository = new Mock<ISeoContentRepository>();
        repository.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddSingleton(storage);
        services.AddSingleton<IBackgroundJobClient>(new BackgroundJobClient(storage));
        services.AddSingleton(repository.Object);

        return new SeoBootstrapHostedService(
            services.BuildServiceProvider(),
            Options.Create(new SeoBootstrapOptions { BootstrapOnStartup = true }),
            NullLogger<SeoBootstrapHostedService>.Instance);
    }
}
