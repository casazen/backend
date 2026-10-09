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
        /// iCal import feeds of one property (key: property id): two sync runs of a feed (the 15-minute job and a first
        /// sync or "sync now") never write the same blocks at once (PC-10, A2-12); adding a feed (count and duplicate
        /// check) and removing one never interleave with them (PC-11). Also taken to create the export link, and to turn a
        /// block into an OTA stay or change that stay after a sync (CO-21): a sync never removes the block meanwhile.
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

        /// <summary>
        /// Seasonal price suggestions of one property (key: property id): the nightly job, the manual recalculation and a
        /// configuration save never upsert the same dates at once (PC-15).
        /// </summary>
        SeasonalPriceSuggestions = 1_019,

        /// <summary>
        /// Compliance status of one property (key: property id): a host request and the nightly check suspend it once
        /// and email the host once (CO-06, A5-20).
        /// </summary>
        PropertyComplianceStatus = 1_030,

        /// <summary>
        /// One run of the compliance check of every active property (single key, session lock held for the whole run):
        /// the nightly job and the one-shot command never run together (CO-06, A5-36).
        /// </summary>
        PropertyComplianceCheckRun = 1_031,

        /// <summary>
        /// STR fiscal regimes and taxpayers of one org (key: org id): the one 21% cedolare unit per taxpayer and tax year is
        /// checked and written one request at a time (CO-18, A5-22).
        /// </summary>
        OrgFiscalRegime = 1_023,

        /// <summary>
        /// One run of the daily CIN alert (single key, session lock held for the whole run): two runs never alert the same
        /// hosts at once, even outside Hangfire's own lock (CO-20, A5-31).
        /// </summary>
        CinDeadlineAlertsRun = 1_042,

        /// <summary>
        /// One run of the supplier repair <c>fix-orphaned</c> (single key): two admin runs never merge the same duplicate
        /// profiles at once (SU-14, A4-22). The run also takes <see cref="SupplierClaim"/> for every profile it merges.
        /// </summary>
        SupplierMaintenance = 1_054,

        /// <summary>
        /// Availability days of one supplier (key: supplier org id): the iCal sync (15-minute job, first sync, "sync
        /// now") and the supplier's manual changes never write the same days at once (SU-15).
        /// </summary>
        SupplierCalendarSync = 1_065,

        /// <summary>
        /// Import of the official ISTAT comuni list (single key): the upload of an admin and the seed file of a starting
        /// instance never write the table together, and the diff of an import is computed on rows nobody else changes (SU-04).
        /// </summary>
        ComuneImport = 1_087,

        /// <summary>
        /// Rent of one lease (key: lease id): the schedule set-up, the tenant's payment session, an offline payment, the
        /// payment webhooks and the collection job change its installments one at a time, so an installment never gets
        /// two payable PaymentIntents or an offline payment while it is paid online (LT-06).
        /// </summary>
        RentLease = 1_206,

        /// <summary>
        /// Photo gallery of one property (key: property id): two uploads, deletions or reorders never read and rewrite the
        /// photo list at the same time, so no photo is lost (PC-04).
        /// </summary>
        PropertyPhotos = 1_074,

        /// <summary>
        /// Service catalog of one supplier (key: supplier org id, <c>orgId.ToString("N")</c>): the changes of a supplier's
        /// services (create, duplicate, edit, delete, publish, pause, photos) and the merge of duplicate supplier profiles
        /// (<c>fix-orphaned</c>) run one at a time, so the limit of services, the free slug and the photo list are never
        /// decided on a stale read (SP-02).
        /// </summary>
        SupplierServiceCatalog = 1_302,

        /// <summary>
        /// One run of the automatic cancellation of the service requests nobody answered (single key, session lock held for the
        /// whole run): two runs never cancel and notify the same requests at once, even outside Hangfire's own lock (SP-04, D8).
        /// </summary>
        ServiceRequestAutoCancelRun = 1_310,

        /// <summary>
        /// One run of the upkeep of the bookings from the public showcases (single key, session lock held for the whole run): the
        /// holds past their expiry are deleted and the showcase requests nobody answered are cancelled and told once, even
        /// outside Hangfire's own lock (SP-10).
        /// </summary>
        ServiceRequestExpiryRun = 1_311,

        /// <summary>
        /// One run of the reminders of the day before to the customers of the public showcases (single key, session lock held for
        /// the whole run): two runs never send the same reminder at once, even outside Hangfire's own lock (SP-10).
        /// </summary>
        ServiceRequestRemindersRun = 1_312,

        /// <summary>
        /// One run of the retention of the data of the private customers of the suppliers (single key, session lock held for the
        /// whole run): two nights' runs never anonymize the same rows at once, even outside Hangfire's own lock (SP-10).
        /// </summary>
        ServiceCustomerRetentionRun = 1_313,

        /// <summary>
        /// Payment of one service request (key: the request id, <c>requestId.ToString("N")</c>): the payment session of the payer
        /// (anonymous with the link, or the signed-in host), the supplier's payment request and reminder, the offline record, and
        /// (SP-15b) the Stripe webhook and the refunds change the payment one at a time, so a request never gets two payable
        /// PaymentIntents, a link is never sent while another is being issued, and an offline record never races a payment in
        /// progress (SP-15a). The values 1_320 to 1_329 are the payments of the service requests.
        /// </summary>
        ServiceRequestPayment = 1_320,

        /// <summary>
        /// The people of one org (key: org id): adding, changing, deactivating or removing a member, and the owner's
        /// creation, run one at a time, so the owner rule is decided on rows nobody else is changing (AM-01). The seat
        /// count is decided under <see cref="OrgSeats"/>, which the callers take <b>before</b> this one (AM-02).
        /// </summary>
        OrgMembership = 1_401,

        /// <summary>
        /// One run of the org membership reconcile (single key): two admin runs never create the same owners or fix the
        /// same memberships at once (AM-01). The run does not take the <see cref="OrgMembership"/> lock of every org it
        /// touches: a member write that races with it is arbitrated by the unique indexes, and the run then answers 409
        /// and saves nothing (it is idempotent, so it is simply run again).
        /// </summary>
        OrgMembershipMaintenance = 1_402,

        /// <summary>
        /// The seats of one org (key: org id, AM-02, decisions D13 and D35): the count of active members plus pending
        /// invitations and what depends on it run one at a time, like <c>CreatePropertyWithinLimitAsync</c> does for the
        /// properties. Taken to create an invitation, to send it again, to accept it, to reactivate a member and by the
        /// reminders and expiries of the maintenance job, so the last seat is given to one request only. Always taken
        /// <b>before</b> <see cref="OrgMembership"/> (and before the org's property slot when a person leaves an empty org),
        /// and for two orgs in the order of their ids: no cycle between two requests.
        /// </summary>
        OrgSeats = 1_403,

        /// <summary>
        /// One run of the org invitation maintenance (single key, session lock held for the whole run): reminders on the
        /// third day, expiry and deletion of the closed ones never run twice at once, even outside Hangfire's own lock
        /// (AM-02).
        /// </summary>
        OrgInvitationMaintenance = 1_404,

        /// <summary>
        /// One run of the hourly application of the scheduled changes of rental mode (single key, session lock held for the
        /// whole run): two runs never apply or fail the same change at once, even outside Hangfire's own lock (PM-02). Each
        /// change is then applied under the dates lock of its property, so it also serializes with the bookings.
        /// </summary>
        PropertyModeChangeRun = 1_501,
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
