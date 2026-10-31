using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestExpiryService"/>
/// <remarks>
/// <para><b>A system job.</b> It works across every supplier on purpose: the holds it deletes are the ones whose time has passed, and
/// the requests it cancels are the ones whose deadline has passed, whoever they belong to. It is run only by Hangfire
/// (<c>service-request-expiry</c>) and reachable from no request; the statement on <c>ShowcaseBookingHolds</c> is
/// <see cref="ExpiredHoldIdsOf"/>, which <c>ShowcaseBookingTenancyTests</c> lists as the one system statement.</para>
/// <para><b>Nothing to lock but the run</b> — except for a hold that has a block on the customer's card (SP-15c-a). A hold past its expiry no
/// longer counts for the slot planner and cannot be checked anymore (the check refuses it under the supplier's lock), so deleting it needs no
/// lock of the calendar. The request cancellation is <see cref="IServiceRequestAutoCancelService.CancelUnansweredShowcaseAsync"/>: one request
/// at a time, saved only if nobody touched it since it was read.</para>
/// <para><b>The customer's card is never left blocked.</b> An expired hold that never became a request and has a PaymentIntent
/// (<see cref="UnconsumedPaidHoldsOf"/>: ids and supplier org only) is released one at a time by
/// <see cref="IShowcaseBookingService.ReleaseExpiredHoldAsync"/> — the block first, under the supplier's calendar lock, then the hold — and a
/// hold whose block could not be released is <b>kept</b> for the next run. A hold that became a request is deleted as before: its PaymentIntent
/// belongs to the payment now and is never touched here.</para>
/// <para><b>The reminders (SP-10c)</b> are the last pass, after the requests whose deadline had passed were closed: the supplier of a request on
/// measure that nobody answered is reminded at half of the time it has and ten minutes before the deadline, each once
/// (<see cref="IShowcaseReplyReminderService"/>). A failure of that pass is logged and never undoes the upkeep above it.</para>
/// </remarks>
public sealed class ServiceRequestExpiryService(
    AppDbContext db,
    IServiceRequestAutoCancelService autoCancel,
    ILogger<ServiceRequestExpiryService> logger,
    TimeProvider? timeProvider = null,
    IShowcaseBookingService? holds = null,
    IShowcaseReplyReminderService? reminders = null) : IServiceRequestExpiryService
{
    internal const string RunLockKey = "service-request-expiry";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceRequestExpiryRun> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceRequestExpiryRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Service request expiry run skipped: another run is in progress");
            return ServiceRequestExpiryRun.LockTaken;
        }

        // The holds of the bookings and the inquiries of the requests on measure (SP-10b) that nobody checked: both wait for an e-mail
        // check and lapse alike, so the run counts them together.
        var holdsDeleted = await DeleteExpiredHoldsAsync(cancellationToken) + await DeleteExpiredInquiriesAsync(cancellationToken);
        var cancellation = await autoCancel.CancelUnansweredShowcaseAsync(cancellationToken);
        var reminded = await SendRemindersAsync(cancellationToken);

        if (holdsDeleted > 0 || cancellation.Cancelled > 0 || cancellation.Failed > 0 || reminded.Sent > 0)
        {
            logger.LogInformation(
                "Service request expiry: {Holds} holds deleted, {Cancelled} showcase requests cancelled, {Conflicts} changed under the run, {Failed} failed, {Reminders} reminders queued",
                holdsDeleted, cancellation.Cancelled, cancellation.Conflicts, cancellation.Failed, reminded.Sent);
        }

        return new ServiceRequestExpiryRun(
            Skipped: false, holdsDeleted, cancellation.Cancelled, cancellation.Conflicts, cancellation.Failed, reminded.Sent);
    }

    /// <summary>The reminders to the suppliers of the requests on measure nobody answered yet (SP-10c); a pass that fails is logged and counted as nothing.</summary>
    private async Task<ShowcaseReplyReminderRun> SendRemindersAsync(CancellationToken cancellationToken)
    {
        if (reminders is null)
            return ShowcaseReplyReminderRun.Empty;

        try
        {
            return await reminders.SendDueAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogError(ex, "Service request expiry: the reminders to the suppliers could not be sent; the next run tries again");
            return ShowcaseReplyReminderRun.Empty;
        }
    }

    /// <summary>
    /// The holds whose expiry has passed at <paramref name="nowUtc"/>, the oldest first, at most
    /// <see cref="ShowcaseBookingLimits.ExpiryBatchSize"/>: the ones nobody checked and the ones that were checked and kept for the
    /// replay of the link. Only ids: the payload is not read.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> ExpiredHoldIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ShowcaseBookingHolds
            .AsNoTracking()
            .Where(h => h.ExpiresAt <= nowUtc)
            .OrderBy(h => h.ExpiresAt)
            .ThenBy(h => h.Id)
            .Select(h => h.Id)
            .Take(ShowcaseBookingLimits.ExpiryBatchSize);

    /// <summary>
    /// The inquiries (SP-10b) whose expiry has passed at <paramref name="nowUtc"/>, the oldest first, at most
    /// <see cref="ShowcaseBookingLimits.ExpiryBatchSize"/>: the ones nobody checked and the ones that were checked and kept for the replay of
    /// the link. Only ids: the payload (the words of the customer) is not read.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> ExpiredInquiryIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ShowcaseInquiries
            .AsNoTracking()
            .Where(i => i.ExpiresAt <= nowUtc)
            .OrderBy(i => i.ExpiresAt)
            .ThenBy(i => i.Id)
            .Select(i => i.Id)
            .Take(ShowcaseBookingLimits.ExpiryBatchSize);

    private async Task<int> DeleteExpiredInquiriesAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var ids = await ExpiredInquiryIdsOf(db, now).ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        // By key, from a stub: the rows are deleted without being read (their payload is personal data and is encrypted).
        db.ChangeTracker.Clear();
        db.ShowcaseInquiries.RemoveRange(ids.Select(id => new ShowcaseInquiry { Id = id }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another run removed some of them first: the next run finds what is left.
            db.ChangeTracker.Clear();
            logger.LogInformation("Service request expiry: some expired inquiries were already gone");
            return 0;
        }

        return ids.Count;
    }

    /// <summary>
    /// Of <paramref name="ids"/> (expired holds), the ones that never became a request and have a block on the customer's card, with the supplier
    /// org that keys them. Ids and the org only: no payload, no e-mail index, no token, not even the PaymentIntent (the release reads it again
    /// under the lock).
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<ExpiredPaidHold> UnconsumedPaidHoldsOf(AppDbContext db, IReadOnlyCollection<Guid> ids) =>
        db.ShowcaseBookingHolds
            .AsNoTracking()
            .Where(h => ids.Contains(h.Id) && h.StripePaymentIntentId != null && h.ConsumedAt == null)
            .Select(h => new ExpiredPaidHold(h.Id, h.OrgId));

    /// <summary>An expired hold that holds a block on a card: where it is and which supplier's it is.</summary>
    internal sealed record ExpiredPaidHold(Guid Id, Guid OrgId);

    private async Task<int> DeleteExpiredHoldsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var ids = await ExpiredHoldIdsOf(db, now).ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        // SP-15c-a: the holds whose card is blocked are released one at a time, the block first. The others (never paid, or already a request)
        // are deleted together below. A hold the release could not delete stays for the next run.
        var released = 0;
        var paid = await UnconsumedPaidHoldsOf(db, ids).ToListAsync(cancellationToken);
        if (paid.Count > 0)
        {
            var paidIds = paid.Select(p => p.Id).ToHashSet();
            if (holds is not null)
            {
                foreach (var hold in paid)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (await holds.ReleaseExpiredHoldAsync(hold.OrgId, hold.Id, cancellationToken))
                            released++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        db.ChangeTracker.Clear();
                        logger.LogError(ex, "Service request expiry: expired hold {HoldId} could not be released; the next run tries again", hold.Id);
                    }
                }
            }
            else
            {
                logger.LogWarning("Service request expiry: {Count} expired holds have a card block and there is no way to release it; kept", paid.Count);
            }

            // Whatever happened to them, they are not deleted by key below: a block that was not released must not lose its hold.
            ids = ids.Where(id => !paidIds.Contains(id)).ToList();
        }

        if (ids.Count == 0)
            return released;

        // By key, from a stub: the rows are deleted without being read (their payload is personal data and is encrypted).
        db.ChangeTracker.Clear();
        db.ShowcaseBookingHolds.RemoveRange(ids.Select(id => new ShowcaseBookingHold { Id = id }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another run (or a check of the link) removed or changed some of them first: the next run finds what is left.
            db.ChangeTracker.Clear();
            logger.LogInformation("Service request expiry: some expired holds were already gone");
            return released;
        }

        return released + ids.Count;
    }
}
