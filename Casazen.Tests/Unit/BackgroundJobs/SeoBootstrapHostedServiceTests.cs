using Casazen.Core.Repositories;
using Casazen.Web.Configuration;
using Casazen.Web.HostedServices;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// FD-21 (A8-26): the SEO bootstrap queues the AI generation once per environment, under a distributed lock, instead
/// of at every start while the page count is zero (each deploy paid for a new generation, each replica queued one).
/// </summary>
public class SeoBootstrapHostedServiceTests
{
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IBackgroundJobClient> _jobClient = new();
    private readonly Mock<ISeoContentRepository> _repository = new();
    private readonly List<KeyValuePair<string, string>> _marker = [];

    public SeoBootstrapHostedServiceTests()
    {
        _connection.Setup(c => c.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(Mock.Of<IDisposable>());
        _connection.Setup(c => c.SetRangeInHash(SeoBootstrapHostedService.MarkerKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => _marker.AddRange(pairs));
        _jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-seo-1");
    }

    [Fact]
    public async Task StartAsync_NoMarkerAndNoPages_QueuesOnceAndStoresMarker()
    {
        _repository.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await CreateService().StartAsync(CancellationToken.None);

        _jobClient.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        Assert.Contains(_marker, p => p.Key == SeoBootstrapHostedService.EnqueuedAtField);
        Assert.Contains(_marker, p => p.Key == SeoBootstrapHostedService.JobIdField && p.Value == "job-seo-1");
    }

    [Fact]
    public async Task StartAsync_MarkerAlreadyStored_DoesNotQueueAgainEvenWithZeroPages()
    {
        _connection.Setup(c => c.GetAllEntriesFromHash(SeoBootstrapHostedService.MarkerKey))
            .Returns(new Dictionary<string, string> { [SeoBootstrapHostedService.EnqueuedAtField] = "2026-09-01T00:00:00Z" });
        _repository.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await CreateService().StartAsync(CancellationToken.None);

        _jobClient.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_PagesExist_DoesNotQueue()
    {
        _repository.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(12);

        await CreateService().StartAsync(CancellationToken.None);

        _jobClient.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AnotherReplicaHoldsLock_DoesNotQueue()
    {
        _connection.Setup(c => c.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new DistributedLockTimeoutException("casazen:seo-bootstrap:lock"));
        _repository.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await CreateService().StartAsync(CancellationToken.None);

        _jobClient.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_BootstrapDisabled_DoesNotTouchStorage()
    {
        await CreateService(bootstrapOnStartup: false).StartAsync(CancellationToken.None);

        _connection.Verify(c => c.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _jobClient.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    private SeoBootstrapHostedService CreateService(bool bootstrapOnStartup = true)
    {
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetConnection()).Returns(_connection.Object);

        var services = new ServiceCollection();
        services.AddSingleton(storage.Object);
        services.AddSingleton(_jobClient.Object);
        services.AddSingleton(_repository.Object);

        return new SeoBootstrapHostedService(
            services.BuildServiceProvider(),
            Options.Create(new SeoBootstrapOptions { BootstrapOnStartup = bootstrapOnStartup, AutoApproveAfterBootstrap = false }),
            NullLogger<SeoBootstrapHostedService>.Instance);
    }
}
