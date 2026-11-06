using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Tries again the check-in writes to Octorate that did not go through (E03/F01/US01/T03): every 15 minutes, the guest links and the states of
/// a stay that are <see cref="OctorateSyncStatus.Pending"/> or <see cref="OctorateSyncStatus.Failed"/>, with fewer than
/// <c>Octorate:CheckInMaxAttempts</c> attempts and a last attempt older than 15 minutes times the attempts (the backoff). Behind
/// <c>Features:OctorateConnector</c> (the registration removes it while the flag is off).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The service does the work (<see cref="ICheckInOctorateSyncService.RetryGuestsAsync"/>, which takes up only the guests whose link is
/// Pending or Failed and creates no link, never a guest changed or added since the host confirmed;
/// <see cref="ICheckInOctorateSyncService.SetStayStateAsync"/>): it counts the attempt, keeps the code of a failure and raises the one "To do" at
/// the last failed attempt; a later success closes it.</item>
/// <item>A system read over every org; each booking is retried for its own org under <see cref="IOctorateOrgScope.Use"/>. A booking that fails
/// does not stop the others. At most <see cref="MaxBookingsPerRun"/> bookings per run; the next run takes the rest.</item>
/// <item>No retry by Hangfire: the next run reads the rows again. The lock waits <see cref="LockTimeoutSeconds"/>, the value of
/// <c>JobLockTimeouts.FrequentSeconds</c>, far less than the interval.</item>
/// </list>
/// </remarks>
public class OctorateCheckInRetryJob(
    AppDbContext db,
    ICheckInOctorateSyncService sync,
    IOctorateOrgScope orgScope,
    IOptions<OctorateOptions> options,
    ILogger<OctorateCheckInRetryJob> logger,
    TimeProvider? timeProvider = null)
{
    public const string RecurringJobId = "octorate-checkin-retry";

    /// <summary>Every 15 minutes (UTC).</summary>
    public const string Cron = "*/15 * * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.FrequentSeconds</c>.</summary>
    public const int LockTimeoutSeconds = 60;

    /// <summary>The bookings a run retries at most.</summary>
    public const int MaxBookingsPerRun = 200;

    /// <summary>The wait after each attempt: a write is due again 15 minutes times its attempts after the last one.</summary>
    public static readonly TimeSpan BackoffStep = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>True when a write with <paramref name="attempts"/> attempts, the last at <paramref name="lastAttemptAt"/>, is due again.</summary>
    public static bool IsDue(int attempts, DateTime? lastAttemptAt, DateTime now) =>
        lastAttemptAt is not { } last || last <= now - (BackoffStep * Math.Max(1, attempts));

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(options.Value.CheckInMaxAttempts, 1, 20);
        var now = _clock.GetUtcNow().UtcDateTime;

        var links = await db.StayGuestOctorateLinks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(l => (l.Status == OctorateSyncStatus.Pending || l.Status == OctorateSyncStatus.Failed) && l.Attempts < maxAttempts)
            .Select(l => new { l.OrgId, l.BookingId, l.Attempts, l.UpdatedAt })
            .ToListAsync(cancellationToken);
        var states = await db.OctorateStayStateSyncs
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => (s.Status == OctorateSyncStatus.Pending || s.Status == OctorateSyncStatus.Failed) && s.Attempts < maxAttempts)
            .Select(s => new { s.OrgId, s.BookingId, s.State, s.Attempts, s.LastAttemptAt })
            .ToListAsync(cancellationToken);

        var guestTargets = links
            .Where(l => IsDue(l.Attempts, l.UpdatedAt, now))
            .Select(l => (l.OrgId, l.BookingId))
            .Distinct()
            .ToList();
        var stateTargets = states
            .Where(s => IsDue(s.Attempts, s.LastAttemptAt, now))
            .Select(s => (s.OrgId, s.BookingId, s.State))
            .ToList();

        var bookings = guestTargets.Select(t => t.BookingId)
            .Concat(stateTargets.Select(t => t.BookingId))
            .Distinct()
            .Take(MaxBookingsPerRun)
            .ToHashSet();

        var retried = 0;
        var failed = 0;
        foreach (var org in guestTargets.Select(t => t.OrgId).Concat(stateTargets.Select(t => t.OrgId)).Distinct())
        {
            using (orgScope.Use(org))
            {
                foreach (var (_, bookingId) in guestTargets.Where(t => t.OrgId == org && bookings.Contains(t.BookingId)))
                {
                    if (await TryAsync(bookingId, () => sync.RetryGuestsAsync(bookingId, cancellationToken), cancellationToken))
                        retried++;
                    else
                        failed++;
                }

                foreach (var (_, bookingId, state) in stateTargets.Where(t => t.OrgId == org && bookings.Contains(t.BookingId)))
                {
                    if (await TryAsync(bookingId, () => sync.SetStayStateAsync(bookingId, state, cancellationToken), cancellationToken))
                        retried++;
                    else
                        failed++;
                }
            }
        }

        logger.LogInformation(
            "Octorate check-in retry: {Bookings} bookings due, {Retried} writes tried again, {Failed} not tried",
            bookings.Count, retried, failed);
    }

    /// <summary>One retry; an error of a booking is logged (ids only) and never stops the others.</summary>
    private async Task<bool> TryAsync<T>(Guid bookingId, Func<Task<T>> retry, CancellationToken cancellationToken)
    {
        try
        {
            await retry();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is NotFoundException)
                logger.LogInformation("Octorate check-in retry: booking {BookingId} no longer exists", bookingId);
            else
                logger.LogError(ex, "Octorate check-in retry of booking {BookingId} failed", bookingId);
            return false;
        }
        finally
        {
            // Each booking starts clean: nothing left half-changed by one is saved with the next.
            db.ChangeTracker.Clear();
        }
    }
}
