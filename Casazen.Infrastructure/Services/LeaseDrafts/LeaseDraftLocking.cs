using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Infrastructure.Services.LeaseDrafts;

/// <summary>
/// The row lock every change of a lease draft takes (LR-12), shared by the draft service and the retention job so they take it the same
/// way and read a lock that does not come the same way: a lease whose lock is held for more than a few seconds is a lease that is busy,
/// which the caller treats as a lost race (409 for a person, skip for the job), never as a timeout of the connection (500).
/// </summary>
internal static class LeaseDraftLocking
{
    /// <summary>
    /// Local to the transaction that runs it: a wait for a lock longer than this ends with <c>55P03</c>. Long enough for the usual holder
    /// (a confirmation that reads the energy certificate from the storage), short enough for a person who is typing.
    /// </summary>
    public const string SetLockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    /// <summary>
    /// Locks the row of the lease until the transaction ends (<c>SELECT … FOR UPDATE</c>, the same the signature takes). PostgreSQL
    /// only: elsewhere (EF InMemory) there is no lock and nothing to wait for. The transaction must already be open.
    /// </summary>
    public static async Task LockRowAsync(AppDbContext db, Guid leaseId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return;

        await db.Database.ExecuteSqlRawAsync(SetLockTimeoutSql, cancellationToken);
        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {leaseId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// True when the database gave up waiting for a lock (<c>lock_timeout</c>), stopped a transaction that lost a race with another
    /// (serialization failure, deadlock), or the command timed out while it waited.
    /// </summary>
    public static bool IsContention(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;

            if (current is TimeoutException)
                return true;
        }

        return false;
    }
}
