using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services.LeaseDrafts;

/// <summary>
/// The rule of the days of a lease (<see cref="LeasePeriodRules"/>) asked of the database: the lease in the way, if any, for the soft
/// warning of the wizard (<see cref="FindAsync"/>), and the hard check at the steps that make a lease a contract
/// (<see cref="EnsureFreeAsync"/>, LR-OVERLAP).
/// </summary>
/// <remarks>
/// <para><b>One at a time per property.</b> <see cref="EnsureFreeAsync"/> takes the advisory lock of the property
/// (<see cref="PostgresAdvisoryLocks.Scope.LeasePeriod"/>) <b>inside the transaction that then writes</b>, after the row lock of the lease,
/// and looks for the lease in the way only once it holds it: the one that waits sees what the one before it committed, so two leases of the
/// same property cannot both pass the check before either is written. The lock is released by the commit or the rollback of the caller's
/// transaction (it never opens one of its own that outlives the check). Outside PostgreSQL (EF InMemory) nothing is locked.</para>
/// <para><b>A lock that cannot be had</b> in a few seconds (<c>lock_timeout</c>) is a lost race like any other of the lease: 409
/// <c>lease_draft_conflict</c>, nothing written, never a 500.</para>
/// </remarks>
internal static class LeasePeriodGuard
{
    /// <summary>The first lease (earliest start) of <paramref name="propertyId"/> that covers some of the days <paramref name="from"/> to <paramref name="to"/>, or <c>null</c>.</summary>
    public static async Task<LeasePeriodConflict?> FindAsync(
        AppDbContext db, Guid propertyId, Guid exceptLeaseId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        if (!LeasePeriodRules.HasPeriod(from, to))
            return null;

        var row = await db.LeaseContracts
            .AsNoTracking()
            .Where(LeasePeriodRules.InTheWayOf(propertyId, exceptLeaseId, from, to))
            .OrderBy(l => l.StartDate)
            .ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.StartDate, LastDay = l.TerminatedOn ?? l.CurrentTermEndDate })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new LeasePeriodConflict(row.Id, RomeCalendar.DateInRome(row.StartDate), RomeCalendar.DateInRome(row.LastDay));
    }

    /// <summary>
    /// 409 <c>lease_period_overlaps</c> when another lease of the property covers some of the days <paramref name="from"/> to
    /// <paramref name="to"/>. Call it inside the transaction that writes the step, after the row lock of the lease. A period that is not a
    /// whole one (a draft without its dates) is not checked: the step has its own need for that.
    /// </summary>
    public static async Task EnsureFreeAsync(
        AppDbContext db, Guid propertyId, Guid leaseId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        if (!LeasePeriodRules.HasPeriod(from, to))
            return;

        try
        {
            if (PostgresAdvisoryLocks.IsSupported(db))
                await db.Database.ExecuteSqlRawAsync(LeaseDraftLocking.SetLockTimeoutSql, cancellationToken);

            // Null when the caller's transaction is the one that holds the lock; if there were none, the lock lives only for the check.
            await using var own = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, (PostgresAdvisoryLocks.Scope.LeasePeriod, propertyId.ToString("N")));

            if (await FindAsync(db, propertyId, leaseId, from, to, cancellationToken) is { } conflict)
                throw Overlaps(conflict);
        }
        catch (Exception ex) when (ex is not DomainException && LeaseDraftLocking.IsContention(ex))
        {
            throw new DomainConflictException(LeaseDraftErrorCodes.Conflict, "LeaseDraftConflict");
        }
    }

    /// <summary>The 409 for a lease in the way: the code, the localized text with the two dates, and the members the client links from.</summary>
    public static DomainConflictException Overlaps(LeasePeriodConflict conflict) =>
        new(LeasePeriodCodes.Overlaps, "LeasePeriodOverlaps", conflict.ConflictingFrom, conflict.ConflictingTo)
        {
            Extensions = new Dictionary<string, object?>
            {
                ["conflictingLeaseId"] = conflict.ConflictingLeaseId,
                ["conflictingFrom"] = conflict.ConflictingFrom,
                ["conflictingTo"] = conflict.ConflictingTo,
            },
        };
}
