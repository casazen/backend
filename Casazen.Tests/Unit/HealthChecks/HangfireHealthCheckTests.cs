using Casazen.Web.Configuration;
using Casazen.Web.HealthChecks;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.HealthChecks;

/// <summary>FD-12 (A9-19): Hangfire readiness, storage reachable and this process's server alive.</summary>
public class HangfireHealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckHealthAsync_CurrentProcessServerWithRecentHeartbeat_ReturnsHealthy()
    {
        var result = await CheckAsync(Environments.Production, Server(HangfireServerIdentity.CurrentProcessIdPrefix + Guid.NewGuid(), 20));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_OnlyAnotherInstanceServerAlive_ReturnsDegraded()
    {
        var result = await CheckAsync(Environments.Production, Server($"other-container:1:{Guid.NewGuid()}", 10));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_CurrentProcessServerHeartbeatStale_ReturnsUnhealthy()
    {
        var result = await CheckAsync(
            Environments.Production,
            Server(HangfireServerIdentity.CurrentProcessIdPrefix + Guid.NewGuid(), secondsAgo: 600));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_NoServer_ReturnsUnhealthy()
    {
        var result = await CheckAsync(Environments.Production);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_StorageUnreachable_ReturnsUnhealthyWithoutExceptionMessageInDescription()
    {
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers()).Throws(new InvalidOperationException("Host=db.secret.supabase.co refused"));
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoring.Object);

        var result = await CreateCheck(Environments.Production, storage.Object).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
        Assert.DoesNotContain("secret", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_NotConfiguredInTesting_ReturnsDegraded()
    {
        var result = await CreateCheck("Testing", storage: null).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_NotConfiguredInProduction_ReturnsUnhealthy()
    {
        var result = await CreateCheck(Environments.Production, storage: null).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static Task<HealthCheckResult> CheckAsync(string environment, params ServerDto[] servers)
    {
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers()).Returns(servers.ToList());
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoring.Object);
        return CreateCheck(environment, storage.Object).CheckHealthAsync(new HealthCheckContext());
    }

    private static HangfireHealthCheck CreateCheck(string environment, JobStorage? storage)
    {
        var services = new ServiceCollection();
        if (storage is not null)
            services.AddSingleton(storage);

        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);

        return new HangfireHealthCheck(services.BuildServiceProvider(), hostEnvironment.Object, new Unit.FixedTimeProvider(Now));
    }

    private static ServerDto Server(string name, int secondsAgo) => new()
    {
        Name = name,
        Heartbeat = Now.UtcDateTime.AddSeconds(-secondsAgo),
        StartedAt = Now.UtcDateTime.AddHours(-1),
        Queues = ["default"],
        WorkersCount = 20,
    };
}
