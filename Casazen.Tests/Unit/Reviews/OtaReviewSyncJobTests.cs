using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The read of the reviews of the channels, every six hours (E05/F06/US01/T02, ADR-032, NF-69, OCT-12): which bookings are read (the Airbnb stays of a confirmed listing that ended
/// inside the window of the reviews, of the orgs with a connected account), the cap of one run for the whole run, the calls of an org that the job never uses up, the failure of one
/// booking that never stops the others, and the schedule behind the connector flag. The service is the real one over a loose client of the channel manager: what is proven is what the
/// job asks of it.
/// </summary>
public class OtaReviewSyncJobTests
{
    private static OtaReviewSyncJob NewJob(OtaReviewHarness h, AppDbContext db, int cap)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(e => e.EnvironmentName).Returns("Staging");
        return new OtaReviewSyncJob(
            db,
            h.Service(db),
            h.Mappings.Object,
            h.Scope,
            h.Flags,
            host.Object,
            h.Limiter,
            h.Clock,
            new OptionsWrapper<OctorateReviewsOptions>(new OctorateReviewsOptions { MaxBookingsPerRun = cap }),
            NullLogger<OtaReviewSyncJob>.Instance);
    }

    private static async Task RunAsync(OtaReviewHarness h, int cap = 200)
    {
        await using var db = h.NewDb();
        await NewJob(h, db, cap).ExecuteAsync(CancellationToken.None);
    }

    private static void ReadTimes(OtaReviewHarness h, ReviewStay stay, Times times) =>
        h.Client.Verify(c => c.GetReviewAsync(stay.Accommodation, stay.Refer, It.IsAny<CancellationToken>()), times);

    private static void ReadsInAll(OtaReviewHarness h, Times times) =>
        h.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Job_ReadsTheEligibleAirbnbStaysOfTheConnectedOrgs_AndNothingElse()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.ChannelHasNothing(h.Granted);
        h.ChannelHasNothing(h.Hidden);
        h.ChannelHasNothing(h.Other);
        var orgId = h.Granted.OrgId;
        var propertyId = h.Granted.PropertyId;

        // Not eligible: another channel, a stay that was cancelled, no reservation on the channel manager, a stay that is not over, a stay whose window of sixty days is over.
        await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMBOOKING1", BookingSource.BookingCom);
        await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMCANCELED", status: BookingStatus.Cancelled);
        await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, string.Empty);
        await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMFUTURE01", endedDaysAgo: -2);
        await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMANCIENT1", endedDaysAgo: OtaReviewPollSchedule.WindowDays + 15);

        await RunAsync(h);

        // The org that is not connected has no token to read with: its stay is left alone.
        ReadTimes(h, h.Granted, Times.Once());
        ReadTimes(h, h.Hidden, Times.Once());
        ReadsInAll(h, Times.Exactly(2));
        Assert.Equal(2, await h.ReadAsync(db => db.OtaReviews.CountAsync()));
    }

    [Fact]
    public async Task Job_AStayWithoutAConfirmedListing_IsNotRead()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.ChannelHasNothing(h.Granted);
        h.ChannelHasNothing(h.Hidden);
        h.WithdrawListing(h.Hidden.OrgId, OtaReviewHarness.HiddenAccommodation);

        await RunAsync(h);

        ReadTimes(h, h.Granted, Times.Once());
        ReadsInAll(h, Times.Once());
    }

    [Fact]
    public async Task Job_ABookingIsReadAgainOnlyWhenItsTimeHasCome()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.ChannelHasNothing(h.Granted);
        h.ChannelHasNothing(h.Hidden);

        await RunAsync(h);
        await RunAsync(h);
        ReadTimes(h, h.Granted, Times.Once());

        h.Clock.Advance(TimeSpan.FromHours(7));
        await RunAsync(h);
        ReadTimes(h, h.Granted, Times.Exactly(2));
        ReadTimes(h, h.Hidden, Times.Exactly(2));
    }

    [Fact]
    public async Task Job_TheCapIsForTheWholeRun_TheStayThatEndedLastIsReadFirst_AndTheRestWaitForTheNextRun()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.ChannelHasNothing(h.Granted);
        h.ChannelHasNothing(h.Hidden);
        var recent = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, OtaReviewHarness.GrantedAccommodation, "HMRECENT01", endedDaysAgo: 1);
        h.ChannelHasNothing(recent);

        await RunAsync(h, cap: 1);

        ReadTimes(h, recent, Times.Once());
        ReadsInAll(h, Times.Once());

        // The next run reads the two that were never read; the one read a moment ago is not due.
        await RunAsync(h, cap: 5);
        ReadTimes(h, h.Granted, Times.Once());
        ReadTimes(h, h.Hidden, Times.Once());
        ReadTimes(h, recent, Times.Once());
        ReadsInAll(h, Times.Exactly(3));
    }

    [Fact]
    public async Task Job_WithTheConnectorOff_DoesNothing()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.Flags.Set(FeatureFlags.OctorateConnector, false);

        await RunAsync(h);

        ReadsInAll(h, Times.Never());
        Assert.Empty(await h.ReadAsync(db => db.OtaReviews.ToListAsync()));
    }

    [Fact]
    public async Task Job_AnOrgWhoseCallsAreNearlyUsedUp_IsLeftForTheNextRun_AndTheOtherOrgIsRead()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId, h.Other.OrgId);
        h.ChannelHasNothing(h.Granted);
        h.ChannelHasNothing(h.Hidden);
        h.ChannelHasNothing(h.Other);
        while (h.Limiter.AvailableCalls(h.Granted.OrgId) > OtaReviewSyncJob.RateReserve)
            await h.Limiter.TryAcquireAsync(h.Granted.OrgId);

        await RunAsync(h);

        // The last calls of an org are the host's who replies and the chat's: the job never takes them.
        ReadTimes(h, h.Granted, Times.Never());
        ReadTimes(h, h.Hidden, Times.Never());
        ReadTimes(h, h.Other, Times.Once());
    }

    [Fact]
    public async Task Job_TheLimitOfTheChannelManager_StopsTheOrgOnly_AndAFailureNobodyForesawStopsNobody()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId, h.Other.OrgId);
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));
        h.Client
            .Setup(c => c.GetReviewAsync(h.Hidden.Accommodation, h.Hidden.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        h.ChannelHasNothing(h.Other);

        await RunAsync(h);

        // Whatever happened to the stays of org A (stopped by the limit, or failed on its own), the stay of org B is read and stored.
        ReadTimes(h, h.Other, Times.Once());
        var rows = await h.ReadAsync(db => db.OtaReviews.ToListAsync());
        Assert.Contains(rows, r => r.BookingId == h.Other.BookingId);
        Assert.DoesNotContain(rows, r => r.BookingId == h.Granted.BookingId);
        Assert.DoesNotContain(rows, r => r.BookingId == h.Hidden.BookingId);
    }

    [Fact]
    public async Task Job_ARefusedRead_IsStoredAsACode_AndTheBookingWaitsADayBeforeItIsTriedAgain()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ReviewNotAvailable(403));
        h.ChannelHasNothing(h.Hidden);

        await RunAsync(h);
        var refused = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, refused.LastSyncError);

        // Seven hours later the booking that was read is due again, and the refused one is not (it waits a day).
        h.Clock.Advance(TimeSpan.FromHours(7));
        await RunAsync(h);
        ReadTimes(h, h.Granted, Times.Once());
        ReadTimes(h, h.Hidden, Times.Exactly(2));

        h.Clock.Advance(TimeSpan.FromHours(18));
        await RunAsync(h);
        ReadTimes(h, h.Granted, Times.Exactly(2));
    }

    /// <summary>
    /// An action nobody confirmed (a reply whose call had no answer) is read again whatever the age of the stay: the window of sixty days is lifted for it, also after a read that failed, and the
    /// read ends it (an error the host can act on after an hour); a stay of the same age with nothing waiting stays out.
    /// </summary>
    [Fact]
    public async Task Job_AnActionNobodyConfirmedOnAStayOlderThanTheWindow_IsReadAgain_AndEndsAsAnErrorTheHostCanActOn()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await h.ConnectAsync(h.Granted.OrgId);
        h.ChannelHasNothing(h.Granted);
        h.ChannelHasNothing(h.Hidden);
        var orgId = h.Granted.OrgId;

        // Two stays that ended forty days ago, each with the review of the guest out; the host replied to one, and the call to the channel manager had no answer.
        var waiting = await h.AddStayAsync(orgId, h.Granted.PropertyId, OtaReviewHarness.GrantedAccommodation, "HMOLD00001", endedDaysAgo: 40);
        var quiet = await h.AddStayAsync(orgId, h.Granted.PropertyId, OtaReviewHarness.GrantedAccommodation, "HMOLD00002", endedDaysAgo: 40);
        h.ChannelHasReview(waiting);
        h.ChannelHasReview(quiet);
        var review = await h.SyncAsync(waiting);
        await h.SyncAsync(quiet);
        h.Client
            .Setup(c => c.SendHostReplyAsync(waiting.Accommodation, waiting.Refer, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await using (var rig = h.AsRequestOf(orgId))
            await rig.Service.ReplyAsync(review.Id, "Grazie di cuore!", "reply-key-0001", "auth0|titolare");
        Assert.Equal(OtaReviewActionState.ManualVerify, (await h.ReviewOfAsync(waiting)).HostReplyState);

        // Thirty days later both stays are seventy days old, past the window: the one that waits for nothing is not read, the unconfirmed one is, and a read that fails does not end the wait.
        h.Clock.Advance(TimeSpan.FromDays(30));
        h.Client.Invocations.Clear();
        h.Client
            .Setup(c => c.GetReviewAsync(waiting.Accommodation, waiting.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await RunAsync(h);

        ReadTimes(h, waiting, Times.Once());
        ReadTimes(h, quiet, Times.Never());
        var failed = await h.ReviewOfAsync(waiting);
        Assert.Equal(OtaReviewActionState.ManualVerify, failed.HostReplyState);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime.AddHours(6), failed.NextPollAt);

        // The next run reads it again: the channel manager still shows no reply and more than an hour has passed since it was asked, so it is an error the host can act on.
        h.Clock.Advance(TimeSpan.FromHours(7));
        h.ChannelHasReview(waiting);
        await RunAsync(h);

        ReadTimes(h, waiting, Times.Exactly(2));
        ReadTimes(h, quiet, Times.Never());
        var ended = await h.ReviewOfAsync(waiting);
        Assert.Equal(OtaReviewActionState.Error, ended.HostReplyState);
        Assert.Equal(OtaReviewErrorCodes.ReplyNotDelivered, ended.HostReplyError);
        Assert.Null(ended.NextPollAt);
    }

    // ─── The schedule and the registrations ───

    private static IFeatureFlags Flags(bool octorateConnector)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(octorateConnector);
        return flags.Object;
    }

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Jobs) Manager()
    {
        var jobs = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => jobs[id] = (job, cron, options));
        return (manager, jobs);
    }

    [Fact]
    public void Configure_OctorateConnectorOn_SchedulesTheReviewReadEverySixHoursAtMinute40_AndOffRemovesIt()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(octorateConnector: true));

        Assert.Equal("ota-review-sync", OtaReviewSyncJob.RecurringJobId);
        var job = jobs[OtaReviewSyncJob.RecurringJobId];
        Assert.Equal("40 */6 * * *", job.Cron);
        Assert.Equal(TimeZoneInfo.Utc, job.Options.TimeZone);
        Assert.Equal(nameof(OtaReviewSyncJob.ExecuteAsync), job.Job.Method.Name);
        manager.Verify(m => m.RemoveIfExists(OtaReviewSyncJob.RecurringJobId), Times.Never);

        // The job lives in the infrastructure project, which cannot see JobLockTimeouts (web): the numbers are kept equal here, and the run is never retried by Hangfire.
        Assert.Equal(JobLockTimeouts.DefaultSeconds, OtaReviewSyncJob.LockTimeoutSeconds);
        var lockAttribute = job.Job.Method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(lockAttribute);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, lockAttribute.TimeoutSec);
        Assert.Equal(0, job.Job.Method.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts);

        var (off, offJobs) = Manager();
        RecurringJobsRegistration.Configure(off.Object, Flags(octorateConnector: false));
        Assert.DoesNotContain(OtaReviewSyncJob.RecurringJobId, offJobs.Keys);
        off.Verify(m => m.RemoveIfExists(OtaReviewSyncJob.RecurringJobId), Times.Once);
    }

    [Fact]
    public void AddOtaReviews_RegistersTheServiceAndTheJobPerScope_AndBindsItsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:Reviews:MaxBookingsPerRun"] = "7" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddOtaReviews();

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IOtaReviewService) && d.ImplementationType == typeof(OtaReviewService) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(OtaReviewSyncJob) && d.Lifetime == ServiceLifetime.Scoped);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(7, provider.GetRequiredService<IOptions<OctorateReviewsOptions>>().Value.MaxBookingsPerRun);
    }
}
