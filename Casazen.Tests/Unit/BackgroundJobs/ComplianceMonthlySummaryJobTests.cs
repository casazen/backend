using System.Reflection;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// E03/F02/US02/T02 (NF-52, CHK-12 point 6): the job of the monthly compliance summary only runs the service, on the first of every month at 07:00 Europe/Rome, never
/// overlapping another run, whatever the flags say (the service reads the flag of the module).
/// </summary>
public class ComplianceMonthlySummaryJobTests
{
    [Fact]
    public void Configure_TheMonthlySummary_IsScheduledOnTheFirstOfTheMonthAt0700InRome_WhateverTheFlags()
    {
        var registered = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => registered[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        Assert.Equal("compliance-monthly-summary", ComplianceMonthlySummaryJob.RecurringJobId);
        var summary = registered[ComplianceMonthlySummaryJob.RecurringJobId];
        Assert.Equal("0 7 1 * *", summary.Cron);
        Assert.Equal(RomeCalendar.TimeZone, summary.Options.TimeZone);
        Assert.Equal(typeof(ComplianceMonthlySummaryJob), summary.Job.Type);
        Assert.Equal(nameof(ComplianceMonthlySummaryJob.ExecuteAsync), summary.Job.Method.Name);
        manager.Verify(m => m.RemoveIfExists(ComplianceMonthlySummaryJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void ExecuteAsync_NeverOverlapsAnotherRun()
    {
        var method = typeof(ComplianceMonthlySummaryJob).GetMethod(nameof(ComplianceMonthlySummaryJob.ExecuteAsync))!;

        var attribute = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, attribute.TimeoutSec);
    }

    [Theory]
    [InlineData(2026, 11, 1, 6, 0, 2026, 11, 1)]
    [InlineData(2026, 10, 31, 23, 30, 2026, 11, 1)]
    [InlineData(2026, 10, 31, 22, 30, 2026, 10, 31)]
    [InlineData(2026, 12, 31, 23, 30, 2027, 1, 1)]
    public async Task ExecuteAsync_RunsTheServiceOnceWithTheRomeDateOfTheClock(int year, int month, int day, int hour, int minute, int runYear, int runMonth, int runDay)
    {
        var service = new Mock<IComplianceMonthlySummaryService>();
        service
            .Setup(s => s.RunAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceMonthlySummaryRunResult(false, 0, 0));
        var clock = new FakeTimeProvider(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

        await new ComplianceMonthlySummaryJob(service.Object, clock).ExecuteAsync();

        service.Verify(s => s.RunAsync(new DateOnly(runYear, runMonth, runDay), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheRunFails_TheFailureReachesHangfire_SoTheRunIsRepeated()
    {
        var service = new Mock<IComplianceMonthlySummaryService>();
        service
            .Setup(s => s.RunAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AggregateException("1 compliance summaries of 2026-10 could not be sent", new InvalidOperationException("boom")));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero));

        // Not swallowed: a failed job is retried by Hangfire, and the retry sends only the organizations that have no row for the month yet.
        await Assert.ThrowsAsync<AggregateException>(() => new ComplianceMonthlySummaryJob(service.Object, clock).ExecuteAsync());
    }
}
