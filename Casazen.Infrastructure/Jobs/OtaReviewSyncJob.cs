using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The read of the reviews of the channels, every six hours (E05/F06/US01/T02, ADR-032, NF-69, OCT-12): the Octorate API has no list per property, so every eligible
/// Airbnb booking is read on its own through <see cref="IOtaReviewService.SyncBookingAsync"/>, within the cap of <c>Octorate:Reviews:MaxBookingsPerRun</c> (200) for the whole run.
/// Behind <c>Features:OctorateConnector</c> (the registration removes it while the flag is off, and a run with the flag off does nothing).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which bookings.</b> The Airbnb bookings of the properties with a confirmed listing, of a stay that happens (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> and no
/// no-show: the one definition), whose check-out is within the last <see cref="OtaReviewPollSchedule.WindowDays"/> days and not in the future, with a reservation on the channel
/// manager, and that are due: never read, or whose <c>NextPollAt</c> has come. The cadence is the row's (every six hours in the first fourteen days after the check-out, then once a
/// day until day sixty, and no more once the guest's review is out and the host's own review is sent or closed). The ones never read come first, the most recent stay first, then the
/// most overdue. <b>An action whose outcome nobody confirmed</b> (a reply or a host's review that is <c>ManualVerify</c>) is read again whatever the window says, so a stay older than
/// sixty days is read too while it waits (it is how the action ends: found on the channel, or an error the host can act on after an hour); only the window is lifted, the stay still has
/// to be one that happens, of a property with a confirmed listing and a reservation on the channel manager.</item>
/// <item><b>The calls of the org.</b> Each org is read for itself under <see cref="IOctorateOrgScope.Use"/>, through the same bucket of calls as every other call to Octorate; the run
/// of an org stops when its bucket is down to <see cref="RateReserve"/> (what is left is for the host who replies and for the chat) or when Octorate says it is over its limits, and
/// the bookings left are read at the next run. A failure of one booking is stored on its row as a code by the service and the next booking is read; a failure nobody foresaw is
/// logged with its type and the run goes on.</item>
/// <item><b>Logs.</b> Ids, counts and the type of an error, never a text, a name or the reference of a booking on the channel manager.</item>
/// <item>The lock waits <see cref="LockTimeoutSeconds"/>, the value of <c>JobLockTimeouts.DefaultSeconds</c> (a test keeps the two equal; the jobs live in the infrastructure project,
/// which cannot see that class).</item>
/// </list>
/// </remarks>
public class OtaReviewSyncJob(
    AppDbContext db,
    IOtaReviewService reviews,
    IChannelMappingService mappings,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    OctorateRateLimiter rateLimiter,
    TimeProvider timeProvider,
    IOptions<OctorateReviewsOptions> options,
    ILogger<OtaReviewSyncJob> logger)
{
    public const string RecurringJobId = "ota-review-sync";

    /// <summary>Every six hours, at minute 40 (UTC), so it does not start with the other jobs of the connector that run on the hour.</summary>
    public const string Cron = "40 */6 * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.DefaultSeconds</c>.</summary>
    public const int LockTimeoutSeconds = 300;

    /// <summary>The calls of an org that this job never uses: the last ones of its bucket are for the host who replies and for the chat.</summary>
    public const int RateReserve = 30;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Octorate review sync: the connector is off, nothing done");
            return;
        }

        var cap = options.Value.EffectiveMaxBookingsPerRun;
        var orgIds = await OtaMessageRuns.ListConnectedOrgsAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var totals = new RunTotals();
        var left = cap;

        foreach (var orgId in orgIds)
        {
            if (left <= 0)
                break;

            IReadOnlyList<Guid> due;
            try
            {
                due = await DueBookingsAsync(orgId, left, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                totals.Failed++;
                logger.LogWarning("Octorate review sync of org {OrgId}: the bookings due could not be selected ({Error})", orgId, ErrorOf(ex));
                continue;
            }

            foreach (var bookingId in due)
            {
                // The last calls of the bucket are not this job's: the org is read again at the next run.
                if (rateLimiter.AvailableCalls(orgId) <= RateReserve)
                {
                    totals.RateLimited++;
                    logger.LogInformation("Octorate review sync of org {OrgId} stopped: its calls to Octorate are nearly used up, the next run continues", orgId);
                    break;
                }

                left--;
                try
                {
                    OtaReviewSyncResult result;
                    using (orgScope.Use(orgId))
                        result = await reviews.SyncBookingAsync(bookingId, cancellationToken);

                    totals.Add(result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OctorateRateLimitException)
                {
                    totals.RateLimited++;
                    logger.LogWarning("Octorate review sync of org {OrgId} stopped: its calls to Octorate are used up, the next run continues", orgId);
                    break;
                }
                catch (Exception ex)
                {
                    totals.Failed++;
                    logger.LogWarning(
                        "Octorate review sync: booking {BookingId} of org {OrgId} failed ({Error}): the next run reads it again", bookingId, orgId, ErrorOf(ex));
                }
            }
        }

        logger.LogInformation(
            "Octorate review sync: {Orgs} orgs, {Read} bookings read, {Published} new reviews, {NotReadable} not readable, {Failed} not read ({ReadFailed} refused by the channel manager, {Unexpected} unexpected), {RateLimited} orgs stopped by the rate limit",
            orgIds.Count,
            totals.Read,
            totals.Published,
            totals.NotReadable,
            totals.ReadFailed + totals.Failed,
            totals.ReadFailed,
            totals.Failed,
            totals.RateLimited);
    }

    /// <summary>
    /// The bookings of the org that are due, at most <paramref name="limit"/>: the ones never read first (the stay that ended last first), then the ones whose time has come (the most
    /// overdue first). A system read of one org: the org is named in every predicate, and the properties of the org that have a confirmed listing are the only ones read.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> DueBookingsAsync(Guid orgId, int limit, CancellationToken ct)
    {
        var connected = (await mappings.ListAsync(orgId, ct))
            .Where(row => row.Status == OctorateMappingStatus.Confirmed && row.Property is not null)
            .Select(row => row.Property!.Id)
            .Distinct()
            .ToList();
        if (connected.Count == 0)
            return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var from = now.AddDays(-OtaReviewPollSchedule.WindowDays);
        BookingStatus[] active = AlloggiatiStatusRules.ActiveBookingStatuses;

        // The stays that happen, of a property with a listing, that are over, with a reservation on the channel manager: the window of the reviews is not asked yet.
        var happened = db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId
                        && b.Source == BookingSource.Airbnb
                        && connected.Contains(b.PropertyId)
                        && active.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.OctorateReservationId != null
                        && b.CheckOutDate <= now);

        // ...and the ones that ended inside the window of the reviews.
        var stays = happened.Where(b => b.CheckOutDate > from);

        // Never read: no row of the review yet.
        var neverRead = await stays
            .Where(b => !db.OtaReviews.Any(r => r.OrgId == orgId && r.BookingId == b.Id && r.Channel == OtaChannel.Airbnb))
            .OrderByDescending(b => b.CheckOutDate)
            .ThenBy(b => b.Id)
            .Select(b => b.Id)
            .Take(limit)
            .ToListAsync(ct);

        var ids = new List<Guid>(neverRead);
        if (ids.Count >= limit)
            return ids;

        // The rows whose time has come, on a stay that still counts: the most overdue first. A row with an action nobody confirmed (a reply or a host's review in ManualVerify) counts
        // whatever the age of the stay: only a read ends it, so the window of sixty days does not leave it behind.
        var overdue = await db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == orgId
                        && r.Channel == OtaChannel.Airbnb
                        && r.NextPollAt != null
                        && r.NextPollAt <= now
                        && (stays.Any(b => b.Id == r.BookingId)
                            || ((r.HostReplyState == OtaReviewActionState.ManualVerify || r.HostReviewState == OtaReviewActionState.ManualVerify)
                                && happened.Any(b => b.Id == r.BookingId))))
            .OrderBy(r => r.NextPollAt)
            .ThenBy(r => r.Id)
            .Select(r => r.BookingId)
            .Take(limit - ids.Count)
            .ToListAsync(ct);
        ids.AddRange(overdue);
        return ids;
    }

    /// <summary>The kind of an error for a log line: the code of an Octorate error, else the type of the exception.</summary>
    private static string ErrorOf(Exception ex) => ex is OctorateException octorate ? octorate.Code : ex.GetType().Name;

    /// <summary>The counts of a whole run, for its log line.</summary>
    private sealed class RunTotals
    {
        public int Read { get; private set; }

        public int Published { get; private set; }

        public int NotReadable { get; private set; }

        /// <summary>The channel manager did not give the reviews of a booking: the service stored the code on its row.</summary>
        public int ReadFailed { get; private set; }

        /// <summary>A booking failed in a way nobody foresaw (the type of the error is in the log).</summary>
        public int Failed { get; set; }

        public int RateLimited { get; set; }

        public void Add(OtaReviewSyncResult result)
        {
            switch (result.Outcome)
            {
                case OtaReviewSyncOutcome.Synced:
                    Read++;
                    if (result.NewlyPublished)
                        Published++;

                    break;
                case OtaReviewSyncOutcome.Failed:
                    ReadFailed++;
                    break;
                default:
                    NotReadable++;
                    break;
            }
        }
    }
}
