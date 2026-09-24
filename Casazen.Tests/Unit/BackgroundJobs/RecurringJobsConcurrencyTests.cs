using System.Reflection;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>FD-11: recurring jobs never run twice at once (overrunning run, retry, manual trigger).</summary>
public class RecurringJobsConcurrencyTests
{
    private static readonly string[] CriticalJobIds =
    [
        "ical-supplier-sync",
        "property-ical-sync",
        "direct-booking-charge",
        "checkout-hold-expiry",
        "alloggiati-deadline-alert",
        "gdpr-data-retention",
        "guest-checkin-send",
        "guest-checkin-reminder",
        "booking-pull-all",
        "dynamic-pricing-adaptation",
        "lease-registration-status-poll",
    ];

    [Fact]
    public void Configure_CriticalJobs_AreRegistered()
    {
        var jobs = RegisteredJobs();

        Assert.All(CriticalJobIds, id => Assert.Contains(id, jobs.Keys));
    }

    [Fact]
    public void Configure_EveryRecurringJob_HasDisableConcurrentExecution()
    {
        var jobs = RegisteredJobs();

        Assert.NotEmpty(jobs);
        Assert.All(jobs, pair =>
        {
            var attribute = ConcurrencyAttribute(pair.Value.Method);
            Assert.True(attribute is not null, $"Recurring job '{pair.Key}' ({pair.Value.Type.Name}.{pair.Value.Method.Name}) has no [DisableConcurrentExecution]");
            Assert.InRange(attribute.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        });
    }

    [Fact]
    public void Configure_JobsEveryFifteenMinutesOrLess_WaitLessThanTheirInterval()
    {
        var jobs = RegisteredJobs();

        foreach (var id in new[] { "ical-supplier-sync", "property-ical-sync", "booking-pull-all", "lease-sign-status-poll", "lease-registration-status-poll", "checkout-hold-expiry" })
        {
            var attribute = ConcurrencyAttribute(jobs[id].Method)!;
            Assert.True(attribute.TimeoutSec < 5 * 60, $"{id} waits {attribute.TimeoutSec}s for its previous run");
        }
    }

    [Fact]
    public void OtaSyncJob_ExecuteAsync_LocksPerProperty()
    {
        var attribute = ConcurrencyAttribute(typeof(OtaSyncJob).GetMethod(nameof(OtaSyncJob.ExecuteAsync))!)!;

        Assert.Contains("{0}", attribute.Resource);
    }

    [Fact]
    public void AlloggiatiWebReportJob_ReportGuestAsync_LocksPerBooking()
    {
        var method = typeof(AlloggiatiWebReportJob).GetMethod(nameof(AlloggiatiWebReportJob.ReportGuestAsync))!;
        var attribute = ConcurrencyAttribute(method)!;

        // Argument {1} is the booking id: one Alloggiati Web submission per booking at a time.
        Assert.Equal("bookingId", method.GetParameters()[1].Name);
        Assert.Contains("{1}", attribute.Resource);
    }

    [Fact]
    public void DynamicPricingJob_DailyAndPerPropertyRuns_ShareOneLock()
    {
        var daily = ConcurrencyAttribute(typeof(DynamicPricingJob).GetMethod(nameof(DynamicPricingJob.ExecuteAsync))!)!;
        var perProperty = ConcurrencyAttribute(typeof(DynamicPricingJob).GetMethod(nameof(DynamicPricingJob.ExecuteForPropertyAsync))!)!;

        Assert.NotNull(daily.Resource);
        Assert.Equal(daily.Resource, perProperty.Resource);
    }

    private static DisableConcurrentExecutionAttribute? ConcurrencyAttribute(MethodInfo method) =>
        method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()
        ?? method.DeclaringType?.GetCustomAttribute<DisableConcurrentExecutionAttribute>();

    private static Dictionary<string, Job> RegisteredJobs()
    {
        var jobs = new Dictionary<string, Job>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, _, _) => jobs[id] = job);

        // Every flag on: the jobs behind a feature flag must be lock-protected too (FD-20).
        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: true));

        return jobs;
    }
}
