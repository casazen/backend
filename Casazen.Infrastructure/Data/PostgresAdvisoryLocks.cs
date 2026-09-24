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
/// commit or rollback of the transaction that holds it.
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

    /// <summary>Stable across processes and releases (unlike <see cref="string.GetHashCode()"/>).</summary>
    internal static int Hash(string key) =>
        BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
