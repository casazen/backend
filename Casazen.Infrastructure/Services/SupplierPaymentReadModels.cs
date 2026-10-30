using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierPaymentReadModels"/>
/// <remarks>
/// The only place of the console's reads that touches <c>ServiceRequestPayments</c> (SP-04e). The table has two parties and an anonymous
/// payer, so it is not tenant-filtered: every statement here carries <c>SupplierOrgId == supplierOrgId</c> as an explicit predicate, and
/// only the columns the console needs are selected (never the hash of the payment link, the PaymentIntent or the line items).
/// </remarks>
public sealed class SupplierPaymentReadModels(
    AppDbContext db,
    IFeatureFlags features,
    IOptions<SupplierPaymentsOptions> options,
    ILegalDocumentService legalDocuments,
    TimeProvider? timeProvider = null) : ISupplierPaymentReadModels
{
    private static readonly ServicePaymentStatus[] ToCollectStatuses =
    [
        ServicePaymentStatus.Requested,
        ServicePaymentStatus.Processing,
        ServicePaymentStatus.Failed,
        ServicePaymentStatus.NeedsReview,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyDictionary<Guid, SupplierJobPayment>> SummariesAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<SupplierPaymentSummaryInput> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return new Dictionary<Guid, SupplierJobPayment>();

        var requestIds = requests.Select(r => r.RequestId).Distinct().ToArray();

        // At most one payment per request is not canceled (the unique index); a canceled one is history, not a payment to talk about.
        var rows = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId && requestIds.Contains(p.ServiceRequestId) && p.Status != ServicePaymentStatus.Canceled)
            .Select(p => new PaymentRow(
                p.Id,
                p.ServiceRequestId,
                p.Status,
                p.AmountCents,
                p.CommissionPercent,
                p.ApplicationFeeCents,
                p.NetCents,
                p.PaidAt,
                p.PaidVia,
                p.RequestedAt,
                p.LastSentAt,
                p.SentCount,
                p.LateAt))
            .ToListAsync(cancellationToken);
        var live = rows.GroupBy(row => row.ServiceRequestId).ToDictionary(group => group.Key, group => group.First());

        // The facts about the supplier matter to a completed job only: the actions are refused before that whatever they say.
        var supplier = requests.Any(r => r.Status == ServiceRequestStatus.Completato)
            ? await ReadSupplierAsync(supplierOrgId, cancellationToken)
            : new SupplierFacts(MayAct: false, CanReceivePayments: false);
        var onlinePayments = features.IsEnabled(FeatureFlags.SupplierOnlinePayments);
        var minimum = options.Value.MinAmountCents;
        var now = _clock.GetUtcNow().UtcDateTime;

        var summaries = new Dictionary<Guid, SupplierJobPayment>();
        foreach (var request in requests)
        {
            live.TryGetValue(request.RequestId, out var payment);
            if (payment is null && request.Status != ServiceRequestStatus.Completato)
                continue;

            var facts = new ServicePaymentActionFacts(
                request.Status,
                request.PaymentMode,
                request.FinalAmountCents,
                request.FinalAmountNeedsConfirmation,
                payment?.Status,
                payment?.LastSentAt,
                onlinePayments,
                supplier.MayAct,
                supplier.CanReceivePayments,
                request.PayerHasEmail,
                minimum);

            summaries[request.RequestId] = new SupplierJobPayment(
                payment?.Id,
                payment?.Status,
                // The record's amount is what the payer pays; until there is a record it is the total the supplier declared.
                payment?.AmountCents ?? request.FinalAmountCents,
                payment?.CommissionPercent,
                payment?.ApplicationFeeCents,
                payment?.NetCents,
                payment?.PaidAt,
                payment?.PaidVia,
                payment?.RequestedAt,
                payment?.LastSentAt,
                payment?.SentCount ?? 0,
                // Late is flagged once and never cleared: it only means something while the payer still owes.
                Late: payment is { LateAt: not null } && ServicePaymentSchedule.IsOwed(payment.Status),
                request.FinalAmountNeedsConfirmation,
                ServicePaymentActions.CanRequest(facts, now),
                ServicePaymentActions.CanRecordOffline(facts));
        }

        return summaries;
    }

    public Task<int> CountLateAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId
                        && p.LateAt != null
                        && (p.Status == ServicePaymentStatus.Requested || p.Status == ServicePaymentStatus.Failed))
            .CountAsync(cancellationToken);

    public Task<bool> HasPaymentsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId && p.Status != ServicePaymentStatus.Canceled)
            .AnyAsync(cancellationToken);

    public async Task<SupplierPaymentTotals> PaidBetweenAsync(
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var totals = await PaidBetween(db, supplierOrgId, fromUtc, toUtc).SingleOrDefaultAsync(cancellationToken);
        return totals ?? SupplierPaymentTotals.None;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> NetToCollectAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        if (requestIds.Count == 0)
            return new Dictionary<Guid, int>();

        var ids = requestIds.ToArray();
        var rows = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId && ids.Contains(p.ServiceRequestId) && ToCollectStatuses.Contains(p.Status))
            .Select(p => new { p.ServiceRequestId, p.NetCents })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.ServiceRequestId).ToDictionary(group => group.Key, group => group.First().NetCents);
    }

    public async Task<(IReadOnlyList<SupplierEarningRow> Items, int Total)> ListEarningsAsync(
        Guid supplierOrgId,
        SupplierEarningsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = FilterEarnings(EarningsOf(db, supplierOrgId), query);
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(x => x.EventAt)
            .ThenBy(x => x.RequestId)
            .Skip((Math.Max(query.Page, 1) - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return (page.Select(ToEarningRow).ToList(), total);
    }

    public async Task<SupplierToCollect> ToCollectAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        // A job asked for inside CasaZen counts at the net of its payment, any other at the total the supplier declared (the home does the same).
        var totals = await EarningsOf(db, supplierOrgId)
            .Where(OwedState)
            .GroupBy(_ => 1)
            .Select(g => new { Jobs = g.Count(), Cents = g.Sum(x => (long?)(x.PaymentNetCents ?? x.FinalAmountCents)) })
            .SingleOrDefaultAsync(cancellationToken);

        return new SupplierToCollect(totals?.Cents ?? 0, totals?.Jobs ?? 0);
    }

    public async Task<IReadOnlyList<SupplierPaidPayment>> PaidSinceAsync(
        Guid supplierOrgId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default) =>
        await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId
                        && (p.Status == ServicePaymentStatus.Paid || p.Status == ServicePaymentStatus.Released)
                        && p.PaidAt >= sinceUtc)
            .Select(p => new SupplierPaidPayment(p.PaidAt!.Value, p.ApplicationFeeCents, p.NetCents))
            .ToListAsync(cancellationToken);

    // ─── The earnings list: the completed jobs of the supplier and, when there is one, their live payment ───

    // The four sets of states of the tabs. A payment record, when the job has one, says where the money is, and the job only when there is none:
    // the same rule as SupplierEarningStates.Of, written for the database. Comparisons of a nullable status are false for null.
    private static readonly Expression<Func<EarningRow, bool>> OwedState = x =>
        (x.PaymentStatus == null && x.RequestStatus == ServiceRequestStatus.Completato)
        || x.PaymentStatus == ServicePaymentStatus.Requested
        || x.PaymentStatus == ServicePaymentStatus.Processing
        || x.PaymentStatus == ServicePaymentStatus.Failed
        || x.PaymentStatus == ServicePaymentStatus.NeedsReview
        // The money CasaZen holds (SP-15c-a) is still to be collected by the supplier.
        || x.PaymentStatus == ServicePaymentStatus.Authorized
        || x.PaymentStatus == ServicePaymentStatus.Held
        || x.PaymentStatus == ServicePaymentStatus.Disputed
        || x.PaymentStatus == ServicePaymentStatus.TransferFailed;

    private static readonly Expression<Func<EarningRow, bool>> PaidState = x =>
        (x.PaymentStatus == null && x.RequestStatus == ServiceRequestStatus.Pagato)
        || x.PaymentStatus == ServicePaymentStatus.Paid
        || x.PaymentStatus == ServicePaymentStatus.Released
        || x.PaymentStatus == ServicePaymentStatus.PartiallyRefunded
        || x.PaymentStatus == ServicePaymentStatus.Refunded;

    private static readonly Expression<Func<EarningRow, bool>> LateState = x =>
        (x.PaymentStatus == ServicePaymentStatus.Requested || x.PaymentStatus == ServicePaymentStatus.Failed) && x.LateAt != null;

    private static readonly Expression<Func<EarningRow, bool>> ReviewState = x =>
        x.PaymentStatus == ServicePaymentStatus.NeedsReview;

    /// <summary>
    /// The tab, the period and the customer of the console applied to the jobs of one supplier. The period is made of Europe/Rome calendar days,
    /// <c>[00:00 Rome of From, 00:00 Rome of the day after To)</c>, on the date of the row (<see cref="EarningRow.EventAt"/>).
    /// </summary>
    internal static IQueryable<EarningRow> FilterEarnings(IQueryable<EarningRow> rows, SupplierEarningsQuery query)
    {
        rows = query.Tab switch
        {
            SupplierEarningsTab.ToCollect => rows.Where(OwedState),
            SupplierEarningsTab.Paid => rows.Where(PaidState),
            SupplierEarningsTab.Late => rows.Where(LateState),
            SupplierEarningsTab.ToVerify => rows.Where(ReviewState),
            _ => rows,
        };

        if (query.From is { } from)
        {
            var startUtc = RomeCalendar.StartOfDayUtc(from);
            rows = rows.Where(x => x.EventAt >= startUtc);
        }

        if (query.To is { } to)
        {
            var endUtc = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            rows = rows.Where(x => x.EventAt < endUtc);
        }

        if (query.ClientId is { } clientId)
            rows = rows.Where(x => x.ClientId == clientId);

        return rows;
    }

    /// <summary>
    /// The completed and paid jobs of <paramref name="supplierOrgId"/> (and of no other), each with its live payment: the one that is not canceled, at
    /// most one per request (a unique index). Neither table has a tenant filter (two parties), so the supplier is an explicit predicate on both;
    /// <c>IgnoreQueryFilters</c> opens the host org of the request (another tenant), as the reader of the requests does. Only the columns the earnings
    /// need are read (never the payment link, the PaymentIntent or the line items) and the name of a private customer, an encrypted column, only for
    /// the page that is returned.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<EarningRow> EarningsOf(AppDbContext db, Guid supplierOrgId) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato))
            .LeftJoin(
                db.ServiceRequestPayments
                    .AsNoTracking()
                    .Where(p => p.SupplierOrgId == supplierOrgId && p.Status != ServicePaymentStatus.Canceled),
                r => r.Id,
                p => p.ServiceRequestId,
                (r, p) => new EarningRow
                {
                    RequestId = r.Id,
                    RequestStatus = r.Status,
                    PaymentMode = r.PaymentMode,
                    Category = r.Category,
                    ServiceName = r.ServiceNameSnapshot,
                    CompletedAt = r.CompletedAt,
                    RequestPaidAt = r.PaidAt,
                    // The day the money moved for a paid job, else the day the work was done.
                    EventAt = (p == null ? null : p.PaidAt) ?? r.PaidAt ?? r.CompletedAt ?? r.CreatedAt,
                    FinalAmountCents = r.FinalAmountCents,
                    // The customer: the private customer of a showcase request, else the host org (the id the inbox filters by).
                    ClientId = r.CustomerId ?? r.OrgId,
                    ClientIsPrivate = r.CustomerId != null,
                    CustomerFullName = r.Customer != null ? r.Customer.FullName : null,
                    CustomerAnonymizedAt = r.Customer != null ? r.Customer.AnonymizedAt : null,
                    HostDisplayName = r.Org.DisplayName,
                    HostName = r.Org.Name,
                    PaymentStatus = p == null ? null : p.Status,
                    PaymentAmountCents = p == null ? null : p.AmountCents,
                    PaymentCommissionPercent = p == null ? null : p.CommissionPercent,
                    PaymentCommissionCents = p == null ? null : p.ApplicationFeeCents,
                    PaymentNetCents = p == null ? null : p.NetCents,
                    PaymentPaidAt = p == null ? null : p.PaidAt,
                    PaymentPaidVia = p == null ? null : p.PaidVia,
                    PaymentRefundedCents = p == null ? null : p.RefundedCents,
                    LateAt = p == null ? null : p.LateAt,
                });

    private static SupplierEarningRow ToEarningRow(EarningRow x)
    {
        var state = SupplierEarningStates.Of(x.RequestStatus, x.PaymentStatus, x.LateAt);

        // The figures of the payment record; without one, a job paid by hand has no commission at all and a job to be paid inside CasaZen has none
        // computed yet (it is when the payment is created), so its commission and net are empty, never 0.
        int? gross;
        decimal? percent;
        int? commission;
        int? net;
        if (x.PaymentStatus is not null)
        {
            gross = x.PaymentAmountCents;
            percent = x.PaymentCommissionPercent;
            commission = x.PaymentCommissionCents;
            net = x.PaymentNetCents;
        }
        else if (x.PaymentMode == ServiceRequestPaymentMode.Manual)
        {
            gross = x.FinalAmountCents;
            percent = 0m;
            commission = gross is null ? null : 0;
            net = gross;
        }
        else
        {
            gross = x.FinalAmountCents;
            percent = null;
            commission = null;
            net = null;
        }

        // A customer of the showcase is named by the full name once the job is taken (decision D9: these jobs are); one the retention job removed
        // has no label and is told apart. A host is its org.
        var anonymized = x.ClientIsPrivate && x.CustomerAnonymizedAt is not null;
        var label = x.ClientIsPrivate
            ? anonymized ? null : NullIfBlank(SupplierJobDisclosure.CustomerName(x.RequestStatus, x.CustomerFullName))
            : NullIfBlank(x.HostDisplayName) ?? NullIfBlank(x.HostName);

        return new SupplierEarningRow(
            x.RequestId,
            NullIfBlank(x.ServiceName),
            x.Category,
            x.ClientId,
            label,
            anonymized,
            x.CompletedAt,
            state,
            x.PaymentMode,
            gross,
            percent,
            commission,
            net,
            x.PaymentPaidAt ?? x.RequestPaidAt,
            // A paid job with no record was paid outside CasaZen (the host marked it paid).
            x.PaymentStatus is null ? (x.RequestStatus == ServiceRequestStatus.Pagato ? ServicePaymentChannel.Offline : null) : x.PaymentPaidVia,
            x.PaymentRefundedCents ?? 0,
            state == SupplierEarningStates.Late ? x.LateAt : null);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A completed or paid job with its live payment, as read from the database.</summary>
    internal sealed class EarningRow
    {
        public Guid RequestId { get; init; }
        public ServiceRequestStatus RequestStatus { get; init; }
        public ServiceRequestPaymentMode PaymentMode { get; init; }
        public string Category { get; init; } = string.Empty;
        public string? ServiceName { get; init; }
        public DateTime? CompletedAt { get; init; }
        public DateTime? RequestPaidAt { get; init; }
        public DateTime EventAt { get; init; }
        public int? FinalAmountCents { get; init; }
        public Guid ClientId { get; init; }
        public bool ClientIsPrivate { get; init; }
        public string? CustomerFullName { get; init; }
        public DateTime? CustomerAnonymizedAt { get; init; }
        public string? HostDisplayName { get; init; }
        public string? HostName { get; init; }
        public ServicePaymentStatus? PaymentStatus { get; init; }
        public int? PaymentAmountCents { get; init; }
        public decimal? PaymentCommissionPercent { get; init; }
        public int? PaymentCommissionCents { get; init; }
        public int? PaymentNetCents { get; init; }
        public DateTime? PaymentPaidAt { get; init; }
        public ServicePaymentChannel? PaymentPaidVia { get; init; }
        public int? PaymentRefundedCents { get; init; }
        public DateTime? LateAt { get; init; }
    }

    /// <summary>The payments of the supplier paid inside <c>[fromUtc, toUtc)</c>, summed by the database.</summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<SupplierPaymentTotals> PaidBetween(AppDbContext db, Guid supplierOrgId, DateTime fromUtc, DateTime toUtc) =>
        db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId
                        && (p.Status == ServicePaymentStatus.Paid || p.Status == ServicePaymentStatus.Released)
                        && p.PaidAt >= fromUtc
                        && p.PaidAt < toUtc)
            .GroupBy(_ => 1)
            .Select(g => new SupplierPaymentTotals(
                g.Sum(p => (long)p.AmountCents),
                g.Sum(p => (long)p.ApplicationFeeCents),
                g.Sum(p => (long)p.NetCents)));

    /// <summary>The supplier's side of the rules: whether it may act at all and whether it can be paid through CasaZen.</summary>
    private async Task<SupplierFacts> ReadSupplierAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
        var profile = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => new { sp.Status, sp.TosAcceptedAt, sp.TosVersion })
            .FirstOrDefaultAsync(cancellationToken);

        // Org is the tenant itself (no tenant filter, TN-2 allow-list); scoped by the id.
        var org = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == supplierOrgId)
            .Select(o => new { o.StripeConnectedAccountId, o.ConnectChargesEnabled, o.ConnectPayoutsEnabled })
            .FirstOrDefaultAsync(cancellationToken);

        // The same two gates as every action of the supplier (ServiceRequestService.GetRequestForActiveSupplierOrThrow): an active
        // supplier that has not to accept new Terms before it goes on.
        var mayAct = profile is { Status: SupplierStatus.Active }
                     && !SupplierActivationRules
                         .TosState(
                             new SupplierProfile { TosAcceptedAt = profile.TosAcceptedAt, TosVersion = profile.TosVersion },
                             legalDocuments.GetTos().Version)
                         .BlocksActions;
        var canReceivePayments = org is not null
                                 && SupplierVerification.CanReceivePayments(org.StripeConnectedAccountId, org.ConnectChargesEnabled, org.ConnectPayoutsEnabled);
        return new SupplierFacts(mayAct, canReceivePayments);
    }

    private sealed record SupplierFacts(bool MayAct, bool CanReceivePayments);

    private sealed record PaymentRow(
        Guid Id,
        Guid ServiceRequestId,
        ServicePaymentStatus Status,
        int AmountCents,
        decimal CommissionPercent,
        int ApplicationFeeCents,
        int NetCents,
        DateTime? PaidAt,
        ServicePaymentChannel? PaidVia,
        DateTime? RequestedAt,
        DateTime? LastSentAt,
        int SentCount,
        DateTime? LateAt);
}
