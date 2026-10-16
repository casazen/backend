using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
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

    /// <summary>The payments of the supplier paid inside <c>[fromUtc, toUtc)</c>, summed by the database.</summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<SupplierPaymentTotals> PaidBetween(AppDbContext db, Guid supplierOrgId, DateTime fromUtc, DateTime toUtc) =>
        db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.SupplierOrgId == supplierOrgId
                        && p.Status == ServicePaymentStatus.Paid
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
