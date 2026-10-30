using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierClientsService"/>
/// <remarks>
/// <para><b>One customer = the id the requests carry</b> (<c>CustomerId</c> for a private customer of the showcase, else the <c>OrgId</c> of the host
/// that asked): the same id as <c>clientId</c> of the inbox, so "its requests" is the inbox filtered by it. The customers are made by one grouped read
/// of the supplier's requests (open or taken ones: a refused or cancelled request makes nobody a customer); the encrypted name, e-mail and phone of the
/// private customers of the <b>page</b> are read afterwards by <see cref="IServiceCustomerReader"/>, the e-mail and the phone only of those whose job was
/// taken, and the names of the host orgs by id.</para>
/// <para><b>Tenancy.</b> <c>ServiceRequests</c> has two parties and no tenant filter, so the supplier org is an explicit predicate of the aggregation and
/// of the detail; the private customers are read through the reader that carries the same predicate. A customer of another supplier has no request of
/// this one, so it is not found (404), like one that does not exist.</para>
/// </remarks>
public sealed class SupplierClientsService(
    AppDbContext db,
    IServiceCustomerReader customers,
    TimeProvider? timeProvider = null) : ISupplierClientsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SupplierClientsPage> ListAsync(
        Guid supplierOrgId,
        SupplierClientFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var rows = Filter(AggregatesOf(db, supplierOrgId, now), filter);

        var total = await rows.CountAsync(cancellationToken);
        var aggregates = await rows
            .OrderByDescending(c => c.LastActivityAt)
            .ThenBy(c => c.ClientId)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new SupplierClientsPage(await ToSummariesAsync(supplierOrgId, aggregates, cancellationToken), total);
    }

    public async Task<SupplierClientDetail> GetAsync(Guid supplierOrgId, Guid clientId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var aggregate = await AggregatesOf(db, supplierOrgId, now, clientId).FirstOrDefaultAsync(cancellationToken)
            ?? throw SupplierClientErrors.Missing(clientId);

        var summary = (await ToSummariesAsync(supplierOrgId, [aggregate], cancellationToken)).Single();

        var jobs = await RecentJobsOf(db, supplierOrgId, clientId)
            .Take(SupplierClientLimits.RecentJobs)
            .ToListAsync(cancellationToken);

        return new SupplierClientDetail(
            summary,
            jobs.Select(job => new SupplierClientJob(
                    job.RequestId,
                    NullIfBlank(job.ServiceName),
                    job.Category,
                    job.Status,
                    SupplierRequestPhases.Of(job.Status, hasPendingProposal: job.HasProposal, hasPendingQuote: job.HasQuote),
                    job.ScheduledStart,
                    job.CompletedAt,
                    job.FinalAmountCents ?? job.QuotedAmountCents ?? job.EstimatedAmountCents))
                .ToList());
    }

    /// <summary>The chip of the page applied to the customers.</summary>
    internal static IQueryable<ClientAggregate> Filter(IQueryable<ClientAggregate> rows, SupplierClientFilter filter) => filter switch
    {
        SupplierClientFilter.Host => rows.Where(c => !c.IsPrivate),
        SupplierClientFilter.Private => rows.Where(c => c.IsPrivate),
        SupplierClientFilter.Regular => rows.Where(c => c.JobsAccepted >= SupplierRegulars.MinJobs),
        SupplierClientFilter.New => rows.Where(c => c.JobsAccepted == 0),
        _ => rows,
    };

    /// <summary>
    /// The customers of <paramref name="supplierOrgId"/>, one row each, counted by the database in one pass over the requests that are open or
    /// taken. The date of a job is the time of its work (else when it was completed, taken or received); the last job is the latest of those already
    /// begun, the next the first of those still ahead of <paramref name="nowUtc"/>. The personal data are not read here.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<ClientAggregate> AggregatesOf(AppDbContext db, Guid supplierOrgId, DateTime nowUtc, Guid? onlyClient = null)
    {
        // ServiceRequest has no tenant filter (two parties, TN-2 allow-list): the supplier is an explicit predicate. IgnoreQueryFilters opens the
        // requests of the hosts (another tenant), as the reader of the inbox does.
        var requests = db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && r.Status != ServiceRequestStatus.Rifiutato
                        && r.Status != ServiceRequestStatus.Annullato);

        // One customer: narrowed before the grouping, so the detail does not count every customer of the supplier.
        if (onlyClient is { } clientId)
            requests = requests.Where(r => (r.CustomerId ?? r.OrgId) == clientId);

        return requests
            .GroupBy(r => new { ClientId = r.CustomerId ?? r.OrgId, IsPrivate = r.CustomerId != null })
            .Select(g => new ClientAggregate
            {
                ClientId = g.Key.ClientId,
                IsPrivate = g.Key.IsPrivate,
                JobsAccepted = g.Count(r => r.Status == ServiceRequestStatus.PresoInCarico
                                            || r.Status == ServiceRequestStatus.InCorso
                                            || r.Status == ServiceRequestStatus.Completato
                                            || r.Status == ServiceRequestStatus.Pagato),
                JobsCompleted = g.Count(r => r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato),
                GrossPaidCents = g.Sum(r => r.Status == ServiceRequestStatus.Pagato ? (long?)r.FinalAmountCents : null) ?? 0L,
                LastJobAt = g.Max(r => r.Status == ServiceRequestStatus.InCorso
                                       || r.Status == ServiceRequestStatus.Completato
                                       || r.Status == ServiceRequestStatus.Pagato
                    ? (DateTime?)(r.ScheduledStartUtc ?? r.CompletedAt ?? r.TakenAt ?? r.CreatedAt)
                    : null),
                NextJobAt = g.Min(r => (r.Status == ServiceRequestStatus.Richiesto || r.Status == ServiceRequestStatus.PresoInCarico)
                                       && (r.ScheduledStartUtc ?? r.CreatedAt) >= nowUtc
                    ? (DateTime?)(r.ScheduledStartUtc ?? r.CreatedAt)
                    : null),
                LastActivityAt = g.Max(r => r.ScheduledStartUtc ?? r.CompletedAt ?? r.TakenAt ?? r.CreatedAt),
            });
    }

    /// <summary>The latest requests of one customer of the supplier, open or taken, the latest work first.</summary>
    internal static IQueryable<ClientJobRow> RecentJobsOf(AppDbContext db, Guid supplierOrgId, Guid clientId) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && (r.CustomerId ?? r.OrgId) == clientId
                        && r.Status != ServiceRequestStatus.Rifiutato
                        && r.Status != ServiceRequestStatus.Annullato)
            .OrderByDescending(r => r.ScheduledStartUtc ?? r.CompletedAt ?? r.TakenAt ?? r.CreatedAt)
            .ThenBy(r => r.Id)
            .Select(r => new ClientJobRow
            {
                RequestId = r.Id,
                ServiceName = r.ServiceNameSnapshot,
                Category = r.Category,
                Status = r.Status,
                HasProposal = r.ProposedStartUtc != null,
                HasQuote = r.QuoteSentAt != null,
                ScheduledStart = r.ScheduledStartUtc,
                CompletedAt = r.CompletedAt,
                EstimatedAmountCents = r.EstimatedAmountCents,
                QuotedAmountCents = r.QuotedAmountCents,
                FinalAmountCents = r.FinalAmountCents,
            });

    /// <summary>
    /// The page of aggregates with the names, and the contacts of the customers whose job was taken. The private customers come from the reader of
    /// the customers (supplier predicate, personal columns decrypted only here), the hosts from their org (name and contact address).
    /// </summary>
    private async Task<IReadOnlyList<SupplierClientSummary>> ToSummariesAsync(
        Guid supplierOrgId,
        IReadOnlyList<ClientAggregate> aggregates,
        CancellationToken cancellationToken)
    {
        var privateIds = aggregates.Where(c => c.IsPrivate).Select(c => c.ClientId).ToArray();
        var takenPrivateIds = aggregates.Where(c => c.IsPrivate && c.JobsAccepted > 0).Select(c => c.ClientId).ToArray();
        var cards = (await customers.ListCardsAsync(supplierOrgId, privateIds, takenPrivateIds, cancellationToken))
            .ToDictionary(card => card.Id);

        var hostIds = aggregates.Where(c => !c.IsPrivate).Select(c => c.ClientId).ToArray();
        // Org is the tenant itself (no tenant filter, TN-2 allow-list); read by the ids of the hosts of this page, which have a request of this supplier.
        var hosts = new Dictionary<Guid, HostRow>();
        if (hostIds.Length > 0)
        {
            var rows = await db.Orgs
                .AsNoTracking()
                .Where(o => hostIds.Contains(o.Id))
                .Select(o => new HostRow(o.Id, o.DisplayName, o.Name, o.ContactEmail))
                .ToListAsync(cancellationToken);
            hosts = rows.ToDictionary(row => row.Id);
        }

        return aggregates.Select(aggregate =>
        {
            var taken = aggregate.JobsAccepted > 0;
            string? label;
            var anonymized = false;
            SupplierClientContact? contact = null;
            if (aggregate.IsPrivate)
            {
                var card = cards.GetValueOrDefault(aggregate.ClientId);
                anonymized = card?.Anonymized ?? false;

                // Decision D9: "Nome C." until a job of the customer is taken, then the full name, the e-mail and the phone.
                label = card is null || anonymized
                    ? null
                    : taken ? NullIfBlank(card.FullName) : NullIfBlank(ShowcaseBookingRules.AbbreviateName(card.FullName));
                if (taken && card is { Anonymized: false })
                    contact = new SupplierClientContact(NullIfBlank(card.FullName), NullIfBlank(card.Email), NullIfBlank(card.Phone));
            }
            else
            {
                var host = hosts.GetValueOrDefault(aggregate.ClientId);
                label = host is null ? null : NullIfBlank(host.DisplayName) ?? NullIfBlank(host.Name);
                // The name of a host org is known before the take (decision D9); how to reach it only after.
                if (taken && host is not null)
                    contact = new SupplierClientContact(label, NullIfBlank(host.ContactEmail), null);
            }

            return new SupplierClientSummary(
                aggregate.ClientId,
                aggregate.IsPrivate,
                label,
                anonymized,
                aggregate.JobsAccepted,
                aggregate.JobsCompleted,
                aggregate.LastJobAt,
                aggregate.NextJobAt,
                aggregate.GrossPaidCents,
                SupplierRegulars.IsRegular(aggregate.JobsAccepted),
                contact);
        }).ToList();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The name and the contact address of a host org.</summary>
    private sealed record HostRow(Guid Id, string DisplayName, string Name, string ContactEmail);

    /// <summary>A customer of the supplier as counted by the database.</summary>
    internal sealed class ClientAggregate
    {
        public Guid ClientId { get; init; }
        public bool IsPrivate { get; init; }
        public int JobsAccepted { get; init; }
        public int JobsCompleted { get; init; }
        public long GrossPaidCents { get; init; }
        public DateTime? LastJobAt { get; init; }
        public DateTime? NextJobAt { get; init; }
        public DateTime LastActivityAt { get; init; }
    }

    /// <summary>One request of a customer, as read for the detail.</summary>
    internal sealed class ClientJobRow
    {
        public Guid RequestId { get; init; }
        public string? ServiceName { get; init; }
        public string Category { get; init; } = string.Empty;
        public ServiceRequestStatus Status { get; init; }
        public bool HasProposal { get; init; }
        public bool HasQuote { get; init; }
        public DateTime? ScheduledStart { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int? EstimatedAmountCents { get; init; }
        public int? QuotedAmountCents { get; init; }
        public int? FinalAmountCents { get; init; }
    }
}
