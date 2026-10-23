using System.Data;
using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The end of a long-term lease in force (LR-14, <see cref="ILeaseTermService"/>): the tacit renewal the hourly job applies, the notice
/// of non-renewal and the early termination the landlord records. A lease renews by itself when nobody gave notice
/// (<see cref="LeaseRenewal"/>), so what tells whether it is over is <see cref="LeaseContract.CurrentTermEndDate"/>, not the end the
/// contract states.
/// </summary>
/// <remarks>
/// <para><b>One writer at a time.</b> Every change takes the row lock of the lease (<c>SELECT … FOR UPDATE</c>, PostgreSQL, the one the
/// signature and the guided draft take) and reads the lease after it, so a notice, a termination and a renewal of the same lease run one after
/// the other, each on what the previous one committed. A lease that stays locked for more than five seconds is busy: 409
/// <c>lease_busy</c>, nothing written (for the job, left to the next run).</para>
/// <para><b>Idempotent by construction.</b> The renewal asks "is it due?" again once the row is locked, and a lease that was renewed is no longer
/// due: a second run, an overlapping run, a retry or a manual trigger finds nothing to do and never adds a period twice. A lease with a notice,
/// ended early, of a transitory type, or not signed by every party is never due. A lease that had already ended when the renewal was
/// introduced was recorded as concluded by the migration (<c>TerminatedOn</c>), so it is not due either (no retroactive renewal).</para>
/// <para><b>The rent plan moves with the lease.</b> The renewal extends an active plan to the new end and the termination cancels its
/// future installments through <see cref="IRentBillingService"/>, in the same transaction: the lease and its plan change together or not at
/// all. The lock of the plan (<see cref="PostgresAdvisoryLocks.Scope.RentLease"/>) is taken before the row of the lease, the order of
/// every other writer of the installments. No e-mail goes to a tenant from here.</para>
/// <para><b>No personal data in a log or an event:</b> ids, dates and counts.</para>
/// </remarks>
public sealed class LeaseTermService(
    AppDbContext db,
    IRentBillingService rentBilling,
    ILogger<LeaseTermService> logger,
    TimeProvider? timeProvider = null) : ILeaseTermService
{
    /// <summary>The key of the session lock of a run (<see cref="PostgresAdvisoryLocks.Scope.LeaseTermRenewalRun"/>).</summary>
    internal const string RunLockKey = "lease-term-renewal";

    /// <summary>Leases read per batch.</summary>
    internal const int BatchSize = 100;

    /// <summary>Batches per run: 2,000 leases an hour; the next run takes the rest.</summary>
    internal const int MaxBatchesPerRun = 20;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ---- what the landlord records -------------------------------------------------------------------------------

    public async Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default) =>
        await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.Id == leaseId)
            .Select(l => (Guid?)l.PropertyId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task RecordNoticeAsync(Guid leaseId, LeaseNoticeParty from, DateOnly on, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(from))
            throw new ArgumentOutOfRangeException(nameof(from), from, "Unknown party");

        await using var transaction = await BeginLockedAsync(leaseId, lockRent: false, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, ignoreTenant: false, cancellationToken);
        EnsureInForce(lease);
        if (!LeaseRenewal.Renews(lease.ContractType))
            throw new DomainRuleException(LeaseLifecycleCodes.TypeHasNoRenewal, "LeaseTypeHasNoRenewal");
        EnsureNotTerminated(lease);
        if (lease.NoticeOn is not null)
            throw new DomainConflictException(LeaseLifecycleCodes.NoticeAlreadyRecorded, "LeaseNoticeAlreadyRecorded");

        // A notice is given on a day that has come, and not before the lease began.
        if (on > _clock.TodayInRomeAsDateOnly() || on < RomeCalendar.DateInRome(lease.StartDate))
            throw new DomainRuleException(LeaseLifecycleCodes.NoticeDateInvalid, "LeaseNoticeDateInvalid");

        var now = UtcNow;
        lease.NoticeOn = ToStoredDate(on);
        lease.NoticeFrom = from;
        lease.UpdatedAt = now;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.NoticeRecorded,
            OccurredAt = now,
            Payload = $"{(from == LeaseNoticeParty.Landlord ? "landlord" : "tenant")}:{Format(on)}",
        });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Lease notice of non-renewal recorded. LeaseId={LeaseId} From={From}", lease.Id, from);
    }

    public async Task RemoveNoticeAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, lockRent: false, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, ignoreTenant: false, cancellationToken);

        // Taking back a notice that is not there changes nothing.
        if (lease.NoticeOn is not { } given)
            return;

        // Once the period is over the lease has ended, or is about to renew: the notice is part of what happened and stays.
        if (lease.CurrentTermEndDate < _clock.TodayInRome())
            throw new DomainConflictException(LeaseLifecycleCodes.TermAlreadyEnded, "LeaseTermAlreadyEnded");

        var now = UtcNow;
        lease.NoticeOn = null;
        lease.NoticeFrom = null;
        lease.UpdatedAt = now;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.NoticeRemoved,
            OccurredAt = now,
            Payload = Format(RomeCalendar.DateInRome(given)),
        });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Lease notice of non-renewal taken back. LeaseId={LeaseId}", lease.Id);
    }

    public async Task TerminateAsync(Guid leaseId, DateOnly on, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RentPaymentNotice> notices;
        int cancelled;
        await using (var transaction = await BeginLockedAsync(leaseId, lockRent: true, cancellationToken))
        {
            var lease = await LoadForUpdateAsync(leaseId, ignoreTenant: false, cancellationToken);
            EnsureInForce(lease);
            EnsureNotTerminated(lease);

            // The last day of the lease is a day of the lease: from its start up to the end of the period it is in now. A day in the past
            // is allowed, for a lease the landlord knows ended earlier.
            if (on < RomeCalendar.DateInRome(lease.StartDate) || on > RomeCalendar.DateInRome(lease.CurrentTermEndDate))
                throw new DomainRuleException(LeaseLifecycleCodes.TerminationDateInvalid, "LeaseTerminationDateInvalid");

            var now = UtcNow;
            lease.TerminatedOn = ToStoredDate(on);
            lease.CurrentTermEndDate = lease.TerminatedOn.Value;
            lease.UpdatedAt = now;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.Terminated,
                OccurredAt = now,
                Payload = Format(on),
            });
            await db.SaveChangesAsync(cancellationToken);

            // The installments that start after the last day, in the same transaction: the lease is never ended with its future rent still due.
            var result = await rentBilling.CancelInstallmentsAfterAsync(lease.Id, on, cancellationToken);
            cancelled = result.Cancelled;
            notices = result.Notices;
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        // A payment that turned out to be made while the installments were cancelled: the landlord is told, as for any payment.
        foreach (var notice in notices)
            await rentBilling.CompleteAsync(notice, cancellationToken);

        logger.LogInformation("Lease ended early. LeaseId={LeaseId} LastDay={LastDay} InstallmentsCancelled={Cancelled}", leaseId, Format(on), cancelled);
    }

    // ---- the tacit renewal (the job) -----------------------------------------------------------------------------

    public async Task<LeaseTermRenewalRun> RenewDueAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.LeaseTermRenewalRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Lease term renewal skipped: another run is in progress");
            return LeaseTermRenewalRun.SkippedRun;
        }

        var today = _clock.TodayInRome();
        await RepairUnsetEndsAsync(today, cancellationToken);

        int leases = 0, periods = 0, left = 0;
        // Every lease this run looked at, renewed or not: none is read twice, so the cap of periods per lease holds for the run and one
        // that refuses (busy, a failure) cannot stop the ones behind it.
        var seen = new HashSet<Guid>();
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var skip = seen.ToArray();
            var ids = await db.LeaseContracts
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(LeaseRenewal.DueOn(today))
                .Where(l => !skip.Contains(l.Id))
                .OrderBy(l => l.CurrentTermEndDate)
                .ThenBy(l => l.Id)
                .Select(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
                break;

            foreach (var id in ids)
            {
                seen.Add(id);
                var added = await TryRenewAsync(id, today, cancellationToken);
                if (added is null)
                {
                    left++;
                }
                else if (added.Value > 0)
                {
                    leases++;
                    periods += added.Value;
                }
            }

            if (ids.Count < BatchSize)
                break;
        }

        if (leases > 0 || left > 0)
            logger.LogInformation("Lease term renewal: {Leases} leases renewed by {Periods} periods, {Left} left for the next run", leases, periods, left);

        return new LeaseTermRenewalRun(false, leases, periods, left);
    }

    /// <summary>
    /// A lease written by a server that did not know the column yet (a deploy in progress) keeps the starting value of the column, which
    /// the database stores as <c>-infinity</c>: before the end of its own contract. Nothing else puts the current end before the end of
    /// the contract without an early termination (which records <see cref="LeaseContract.TerminatedOn"/>), so those rows get the end of
    /// the contract, the value they would have had. A lease in force whose contract had already ended when it was written is a lease that
    /// came in over: like the ones the migration found ended it is recorded as concluded (<see cref="LeaseContract.TerminatedOn"/> = its last
    /// day) instead of being renewed afterwards (no retroactive renewal). Idempotent; a relational database only.
    /// </summary>
    private async Task RepairUnsetEndsAsync(DateTime today, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return;

        // First the ones that were already over (the same rule as the renewal says which are in force and of a renewable type), then the rest.
        var concluded = await db.LeaseContracts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(LeaseRenewal.DueOn(today))
            .Where(l => l.CurrentTermEndDate < l.EndDate && l.EndDate < today)
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.CurrentTermEndDate, l => l.EndDate).SetProperty(l => l.TerminatedOn, l => (DateTime?)l.EndDate),
                cancellationToken);
        var repaired = await db.LeaseContracts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(l => l.TerminatedOn == null && l.CurrentTermEndDate < l.EndDate)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.CurrentTermEndDate, l => l.EndDate), cancellationToken);
        if (concluded + repaired > 0)
        {
            logger.LogWarning(
                "Lease term renewal: {Leases} leases written without the end of their period were given the end of their contract, {Concluded} of them already over and recorded as concluded",
                concluded + repaired, concluded);
        }
    }

    /// <summary>
    /// Brings one lease forward while it is still due once its row is locked: the periods added (0 when it was no longer due, a notice or an
    /// early termination came first or another run renewed it), or <c>null</c> when it could not be done now (busy, a failure), left for the next run.
    /// </summary>
    internal async Task<int?> TryRenewAsync(Guid leaseId, DateTime today, CancellationToken cancellationToken)
    {
        try
        {
            // The plan of the rent is extended in the same transaction, so its lock comes first (see BeginLockedAsync).
            await using var transaction = await BeginLockedAsync(leaseId, lockRent: true, cancellationToken);

            // The job has no tenant: it reads every org, on purpose.
            var lease = await LoadForUpdateAsync(leaseId, ignoreTenant: true, cancellationToken);
            if (!LeaseRenewal.IsDue(lease, today))
                return 0;

            var added = 0;
            var now = UtcNow;
            while (added < LeaseRenewal.MaxPeriodsPerRun
                   && lease.CurrentTermEndDate < today
                   && LeaseRenewal.NextEnd(lease.CurrentTermEndDate, lease.ContractType) is { } next)
            {
                lease.CurrentTermEndDate = next;
                db.LeaseEvents.Add(new LeaseEvent
                {
                    LeaseContractId = lease.Id,
                    EventType = LeaseEventType.TermRenewed,
                    OccurredAt = now,
                    Payload = Format(RomeCalendar.DateInRome(next)),
                });
                added++;
            }

            lease.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            // The plan of the rent follows, in the same transaction (the lock of the lease joins it).
            var installments = await rentBilling.ExtendScheduleToCurrentTermAsync(leaseId, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Lease renewed by itself. LeaseId={LeaseId} Periods={Periods} CurrentTermEndDate={End} InstallmentsAdded={Installments}",
                leaseId, added, Format(RomeCalendar.DateInRome(lease.CurrentTermEndDate)), installments);
            return added;
        }
        catch (Exception ex) when (LeaseDraftLocking.IsContention(ex) || ex is DomainConflictException { Code: LeaseLifecycleCodes.Busy })
        {
            // Somebody is changing the lease right now: the next run looks again.
            logger.LogInformation("Lease term renewal: the lease is busy and is left for the next run. LeaseId={LeaseId}", leaseId);
            return null;
        }
        catch (NotFoundException)
        {
            // Deleted since the list was read (a draft is never due, but a row can go in any case): nothing to renew.
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One lease that cannot be renewed (a failure of the plan, of the database) must not stop the ones behind it. Its id is not personal data.
            logger.LogWarning(ex, "Lease term renewal: a lease could not be renewed and is left for the next run. LeaseId={LeaseId}", leaseId);
            return null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    // ---- locking and loading -------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the transaction (a relational database only) and locks the row of the lease until it ends; a second writer waits here and then reads
    /// what the first committed. The change tracker is cleared so nothing read earlier in the request is taken for current.
    /// </summary>
    /// <param name="lockRent">
    /// The change touches the rent plan of the lease (the renewal extends it, the termination cancels its installments): the lock of the plan
    /// is taken <b>before</b> the row of the lease. That is the order of every other writer of the installments, which holds the lock of the
    /// plan and then inserts installments, and an insert needs the row of the lease (the foreign key): with the row first, a change here and
    /// the schedule being set up would wait for each other.
    /// </param>
    private async Task<IDbContextTransaction?> BeginLockedAsync(Guid leaseId, bool lockRent, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            if (lockRent && PostgresAdvisoryLocks.IsSupported(db))
            {
                // The wait is bounded like the one for the row; the lock joins the transaction opened above (and is kept until it ends).
                await db.Database.ExecuteSqlRawAsync(LeaseDraftLocking.SetLockTimeoutSql, cancellationToken);
                await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                    db, cancellationToken, (PostgresAdvisoryLocks.Scope.RentLease, leaseId.ToString("N")));
            }

            await LeaseDraftLocking.LockRowAsync(db, leaseId, cancellationToken);
            db.ChangeTracker.Clear();
            return transaction;
        }
        catch (Exception ex) when (LeaseDraftLocking.IsContention(ex))
        {
            // Somebody else holds the lease for longer than a few seconds, or the database stopped this change in a race: nothing was written.
            if (transaction is not null)
                await transaction.DisposeAsync();
            logger.LogInformation("Lease busy: the change was refused. LeaseId={LeaseId}", leaseId);
            throw new DomainConflictException(LeaseLifecycleCodes.Busy, "LeaseBusy");
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<LeaseContract> LoadForUpdateAsync(Guid leaseId, bool ignoreTenant, CancellationToken cancellationToken)
    {
        var query = ignoreTenant ? db.LeaseContracts.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]) : db.LeaseContracts;
        return await query.FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw new NotFoundException($"Lease {leaseId} not found.") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };
    }

    private static void EnsureInForce(LeaseContract lease)
    {
        if (!LeaseRenewal.IsInForce(lease.Status))
            throw new DomainConflictException(LeaseLifecycleCodes.NotInForce, "LeaseNotInForce");
    }

    private static void EnsureNotTerminated(LeaseContract lease)
    {
        if (lease.TerminatedOn is not null)
            throw new DomainConflictException(LeaseLifecycleCodes.AlreadyTerminated, "LeaseAlreadyTerminated");
    }

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
