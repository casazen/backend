using System.Reflection;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// E23/F01/US01/T01, T02 (NF-122): what registers the delivery of the check-in link of a stay with no e-mail address. The hourly retry is scheduled only while the connector and at
/// least one of the two flags of the module are on (otherwise the schedule of an earlier deploy is removed), it never overlaps with its previous run, and a run with the flags off does
/// nothing; the exact list of what <c>AddCheckInLinkDelivery</c> registers is photographed here.
/// </summary>
public class CheckInLinkDeliveryRegistrationTests
{
    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void Configure_TheRetryIsScheduledEveryHourAtMinute30InUtc_OnlyWhileTheConnectorAndAFlagOfTheModuleAreOn(
        bool connector, bool portalChat, bool contactInfo, bool scheduled)
    {
        var flags = new TestFeatureFlags();
        flags.Set(FeatureFlags.OctorateConnector, connector);
        flags.Set(FeatureFlags.PortalChatCheckInLink, portalChat);
        flags.Set(FeatureFlags.GuestContactInfo, contactInfo);
        var registered = new Dictionary<string, (string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, cron, options) => registered[id] = (cron, options));

        RecurringJobsRegistration.Configure(manager.Object, flags);

        Assert.Equal("checkin-link-delivery-retry", CheckInLinkDeliveryRetryJob.RecurringJobId);
        Assert.Equal(scheduled, registered.ContainsKey(CheckInLinkDeliveryRetryJob.RecurringJobId));
        manager.Verify(m => m.RemoveIfExists(CheckInLinkDeliveryRetryJob.RecurringJobId), scheduled ? Times.Never() : Times.Once());
        if (scheduled)
        {
            Assert.Equal("30 * * * *", registered[CheckInLinkDeliveryRetryJob.RecurringJobId].Cron);
            Assert.Equal(TimeZoneInfo.Utc, registered[CheckInLinkDeliveryRetryJob.RecurringJobId].Options.TimeZone);
        }

        // The daily job that issues the links is registered whatever the flags say: it records why a link goes nowhere.
        Assert.Contains("guest-checkin-send", registered.Keys);
    }

    [Fact]
    public void TheRetryJob_NeverOverlapsWithItsPreviousRun_AndHangfireDoesNotRetryIt()
    {
        var method = typeof(CheckInLinkDeliveryRetryJob).GetMethod(nameof(CheckInLinkDeliveryRetryJob.ExecuteAsync))!;

        var concurrency = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        Assert.Equal(0, method.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts);
    }

    [Fact]
    public async Task TheRetryJob_DoesNothingWithTheFlagsOff_AndHandsTheLinkIssuerToTheDeliveryWhenOn()
    {
        var flags = new TestFeatureFlags(FeatureFlags.OctorateConnector);
        var deliveries = new Mock<ICheckInLinkDeliveryService>();
        deliveries
            .Setup(d => d.RetryWaitingAsync(It.IsAny<Func<Guid, Guid, Task<IssuedCheckInLink>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        var checkIn = new Mock<IGuestCheckInService>();
        var job = new CheckInLinkDeliveryRetryJob(deliveries.Object, checkIn.Object, flags, NullLogger<CheckInLinkDeliveryRetryJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        deliveries.VerifyNoOtherCalls();

        flags.Set(FeatureFlags.PortalChatCheckInLink, true);
        Func<Guid, Guid, Task<IssuedCheckInLink>>? handed = null;
        deliveries
            .Setup(d => d.RetryWaitingAsync(It.IsAny<Func<Guid, Guid, Task<IssuedCheckInLink>>>(), It.IsAny<CancellationToken>()))
            .Callback<Func<Guid, Guid, Task<IssuedCheckInLink>>, CancellationToken>((issue, _) => handed = issue)
            .ReturnsAsync(2);
        var booking = Guid.NewGuid();
        var org = Guid.NewGuid();
        var issued = new IssuedCheckInLink(Guid.NewGuid(), "tok", DateTime.UtcNow.AddDays(7));
        checkIn.Setup(c => c.IssueLinkAsync(booking, org)).ReturnsAsync(issued);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.NotNull(handed);
        Assert.Same(issued, await handed(booking, org));
    }

    /// <summary>The exact list of what <c>AddCheckInLinkDelivery</c> registers: the delivery, the contact service and the retry job, each once and scoped (they hold the database of the request).</summary>
    [Fact]
    public void AddCheckInLinkDelivery_RegistersTheDeliveryTheContactServiceAndTheRetryJob_AsTheWholeList()
    {
        var services = new ServiceCollection();

        services.AddCheckInLinkDelivery();

        Assert.Equal(typeof(CheckInLinkDeliveryService), Assert.Single(services, d => d.ServiceType == typeof(ICheckInLinkDeliveryService)).ImplementationType);
        Assert.Equal(typeof(GuestContactInfoService), Assert.Single(services, d => d.ServiceType == typeof(IGuestContactInfoService)).ImplementationType);
        Assert.Single(services, d => d.ServiceType == typeof(CheckInLinkDeliveryRetryJob));
        Assert.All(services, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
        Assert.Equal(
            [nameof(CheckInLinkDeliveryRetryJob), nameof(ICheckInLinkDeliveryService), nameof(IGuestContactInfoService)],
            services.Select(d => d.ServiceType.Name).Order());
    }
}
