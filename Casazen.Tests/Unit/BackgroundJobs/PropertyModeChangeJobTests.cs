using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Features;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// PM-02 (D16): the hourly <c>property-mode-change</c> job only runs the mode change service, which decides what is due and
/// is idempotent. It is registered only with <c>Features:PropertyModeChange</c> on (off: removed from the schedule of an
/// earlier deploy), hourly so a change is applied within the hour after midnight of Rome, and never overlaps itself.
/// </summary>
public class PropertyModeChangeJobTests
{
    [Fact]
    public void Configure_FlagOn_RegistersTheJobHourlyInUtc()
    {
        var registered = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => registered[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(
            manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false, propertyModeChange: true));

        var entry = registered["property-mode-change"];
        Assert.Equal("property-mode-change", PropertyModeChangeJob.RecurringJobId);
        Assert.Equal(typeof(PropertyModeChangeJob), entry.Job.Type);
        Assert.Equal(nameof(PropertyModeChangeJob.ExecuteAsync), entry.Job.Method.Name);
        Assert.Equal("0 * * * *", entry.Cron);
        Assert.Equal(Cron.Hourly(), entry.Cron);
        Assert.Equal(TimeZoneInfo.Utc, entry.Options.TimeZone);
        manager.Verify(m => m.RemoveIfExists(PropertyModeChangeJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_FlagOff_DoesNotRegisterTheJobAndRemovesTheScheduleOfEarlierDeploys()
    {
        var registered = new List<string>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, _, _) => registered.Add(id));

        RecurringJobsRegistration.Configure(
            manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false, propertyModeChange: false));

        Assert.DoesNotContain(PropertyModeChangeJob.RecurringJobId, registered);
        manager.Verify(m => m.RemoveIfExists(PropertyModeChangeJob.RecurringJobId), Times.Once);
        // The other jobs are not touched by the flag.
        Assert.Contains("stay-alerts", registered);
    }

    [Fact]
    public void Flag_IsOffByDefaultAndExposedToTheFrontend()
    {
        Assert.Equal("PropertyModeChange", FeatureFlags.PropertyModeChange);
        Assert.Contains(FeatureFlags.PropertyModeChange, FeatureFlags.All);
        // The committed default: written, and false.
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json")));
        var committed = settings.RootElement.GetProperty("Features").GetProperty(FeatureFlags.PropertyModeChange);
        Assert.Equal(JsonValueKind.False, committed.ValueKind);
        // Missing from the configuration it is off too, and only an explicit true turns it on.
        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(FeatureFlags.PropertyModeChange));
        Assert.True(Flags(new Dictionary<string, string?> { ["Features:PropertyModeChange"] = "true" }).IsEnabled(FeatureFlags.PropertyModeChange));
    }

    private static ConfigurationFeatureFlags Flags(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public async Task ExecuteAsync_RunsTheModeChangeServiceOnce()
    {
        var service = new Mock<IPropertyModeService>();
        service
            .Setup(s => s.ApplyDueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PropertyModeRunResult(Skipped: false, Examined: 2, Applied: 2, Failed: 0));
        using var cts = new CancellationTokenSource();

        await new PropertyModeChangeJob(service.Object).ExecuteAsync(cts.Token);

        service.Verify(s => s.ApplyDueAsync(cts.Token), Times.Once);
    }

    [Fact]
    public void ExecuteAsync_NeverOverlapsAnotherRun()
    {
        var method = typeof(PropertyModeChangeJob).GetMethod(nameof(PropertyModeChangeJob.ExecuteAsync))!;

        var attribute = Assert.IsType<DisableConcurrentExecutionAttribute>(
            Assert.Single(method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false)));
        Assert.Equal(JobLockTimeouts.DefaultSeconds, attribute.TimeoutSec);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
