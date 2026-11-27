using System.Reflection;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// E05/F07/US01: what wires the scheduled messages into the application. The exact list of what <c>AddScheduledMessages</c> registers is photographed here (a service a later task adds goes into this test), the
/// options have the decided defaults and a value that cannot work falls back to them, the two jobs are scheduled only while <c>Features:ScheduledMessages</c> is on (and removed otherwise), never overlap with
/// their previous run and do nothing with the flag off, and the handler of the booking events plans or cancels as the event says and does nothing with the flag off.
/// </summary>
public class ScheduledMessageWiringTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    // ------------------------------------------------------------------------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AddScheduledMessages_RegistersTheServicesOfTheFeature_ScopedEachOnce_AsTheWholeListOfServices()
    {
        var services = new ServiceCollection();

        services.AddScheduledMessages();

        foreach (var (service, implementation) in new (Type, Type)[]
                 {
                     (typeof(IScheduledMessageService), typeof(ScheduledMessageService)),
                     (typeof(IEventHandler<DomainEvent>), typeof(ScheduledMessageBookingHandler)),
                     (typeof(ScheduledMessagePlanJob), typeof(ScheduledMessagePlanJob)),
                     (typeof(IScheduledMessageDispatcher), typeof(ScheduledMessageDispatcher)),
                     (typeof(ScheduledMessageSweepJob), typeof(ScheduledMessageSweepJob)),
                     (typeof(ScheduledMessageAlertDelivery), typeof(ScheduledMessageAlertDelivery)),
                     (typeof(ScheduledMessageAlertJob), typeof(ScheduledMessageAlertJob)),
                 })
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, descriptor.ImplementationType);

            // Scoped: they hold the database of the request (or of the job).
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }

        // Of the types of the application, these and no other (the options of the section Messaging:ScheduledMessages are the framework's).
        Assert.Equal(
            [
                "IEventHandler`1", nameof(IScheduledMessageDispatcher), nameof(IScheduledMessageService), nameof(ScheduledMessageAlertDelivery),
                nameof(ScheduledMessageAlertJob), nameof(ScheduledMessagePlanJob), nameof(ScheduledMessageSweepJob),
            ],
            services.Select(d => d.ServiceType).Where(t => t.Namespace?.StartsWith("Casazen.", StringComparison.Ordinal) == true).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Program_CallsAddScheduledMessagesOnce_AndTheExtensionIsNotAddedToTheSharedOne()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "Casazen.Web", "Program.cs"));
        var shared = File.ReadAllText(Path.Combine(root, "Casazen.Web", "Extensions", "ServiceCollectionExtensions.cs"));

        Assert.Single(Regex.Matches(program, @"builder\.Services\.AddScheduledMessages\(\);"));
        Assert.DoesNotContain("ScheduledMessageService", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduledMessageDispatcher", shared, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The options
    // ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheOptionsOfTheSection_HaveTheDecidedDefaults_AreBoundToTheSection_AndAValueThatCannotWorkFallsBack()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ScheduledMessageOptions.SectionName}:SendWindowFrom"] = "09:30",
                [$"{ScheduledMessageOptions.SectionName}:PlanHorizonDays"] = "0",
                [$"{ScheduledMessageOptions.SectionName}:CatchUpMinutes"] = "100000",
                [$"{ScheduledMessageOptions.SectionName}:BlockedRetryMinutes"] = "45",
            })
            .Build());
        services.AddScheduledMessages();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ScheduledMessageOptions>>().Value;

        Assert.Equal((new TimeOnly(9, 30), new TimeOnly(21, 0)), options.EffectiveSendWindow);
        Assert.Equal(30, options.EffectivePlanHorizonDays);
        Assert.Equal(15, options.EffectiveCatchUpMinutes);
        Assert.Equal(45, options.EffectiveBlockedRetryMinutes);
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(21, 0)), new ScheduledMessageOptions { SendWindowFrom = new TimeOnly(22, 0), SendWindowTo = new TimeOnly(6, 0) }.EffectiveSendWindow);
    }

    [Fact]
    public void TheSettingsThatAreCommitted_AreTheDefaults_AndTheFlagIsOff()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        var options = configuration.GetSection(ScheduledMessageOptions.SectionName).Get<ScheduledMessageOptions>()!;

        Assert.Equal(new ScheduledMessageOptions().EffectiveSendWindow, options.EffectiveSendWindow);
        Assert.Equal(ScheduledMessageOptions.DefaultPlanHorizonDays, options.PlanHorizonDays);
        Assert.Equal(ScheduledMessageOptions.DefaultCatchUpMinutes, options.CatchUpMinutes);
        Assert.Equal(ScheduledMessageOptions.DefaultBlockedRetryMinutes, options.BlockedRetryMinutes);
        Assert.False(configuration.GetValue<bool>($"Features:{FeatureFlags.ScheduledMessages}"));
        Assert.Contains(FeatureFlags.ScheduledMessages, FeatureFlags.All);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The jobs
    // ------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configure_ThePlanIsHourlyAtMinute20AndTheSweepEveryFifteenMinutes_OnlyWhileTheFlagIsOn_AndRemovedOtherwise(bool enabled)
    {
        var flags = new TestFeatureFlags();
        flags.Set(FeatureFlags.ScheduledMessages, enabled);
        var registered = new Dictionary<string, (string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, cron, options) => registered[id] = (cron, options));

        RecurringJobsRegistration.Configure(manager.Object, flags);

        Assert.Equal("scheduled-message-plan", ScheduledMessagePlanJob.RecurringJobId);
        Assert.Equal("scheduled-message-sweep", ScheduledMessageSweepJob.RecurringJobId);
        foreach (var id in new[] { ScheduledMessagePlanJob.RecurringJobId, ScheduledMessageSweepJob.RecurringJobId })
        {
            Assert.Equal(enabled, registered.ContainsKey(id));
            manager.Verify(m => m.RemoveIfExists(id), enabled ? Times.Never() : Times.Once());
        }

        if (enabled)
        {
            Assert.Equal(("20 * * * *", TimeZoneInfo.Utc), (registered[ScheduledMessagePlanJob.RecurringJobId].Cron, registered[ScheduledMessagePlanJob.RecurringJobId].Options.TimeZone));
            Assert.Equal(("*/15 * * * *", TimeZoneInfo.Utc), (registered[ScheduledMessageSweepJob.RecurringJobId].Cron, registered[ScheduledMessageSweepJob.RecurringJobId].Options.TimeZone));
        }
    }

    [Theory]
    [InlineData(typeof(ScheduledMessagePlanJob))]
    [InlineData(typeof(ScheduledMessageSweepJob))]
    public void TheJobs_NeverOverlapWithTheirPreviousRun_AndHangfireDoesNotRetryThem(Type job)
    {
        var method = job.GetMethod("ExecuteAsync")!;

        var concurrency = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        Assert.Equal(0, method.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts);
    }

    [Fact]
    public async Task ThePlanJob_DoesNothingWithTheFlagOff_AndPlansEveryOrgThatHasAMessageToPlan_EvenWhenOneOfThemFails()
    {
        var other = Guid.NewGuid();
        var messages = new Mock<IScheduledMessageService>();
        messages.Setup(m => m.OrgsWithMessageRoutinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([OrgId, other]);
        messages.Setup(m => m.ReplanOrgAsync(OrgId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("one org fails"));
        messages.Setup(m => m.ReplanOrgAsync(other, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        var flags = new TestFeatureFlags();
        var job = new ScheduledMessagePlanJob(messages.Object, flags, NullLogger<ScheduledMessagePlanJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        messages.VerifyNoOtherCalls();

        flags.Set(FeatureFlags.ScheduledMessages, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None));

        // The organization that failed did not stop the next one: both were looked at, and the run shows as failed.
        messages.Verify(m => m.ReplanOrgAsync(OrgId, It.IsAny<CancellationToken>()), Times.Once);
        messages.Verify(m => m.ReplanOrgAsync(other, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheSweepJob_DoesNothingWithTheFlagOff_AndSweepsAtTheInstantOfTheClockWhenOn()
    {
        var instant = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var dispatcher = new Mock<IScheduledMessageDispatcher>();
        dispatcher.Setup(d => d.SweepAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var flags = new TestFeatureFlags();
        var job = new ScheduledMessageSweepJob(dispatcher.Object, flags, new FixedTimeProvider(instant), NullLogger<ScheduledMessageSweepJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        dispatcher.VerifyNoOtherCalls();

        flags.Set(FeatureFlags.ScheduledMessages, true);
        await job.ExecuteAsync(CancellationToken.None);

        dispatcher.Verify(d => d.SweepAsync(instant.UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The handler of the booking events
    // ------------------------------------------------------------------------------------------------------------------------------------

    private static DomainEvent EventOf(DomainEventType type, Guid bookingId) =>
        new(Guid.NewGuid(), OrgId, type, bookingId, new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), "{}");

    private static ScheduledMessageBookingHandler HandlerOf(IScheduledMessageService messages, bool enabled = true) =>
        new(messages, enabled ? new TestFeatureFlags(FeatureFlags.ScheduledMessages) : new TestFeatureFlags(), NullLogger<ScheduledMessageBookingHandler>.Instance);

    [Theory]
    [InlineData(DomainEventType.BookingConfirmed)]
    [InlineData(DomainEventType.BookingModified)]
    public async Task ABookingThatIsConfirmedOrChanged_IsPlannedAgain(DomainEventType type)
    {
        var booking = Guid.NewGuid();
        var messages = new Mock<IScheduledMessageService>();
        messages.Setup(m => m.PlanBookingAsync(booking, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await HandlerOf(messages.Object).HandleAsync(EventOf(type, booking), CancellationToken.None);

        messages.Verify(m => m.PlanBookingAsync(booking, It.IsAny<CancellationToken>()), Times.Once);
        messages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ACancelledBooking_HasItsOpenMessagesCancelled_WithTheReasonOfTheCancellation()
    {
        var booking = Guid.NewGuid();
        var messages = new Mock<IScheduledMessageService>();

        await HandlerOf(messages.Object).HandleAsync(EventOf(DomainEventType.BookingCancelled, booking), CancellationToken.None);

        messages.Verify(m => m.CancelForBookingAsync(booking, ScheduledMessageReasons.BookingCancelled, It.IsAny<CancellationToken>()), Times.Once);
        messages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheHandler_IgnoresTheEventsThatAreNotItsOwn_AnEventWithNoBooking_AndEverythingWithTheFlagOff()
    {
        var booking = Guid.NewGuid();
        var messages = new Mock<IScheduledMessageService>();

        await HandlerOf(messages.Object).HandleAsync(EventOf(DomainEventType.CheckOutCompleted, booking), CancellationToken.None);
        await HandlerOf(messages.Object).HandleAsync(EventOf(DomainEventType.BookingConfirmed, Guid.Empty), CancellationToken.None);
        await HandlerOf(messages.Object, enabled: false).HandleAsync(EventOf(DomainEventType.BookingConfirmed, booking), CancellationToken.None);
        await HandlerOf(messages.Object, enabled: false).HandleAsync(EventOf(DomainEventType.BookingCancelled, booking), CancellationToken.None);

        messages.VerifyNoOtherCalls();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
