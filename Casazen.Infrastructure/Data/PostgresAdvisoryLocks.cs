using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// Transaction-scoped PostgreSQL advisory locks (<c>pg_advisory_xact_lock(int, int)</c>) that serialize a
/// check-then-insert across requests and API instances (A1-14, A1-21). The lock is released by the
/// commit or rollback of the transaction that holds it. A job run that must not overlap with another run takes a
/// session-level try lock instead (<see cref="TryAcquireSessionLockAsync"/>).
/// </summary>
/// <remarks>
/// Keys use the two-integer form: the first integer is a <see cref="Scope"/>, the second a stable hash
/// of the key text. The two-integer key space never overlaps the single-<c>bigint</c> locks of
/// <c>BookingRepository</c>. A hash collision only serializes two unrelated keys of the same scope.
/// Outside PostgreSQL (EF InMemory in unit tests) nothing is locked and no transaction is opened.
/// </remarks>
internal static class PostgresAdvisoryLocks
{
    /// <summary>First integer of the lock key: what the lock protects.</summary>
    internal enum Scope
    {
        /// <summary>Org provisioning of one user (one org per user).</summary>
        OrgProvisioningUser = 1_001,

        /// <summary>Allocation of one org slug (unique <c>Orgs.Slug</c>).</summary>
        OrgSlug = 1_002,

        /// <summary>Property count and insert of one org (plan limit).</summary>
        OrgPropertySlot = 1_003,

        /// <summary>Stripe Checkout of one org's plan: at most one subscription per org (A1-10).</summary>
        OrgBillingCheckout = 1_004,

        /// <summary>Refunds of one payment: the refundable amount is checked and reserved one request at a time (BK-02).</summary>
        PaymentRefund = 1_005,

        /// <summary>Cancellation of one booking: a double click cancels and refunds once (BK-02).</summary>
        BookingCancellation = 1_006,

        /// <summary>Acceptance of one supplier invite (key: token hash): an invite is used at most once (SU-01).</summary>
        SupplierInvite = 1_007,

        /// <summary>Claim of one supplier profile (key: supplier org id): at most one account is linked (SU-02).</summary>
        SupplierClaim = 1_008,

        /// <summary>Deactivation of any user (single key): the last active platform admin is never deactivated (PL-03).</summary>
        UserDeactivation = 1_009,

        /// <summary>
        /// Import of the iCal feed of one property (key: property id): two sync runs (the 15-minute job and the first
        /// sync of a new URL) never write the same blocks at once (PC-10, A2-12).
        /// </summary>
        PropertyICalSync = 1_010,

        /// <summary>
        /// One run of the hourly stay alerts (single key, session lock held for the whole run): two runs never send the
        /// same alerts at once, even outside Hangfire's own lock (CO-10, A5-11).
        /// </summary>
        StayAlertsRun = 1_011,

        /// <summary>
        /// Stripe Connect account of one org (key: org id): two clicks on "Collega Stripe" create one Express account
        /// (BK-09, A3-19).
        /// </summary>
        OrgConnectAccount = 1_012,
    }

    public static bool IsSupported(DbContext context) => context.Database.IsNpgsql();

    /// <summary>
    /// Opens a READ COMMITTED transaction and takes the given locks, in order. When the context already has a
    /// transaction the locks join it and <c>null</c> is returned (the owner of that transaction commits).
    /// Also <c>null</c> when the provider is not PostgreSQL.
    /// </summary>
    /// <remarks>
    /// READ COMMITTED on purpose: every statement after the lock sees the rows committed by the previous
    /// holder. Under REPEATABLE READ or SERIALIZABLE the snapshot would be taken by the lock statement,
    /// before the wait, and a count would miss the previous holder's insert.
    /// </remarks>
    public static async Task<IDbContextTransaction?> BeginLockedTransactionAsync(
        DbContext context,
        CancellationToken cancellationToken,
        params (Scope Scope, string Key)[] locks)
    {
        if (!IsSupported(context))
            return null;

        IDbContextTransaction? transaction = null;
        if (context.Database.CurrentTransaction is null)
            transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            foreach (var (scope, key) in locks)
            {
                var scopeId = (int)scope;
                var keyHash = Hash(key);
                await context.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock({scopeId}, {keyHash})", cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            throw;
        }

        return transaction;
    }

    /// <summary>
    /// Tries the session-level lock <c>pg_try_advisory_lock(scope, hash(key))</c> without waiting and keeps the connection
    /// open until the returned handle is disposed, which releases it. Returns <c>null</c> when another session holds
    /// the lock. Outside PostgreSQL there is nothing to lock: a handle that does nothing is returned.
    /// </summary>
    /// <remarks>
    /// Meant for a whole job run that commits several transactions of its own: a transaction-scoped lock would be
    /// released by the first commit. The lock also dies with the connection if the process crashes.
    /// </remarks>
    public static async Task<IAsyncDisposable?> TryAcquireSessionLockAsync(
        DbContext context,
        Scope scope,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsSupported(context))
            return NoLock.Instance;

        var scopeId = (int)scope;
        var keyHash = Hash(key);
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var acquired = await context.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_lock({scopeId}, {keyHash}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (acquired)
                return new SessionLock(context, scopeId, keyHash);
        }
        catch
        {
            await context.Database.CloseConnectionAsync();
            throw;
        }

        await context.Database.CloseConnectionAsync();
        return null;
    }

    /// <summary>Stable across processes and releases (unlike <see cref="string.GetHashCode()"/>).</summary>
    internal static int Hash(string key) =>
        BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed class SessionLock(DbContext context, int scopeId, int keyHash) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({scopeId}, {keyHash})");
            }
            finally
            {
                // Back to the pool only after the unlock; if the unlock failed the connection is broken and closing it
                // ends the session, which releases the lock anyway.
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
