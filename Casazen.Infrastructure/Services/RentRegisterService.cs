using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IRentRegisterService"/>
/// <remarks>
/// <para>Read only and tenant-safe: every query starts from the ledger of the caller's org and narrows it to the properties of
/// the scope in SQL (<see cref="LongRentScopeQueries"/>). Whether an installment is overdue is
/// <see cref="RentInstallmentRules"/>, the rule the ledger of a lease uses. Logs carry nothing: there is nothing to log in a read.</para>
/// <para>The page is ordered by due date, then by id: the id is unique, so the order is total and a page never repeats or skips a
/// row while the ledger is read page after page. The new index <c>(OrgId, DueDate)</c> serves the month and the order.</para>
/// </remarks>
public sealed class RentRegisterService(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IRentRegisterService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RentRegisterPage> GetRegisterAsync(
        HostScope scope, RentRegisterQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var today = _clock.TodayInRomeAsDateOnly();
        var now = _clock.GetUtcNow().UtcDateTime;
        var intervalHours = RentCharges.GetReminderIntervalHours(configuration);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, RentRegisterQuery.MaxPageSize);

        var month = RentRegisterAggregates.OfMonth(db.RentLedgerEntries.AsNoTracking().WithinScope(scope), query.FirstDay, query.LastDay);
        var counters = await RentRegisterAggregates.CountersAsync(month, today, cancellationToken);

        var filtered = query.Status switch
        {
            RentRegisterStatus.Paid => month.Where(e => e.Status == RentLedgerStatus.Paid),
            RentRegisterStatus.Overdue => month.Where(RentInstallmentRules.OverdueOn(today)),
            RentRegisterStatus.Pending => month.Where(RentInstallmentRules.PendingOn(today)),
            _ => month,
        };

        var total = query.Status switch
        {
            RentRegisterStatus.Paid => counters.Collected.Count,
            RentRegisterStatus.Overdue => counters.Overdue.Count,
            RentRegisterStatus.Pending => counters.Pending.Count,
            _ => counters.Expected.Count,
        };

        var rows = total == 0 || (page - 1) * pageSize >= total
            ? []
            : await filtered
                .OrderBy(e => e.DueDate)
                .ThenBy(e => e.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(e => new
                {
                    e.Id,
                    e.LeaseContractId,
                    e.LeaseContract.PropertyId,
                    PropertyName = e.LeaseContract.Property.Name,
                    TenantFirstName = e.LeaseContract.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.FirstName).FirstOrDefault(),
                    TenantLastName = e.LeaseContract.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.LastName).FirstOrDefault(),
                    TenantAnonymized = e.LeaseContract.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.AnonymizedAt != null).FirstOrDefault(),
                    HasTenantEmail = e.LeaseContract.Parties.Any(p =>
                        p.Role == PartyRole.Tenant && p.AnonymizedAt == null && p.ContactEmail != ""),
                    e.PeriodStart,
                    e.PeriodEnd,
                    e.DueDate,
                    e.AmountDue,
                    e.Status,
                    e.PaidVia,
                    e.PaidOn,
                    e.PaymentRequestedAt,
                    e.LastReminderAt,
                    e.ReminderCount,
                })
                .ToListAsync(cancellationToken);

        var org = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == scope.OrgId)
            .Select(o => new { o.StripeConnectedAccountId, o.ConnectChargesEnabled })
            .FirstOrDefaultAsync(cancellationToken);
        var onlinePayments = org is { ConnectChargesEnabled: true } && !string.IsNullOrWhiteSpace(org.StripeConnectedAccountId);

        var reminderAllowedFrom = now.AddHours(-intervalHours);
        var items = rows
            .Select(r => new RentRegisterRow(
                r.Id,
                r.LeaseContractId,
                r.PropertyId,
                r.PropertyName,
                r.TenantAnonymized ? null : r.TenantFirstName,
                r.TenantAnonymized ? null : r.TenantLastName,
                r.TenantAnonymized,
                r.PeriodStart,
                r.PeriodEnd,
                r.DueDate,
                r.AmountDue,
                RentCharges.Currency.ToUpperInvariant(),
                r.Status,
                RentInstallmentRules.IsOverdue(r.Status, r.DueDate, today),
                RentInstallmentRules.DaysOverdue(r.Status, r.DueDate, today),
                r.PaidVia,
                r.PaidOn,
                r.PaymentRequestedAt,
                r.LastReminderAt,
                r.ReminderCount,
                RentInstallmentRules.IsRemindable(r.Status)
                    && r.HasTenantEmail
                    && (r.LastReminderAt is null || r.LastReminderAt <= reminderAllowedFrom)))
            .ToList();

        return new RentRegisterPage(
            query.FirstDay.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            counters,
            items,
            total,
            page,
            pageSize,
            onlinePayments,
            intervalHours);
    }

    public async Task<IReadOnlyList<RentInstallmentRef>> FindInstallmentsAsync(
        IReadOnlyCollection<Guid> installmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installmentIds);
        var wanted = installmentIds.Distinct().ToList();
        if (wanted.Count == 0)
            return [];

        // The tenant filter keeps other orgs' installments out: an id that is not the caller's is simply not here.
        return await db.RentLedgerEntries
            .AsNoTracking()
            .Where(e => wanted.Contains(e.Id))
            .Select(e => new RentInstallmentRef(e.Id, e.LeaseContractId, e.LeaseContract.PropertyId))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
/// The numbers of a month of the ledger, shared by the rent register and the overview of the area so that both always say the
/// same: every installment due in the month and not cancelled falls in exactly one of <c>Collected</c>, <c>Overdue</c> and
/// <c>Pending</c> (<see cref="RentInstallmentRules"/>).
/// </summary>
internal static class RentRegisterAggregates
{
    private const int CollectedBucket = 0;
    private const int PendingBucket = 1;
    private const int OverdueBucket = 2;

    /// <summary>The installments of the month (by due date) that are still part of the rent: not cancelled.</summary>
    public static IQueryable<RentLedgerEntry> OfMonth(IQueryable<RentLedgerEntry> entries, DateOnly first, DateOnly last) =>
        entries.Where(e => e.DueDate >= first && e.DueDate <= last && e.Status != RentLedgerStatus.Cancelled);

    /// <summary>One statement: the three buckets of the month with their count and amount.</summary>
    public static async Task<RentRegisterCounters> CountersAsync(
        IQueryable<RentLedgerEntry> month, DateOnly today, CancellationToken cancellationToken)
    {
        var buckets = await month
            .GroupBy(e => e.Status == RentLedgerStatus.Paid
                ? CollectedBucket
                : (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate < today
                    ? OverdueBucket
                    : PendingBucket)
            .Select(g => new { Bucket = g.Key, Count = g.Count(), Amount = g.Sum(e => e.AmountDue) })
            .ToListAsync(cancellationToken);

        RentCounter Of(int bucket)
        {
            var found = buckets.FirstOrDefault(b => b.Bucket == bucket);
            return new RentCounter(found?.Count ?? 0, found?.Amount ?? 0m);
        }

        var collected = Of(CollectedBucket);
        var pending = Of(PendingBucket);
        var overdue = Of(OverdueBucket);
        return new RentRegisterCounters(
            new RentCounter(collected.Count + pending.Count + overdue.Count, collected.Amount + pending.Amount + overdue.Amount),
            collected,
            pending,
            overdue);
    }
}
