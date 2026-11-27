using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.HostedServices;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// E09/F01/US01/T02 (NF-54): the cache of Octorate's portal catalog is written only by the daily <see cref="MarketplacePortalRefreshJob"/> (03:30 UTC), so a
/// new environment listed no portal until the first night and the wizard «Collega i tuoi canali» had nothing to choose. A start with
/// <c>Features:OctorateConnector</c> on and no portal listed queues one run of that job on Hangfire; it is idempotent and never holds or stops the start.
/// </summary>
public class MarketplacePortalBootstrapHostedServiceTests
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly Mock<IOctorateApiClient> _octorate = new(MockBehavior.Strict);

    public MarketplacePortalBootstrapHostedServiceTests() =>
        _jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-portals-1");

    [Fact]
    public async Task QueueRefreshWhenCacheIsEmpty_ConnectorOnAndNoPortalListed_QueuesOneFireAndForgetRunOfTheDailyJob()
    {
        var queued = await CreateService().QueueRefreshWhenCacheIsEmptyAsync(CancellationToken.None);

        Assert.True(queued);
        VerifyQueued(Times.Once());
        _jobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QueueRefreshWhenCacheIsEmpty_ConnectorOff_QueuesNothingWhateverTheCacheHolds()
    {
        var queued = await CreateService(connectorOn: false).QueueRefreshWhenCacheIsEmptyAsync(CancellationToken.None);

        Assert.False(queued);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    /// <summary>The cache counts as filled only with a portal the marketplace lists: one the refresh no longer returns, or one Octorate does not offer, is not one.</summary>
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task QueueRefreshWhenCacheIsEmpty_OnlyAPortalListedAndEnabledMeansTheCacheIsFilled(bool listed, bool enabled, bool expectedQueued)
    {
        await SeedAsync(Portal("142", listed, enabled));

        var queued = await CreateService().QueueRefreshWhenCacheIsEmptyAsync(CancellationToken.None);

        Assert.Equal(expectedQueued, queued);
        VerifyQueued(expectedQueued ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// Idempotent: the run that was queued is the daily job itself, which upserts the portals by code. Once it filled the cache a restart queues nothing,
    /// and a second run of it (another instance that started at the same moment, or the schedule of the night) changes nothing.
    /// </summary>
    [Fact]
    public async Task QueueRefreshWhenCacheIsEmpty_OnceTheQueuedRunFilledTheCache_AStartQueuesNothingAndARepeatedRunChangesNothing()
    {
        var service = CreateService();
        Assert.True(await service.QueueRefreshWhenCacheIsEmptyAsync(CancellationToken.None));

        _octorate
            .Setup(c => c.GetPortalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctoratePortal { PortalCode = "142", Name = "Portale di prova", PortalCategory = OctoratePortalCategories.Ota, Enabled = true }]);
        await using (var jobDb = new AppDbContext(_options))
        {
            var job = new MarketplacePortalRefreshJob(NewCatalog(jobDb));
            await job.ExecuteAsync(CancellationToken.None);
            await job.ExecuteAsync(CancellationToken.None);
        }

        await using (var db = new AppDbContext(_options))
        {
            var cached = await db.MarketplacePortals.AsNoTracking().SingleAsync();
            Assert.Equal(("142", true, true), (cached.PortalCode, cached.IsListed, cached.Enabled));
        }

        Assert.False(await service.QueueRefreshWhenCacheIsEmptyAsync(CancellationToken.None));
        VerifyQueued(Times.Once());
    }

    /// <summary>A database or a Hangfire storage that is not ready never stops the start: the failure is logged and the daily refresh still fills the cache.</summary>
    [Fact]
    public async Task QueueRefreshWhenCacheIsEmpty_TheQueueFails_IsSwallowedAndNothingIsQueued()
    {
        _jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("the Hangfire storage is not ready"));

        var queued = await CreateService().QueueRefreshWhenCacheIsEmptyAsync(CancellationToken.None);

        Assert.False(queued);
    }

    /// <summary>
    /// Never blocks the start: <c>StartAsync</c> returns while the check of the cache has not even run (it waits behind the flag read here), and the run is
    /// queued only afterwards, from the background.
    /// </summary>
    [Fact]
    public async Task StartAsync_ReturnsBeforeTheCacheIsChecked_SoTheStartOfTheHostIsNeverHeld()
    {
        using var gate = new ManualResetEventSlim(initialState: false);
        var service = CreateService(beforeFlagRead: () => gate.Wait(TimeSpan.FromSeconds(30)));

        await service.StartAsync(CancellationToken.None);

        Assert.False(service.ExecuteTask!.IsCompleted);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);

        gate.Set();
        await service.ExecuteTask;
        VerifyQueued(Times.Once());
        await service.StopAsync(CancellationToken.None);
    }

    private void VerifyQueued(Times times) =>
        _jobs.Verify(
            c => c.Create(
                It.Is<Job>(job => job.Type == typeof(MarketplacePortalRefreshJob) && job.Method.Name == nameof(MarketplacePortalRefreshJob.ExecuteAsync)),
                It.IsAny<EnqueuedState>()),
            times);

    private MarketplacePortalBootstrapHostedService CreateService(bool connectorOn = true, Action? beforeFlagRead = null)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(() =>
        {
            beforeFlagRead?.Invoke();
            return connectorOn;
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(_options));
        services.AddScoped<IMarketplacePortalCatalogService>(provider => NewCatalog(provider.GetRequiredService<AppDbContext>()));
        services.AddSingleton(flags.Object);
        services.AddSingleton(_jobs.Object);

        return new MarketplacePortalBootstrapHostedService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MarketplacePortalBootstrapHostedService>.Instance);
    }

    private MarketplacePortalCatalogService NewCatalog(AppDbContext db) =>
        new(_octorate.Object, db, TimeProvider.System, NullLogger<MarketplacePortalCatalogService>.Instance);

    private async Task SeedAsync(params MarketplacePortal[] portals)
    {
        await using var db = new AppDbContext(_options);
        db.MarketplacePortals.AddRange(portals);
        await db.SaveChangesAsync();
    }

    private static MarketplacePortal Portal(string code, bool listed, bool enabled) =>
        new() { PortalCode = code, Name = code, Category = MarketplacePortalCategory.Ota, IsListed = listed, Enabled = enabled };
}
