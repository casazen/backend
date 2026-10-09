using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceCustomerPrivacyService"/>
/// <remarks>
/// <para><b>A system job.</b> The nightly retention works across every supplier on purpose (as the retention of the guests and of
/// the lease parties): it is run only by Hangfire (<c>gdpr-data-retention</c>) and reachable from no request. Its statements on
/// <c>ServiceCustomers</c> are <see cref="CandidateCustomersOf"/> and the changes of the rows it loads by id;
/// <c>ShowcaseBookingTenancyTests</c> lists the file as a system job.</para>
/// <para><b>The period</b> comes from <c>Gdpr:Retention:SupplierCustomers</c> with the rules of <see cref="RetentionPeriodOptions"/>:
/// without an amount and a cited source nothing is anonymized and every run says so. Counted from the last thing that happened:
/// the end of the work of a request (its creation when it has no hours), and for a customer the latest of its requests (its
/// creation when it has none). A request still open (<c>Richiesto</c>, <c>PresoInCarico</c>, <c>InCorso</c>) keeps its customer.</para>
/// <para><b>Marked by what is gone.</b> A customer is anonymized once (<c>AnonymizedAt</c>); a request has its place removed once
/// (the three place columns are empty: the query looks for the ones that are not). A second run finds nothing.</para>
/// <para><b>Against a booking at the same moment.</b> The list of the due customers is read without a lock; the customers of one
/// supplier are then changed under that supplier's calendar lock (the lock a booking holds when its e-mail check creates the
/// request) and read again after taking it, leaving alone any customer that has an open request by then.</para>
/// </remarks>
public sealed class ServiceCustomerPrivacyService(
    AppDbContext db,
    IOptions<GdprOptions> gdprOptions,
    ILogger<ServiceCustomerPrivacyService> logger,
    TimeProvider? timeProvider = null) : IServiceCustomerPrivacyService
{
    internal const string RunLockKey = "service-customer-retention";

    private const int ChunkSize = 100;

    /// <summary>The placeholder of the name of an anonymized customer (the same as the guests, CO-15).</summary>
    public const string AnonymizedValue = "ANONYMIZED";

    /// <summary>The anonymized e-mail address of a customer: unique per row, never deliverable.</summary>
    public static string AnonymizedEmail(Guid customerId) => $"ANON-{customerId:N}@deleted.local";

    /// <summary>The e-mail index of an anonymized customer: matches no address, so the row is never found again by one.</summary>
    public static string AnonymizedEmailHash(Guid customerId) => $"anon-{customerId:N}";

    private static readonly ServiceRequestStatus[] OpenStatuses =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceCustomerRetentionRun> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        var period = gdprOptions.Value.Retention.SupplierCustomers;
        if (!period.IsConfigured)
        {
            logger.LogWarning(
                "GDPR retention: supplier customers not applied ({Problem}); nothing is anonymized until " +
                "Gdpr:Retention:SupplierCustomers has a period and its source (docs/runbooks/gdpr.md)",
                period.ConfigurationProblem);
            return new ServiceCustomerRetentionRun(false, 0, 0);
        }

        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceCustomerRetentionRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("GDPR retention: supplier customers skipped, another run is in progress");
            return new ServiceCustomerRetentionRun(true, 0, 0);
        }

        var today = _clock.TodayInRome();
        var requests = await AnonymizeRequestPlacesAsync(period, today, cancellationToken);
        var customers = await AnonymizeCustomersAsync(period, today, cancellationToken);

        logger.LogInformation(
            "GDPR retention: supplier customers applied: {Customers} customers and the place of {Requests} requests anonymized",
            customers, requests);
        return new ServiceCustomerRetentionRun(true, customers, requests);
    }

    /// <summary>
    /// The place of the showcase requests that are over and whose period ended: street address, floor and access notes removed.
    /// The comune and the postal code stay (the supplier's own accounts and statistics, nothing that identifies a person).
    /// </summary>
    private async Task<int> AnonymizeRequestPlacesAsync(RetentionPeriodOptions period, DateTime today, CancellationToken cancellationToken)
    {
        var candidateBefore = period.CandidateStartBefore(today);
        var candidates = await CandidateRequestsOf(db, candidateBefore).ToListAsync(cancellationToken);
        var dueIds = candidates
            .Where(c => period.HasEnded(c.ReferenceUtc, today))
            .Select(c => c.Id)
            .Take(ShowcaseBookingLimits.RetentionBatchSize)
            .ToList();

        var anonymized = 0;
        foreach (var chunk in dueIds.Chunk(ChunkSize))
        {
            db.ChangeTracker.Clear();
            var now = UtcNow();
            var loaded = await db.ServiceRequests
                .IgnoreQueryFilters()
                .Where(r => chunk.Contains(r.Id) && r.RentalContext == ServiceRequestRentalContext.Showcase)
                .ToListAsync(cancellationToken);
            foreach (var request in loaded)
            {
                request.LocationAddress = null;
                request.LocationFloor = null;
                request.LocationAccessNotes = null;
                request.UpdatedAt = now;
                anonymized++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return anonymized;
    }

    /// <summary>
    /// The customers none of whose requests is open and whose last request (or creation) is older than the period. The customers of
    /// a supplier are changed under that supplier's calendar lock — the one a booking takes to create its request — and read again
    /// after taking it: a customer who checks the e-mail of a new booking between the list and the change has an open request by
    /// then and is left alone (the next night's run looks at it again).
    /// </summary>
    private async Task<int> AnonymizeCustomersAsync(RetentionPeriodOptions period, DateTime today, CancellationToken cancellationToken)
    {
        var candidates = await CandidateCustomersOf(db, period.CandidateStartBefore(today)).ToListAsync(cancellationToken);
        var due = candidates
            .Where(c => !c.HasOpenRequest && period.HasEnded(c.ReferenceUtc, today))
            .OrderBy(c => c.ReferenceUtc)
            .Take(ShowcaseBookingLimits.RetentionBatchSize)
            .ToList();

        var anonymized = 0;
        foreach (var ofSupplier in due.GroupBy(c => c.OrgId))
        {
            foreach (var chunk in ofSupplier.Select(c => c.Id).Chunk(ChunkSize))
            {
                db.ChangeTracker.Clear();
                await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                    db, cancellationToken, CalendarSyncService.AvailabilityLock(ofSupplier.Key));
                var now = UtcNow();
                var loaded = await StillDue(db, chunk).ToListAsync(cancellationToken);
                foreach (var customer in loaded)
                {
                    Anonymize(customer, now);
                    anonymized++;
                }

                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
            }
        }

        return anonymized;
    }

    /// <summary>
    /// The customers among <paramref name="ids"/> that are still to be anonymized: not anonymized yet and with no open request. Read
    /// again under the supplier's calendar lock, right before the change, so a booking checked since the list was made keeps its
    /// customer.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<ServiceCustomer> StillDue(AppDbContext db, Guid[] ids) =>
        db.ServiceCustomers.Where(c => ids.Contains(c.Id)
                                       && c.AnonymizedAt == null
                                       && !db.ServiceRequests.IgnoreQueryFilters().Any(r => r.CustomerId == c.Id && OpenStatuses.Contains(r.Status)));

    /// <summary>Replaces every personal field of <paramref name="customer"/>; the language and the consent version and date stay.</summary>
    public static void Anonymize(ServiceCustomer customer, DateTime now)
    {
        customer.FullName = AnonymizedValue;
        customer.Email = AnonymizedEmail(customer.Id);
        customer.Phone = null;
        customer.ConsentIp = string.Empty;
        customer.EmailHash = AnonymizedEmailHash(customer.Id);
        customer.AnonymizedAt ??= now;
        customer.UpdatedAt = now;
    }

    /// <summary>
    /// The showcase requests that are over (final status) and still carry a place, with the date the period counts from: the end of
    /// the work, else the creation. Coarsely cut in SQL at <paramref name="candidateBefore"/> (see
    /// <see cref="RetentionPeriodOptions.CandidateStartBefore"/>); the exact check is made on the row.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<RequestReference> CandidateRequestsOf(AppDbContext db, DateTime candidateBefore) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase
                        && !OpenStatuses.Contains(r.Status)
                        && (r.LocationAddress != null || r.LocationFloor != null || r.LocationAccessNotes != null)
                        && (r.ScheduledEndUtc ?? r.CreatedAt) < candidateBefore)
            .Select(r => new RequestReference(r.Id, r.ScheduledEndUtc ?? r.CreatedAt));

    /// <summary>
    /// The customers not yet anonymized, with their supplier, whether one of their requests is open and the date the period counts
    /// from: the end of their latest request (its creation when it has no hours), else their own creation. Coarsely cut in SQL at
    /// <paramref name="candidateBefore"/> like <see cref="CandidateRequestsOf"/>, so a night reads the customers whose period may
    /// have ended and not every customer there is; the exact check is made on the row.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<CustomerReference> CandidateCustomersOf(AppDbContext db, DateTime candidateBefore) =>
        db.ServiceCustomers
            .AsNoTracking()
            .Where(c => c.AnonymizedAt == null
                        && (db.ServiceRequests.IgnoreQueryFilters()
                                .Where(r => r.CustomerId == c.Id)
                                .Max(r => (DateTime?)(r.ScheduledEndUtc ?? r.CreatedAt)) ?? c.CreatedAt) < candidateBefore)
            .Select(c => new CustomerReference(
                c.Id,
                c.OrgId,
                db.ServiceRequests.IgnoreQueryFilters().Any(r => r.CustomerId == c.Id && OpenStatuses.Contains(r.Status)),
                db.ServiceRequests.IgnoreQueryFilters()
                    .Where(r => r.CustomerId == c.Id)
                    .Max(r => (DateTime?)(r.ScheduledEndUtc ?? r.CreatedAt)) ?? c.CreatedAt));

    /// <summary>A request and the date its retention period counts from.</summary>
    internal sealed record RequestReference(Guid Id, DateTime ReferenceUtc);

    /// <summary>A customer, its supplier, whether it has an open request, and the date its retention period counts from.</summary>
    internal sealed record CustomerReference(Guid Id, Guid OrgId, bool HasOpenRequest, DateTime ReferenceUtc);

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
