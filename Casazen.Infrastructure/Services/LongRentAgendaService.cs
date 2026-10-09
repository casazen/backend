using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ILongRentAgendaService"/>
/// <remarks>
/// <para>Every date is a Europe/Rome calendar day and "today" is the Rome day of the <see cref="TimeProvider"/>. The rules are the ones
/// the rest of the area already uses, not copies: the registration deadline is <see cref="RliRegistrationDeadline"/>, the Questura
/// communication is <see cref="QuesturaCommunicationDeadline"/>, an installment is overdue as <see cref="RentInstallmentRules"/> says,
/// the last day of notice is <see cref="LongRentDeadlineRules"/>.</para>
/// <para>What is read: the leases of the scope that have not ended nor been rejected (a few rows of scalars; the deadlines are
/// computed from them, in memory), and the installments still to be collected in the window (one query, the earliest
/// <see cref="LongRentDeadlineRules.MaxRentItems"/>). A registration already made, a Questura communication already declared and
/// an installment already paid are not deadlines any more and are not read. Nothing is written and nothing is logged.</para>
/// <para>IMU is not listed (the data per comune has no deadline) and ISTAT is out of the first release (decision D8).</para>
/// </remarks>
public sealed class LongRentAgendaService(AppDbContext db, TimeProvider? timeProvider = null) : ILongRentAgendaService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // ---------------------------------------------------------------- deadlines

    public async Task<LongRentDeadlines> GetDeadlinesAsync(
        HostScope scope, LongRentDeadlinesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        // Installments alone need no lease at all.
        IReadOnlyList<LeaseRow> leases = query.Type == LongRentDeadlineType.Rent
            ? Array.Empty<LeaseRow>()
            : await LoadLeasesAsync(scope, cancellationToken);
        return await DeadlinesAsync(scope, query, leases, cancellationToken);
    }

    private async Task<LongRentDeadlines> DeadlinesAsync(
        HostScope scope, LongRentDeadlinesQuery query, IReadOnlyList<LeaseRow> leases, CancellationToken cancellationToken)
    {
        var today = _clock.TodayInRomeAsDateOnly();
        var from = query.From;
        var to = query.To;
        bool Wants(LongRentDeadlineType type) => query.Type is null || query.Type == type;

        // A window that contains today also carries what is past and still open (an RLI deadline missed, a Questura
        // communication not made, a rent not paid): the landlord must see it first.
        var carriesOverdue = from <= today;
        bool Listed(DateOnly date, bool canBeOverdue)
        {
            if (date >= from && date <= to)
                return canBeOverdue || date >= today;
            return canBeOverdue && carriesOverdue && date < from;
        }

        var items = new List<LongRentDeadline>();
        LongRentDeadline Of(LongRentDeadlineType type, DateOnly date, LeaseRow lease) => new(
            type,
            date,
            date.DayNumber - today.DayNumber,
            date < today,
            lease.Id,
            lease.PropertyId,
            lease.PropertyName,
            lease.TenantAnonymized ? null : lease.TenantFirstName,
            lease.TenantAnonymized ? null : lease.TenantLastName,
            InstallmentId: null,
            Amount: null);

        var todayStored = _clock.TodayInRome();
        foreach (var lease in leases)
        {
            if (Wants(LongRentDeadlineType.RliRegistration)
                && RliRegistrationDeadline.AwaitsRegistration(lease.Status)
                && RliRegistrationDeadline.Resolve(lease.Status, lease.StipulaDate, lease.StartDate, todayStored) is { } rli
                && RomeCalendar.DateInRome(rli) is var rliDate
                && Listed(rliDate, canBeOverdue: true))
            {
                items.Add(Of(LongRentDeadlineType.RliRegistration, rliDate, lease));
            }

            if (Wants(LongRentDeadlineType.Questura)
                && lease.HasExtraEUTenant
                && lease.QuesturaCommunicationDate is null
                && RomeCalendar.DateInRome(QuesturaCommunicationDeadline.Deadline(lease.PropertyDeliveryDate ?? lease.StartDate)) is var questuraDate
                && Listed(questuraDate, canBeOverdue: true))
            {
                items.Add(Of(LongRentDeadlineType.Questura, questuraDate, lease));
            }

            if (lease.Status != LeaseStatus.Registered)
                continue;

            if (Wants(LongRentDeadlineType.LeaseEnd) && RomeCalendar.DateInRome(lease.EndDate) is var endDate && Listed(endDate, canBeOverdue: false))
                items.Add(Of(LongRentDeadlineType.LeaseEnd, endDate, lease));

            if (Wants(LongRentDeadlineType.Notice)
                && LongRentDeadlineRules.NoticeDate(lease.ContractType, lease.EndDate) is { } noticeDate
                && Listed(noticeDate, canBeOverdue: false))
            {
                items.Add(Of(LongRentDeadlineType.Notice, noticeDate, lease));
            }
        }

        var truncated = false;
        if (Wants(LongRentDeadlineType.Rent))
        {
            var rents = await db.RentLedgerEntries
                .AsNoTracking()
                .WithinScope(scope)
                .Where(e => (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed)
                    && e.DueDate <= to
                    && (e.DueDate >= from || carriesOverdue))
                .OrderBy(e => e.DueDate)
                .ThenBy(e => e.Id)
                .Take(LongRentDeadlineRules.MaxRentItems + 1)
                .Select(e => new
                {
                    e.Id,
                    e.LeaseContractId,
                    e.LeaseContract.PropertyId,
                    PropertyName = e.LeaseContract.Property.Name,
                    TenantFirstName = e.LeaseContract.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.FirstName).FirstOrDefault(),
                    TenantLastName = e.LeaseContract.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.LastName).FirstOrDefault(),
                    TenantAnonymized = e.LeaseContract.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.AnonymizedAt != null).FirstOrDefault(),
                    e.DueDate,
                    e.AmountDue,
                })
                .ToListAsync(cancellationToken);

            truncated = rents.Count > LongRentDeadlineRules.MaxRentItems;
            foreach (var rent in rents.Take(LongRentDeadlineRules.MaxRentItems))
            {
                items.Add(new LongRentDeadline(
                    LongRentDeadlineType.Rent,
                    rent.DueDate,
                    rent.DueDate.DayNumber - today.DayNumber,
                    rent.DueDate < today,
                    rent.LeaseContractId,
                    rent.PropertyId,
                    rent.PropertyName,
                    rent.TenantAnonymized ? null : rent.TenantFirstName,
                    rent.TenantAnonymized ? null : rent.TenantLastName,
                    rent.Id,
                    rent.AmountDue));
            }
        }

        var ordered = items
            .OrderBy(d => d.Date)
            .ThenBy(d => d.Type)
            .ThenBy(d => d.PropertyName, StringComparer.Ordinal)
            .ThenBy(d => d.LeaseId)
            .ThenBy(d => d.InstallmentId)
            .ToList();
        return new LongRentDeadlines(from, to, ordered, truncated);
    }

    // ---------------------------------------------------------------- overview

    public async Task<LongRentOverview> GetOverviewAsync(HostScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var today = _clock.TodayInRomeAsDateOnly();
        var todayStored = _clock.TodayInRome();
        var expiringUntil = todayStored.AddMonths(LeaseListViews.ExpiringWithinMonths);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);

        // The leases counted as the views of the lease list say (LeaseListViews): one grouped statement.
        var groups = await db.LeaseContracts
            .AsNoTracking()
            .WithinScope(scope)
            .GroupBy(l => new { l.Status, Ended = l.EndDate < todayStored, Soon = l.EndDate <= expiringUntil })
            .Select(g => new { g.Key.Status, g.Key.Ended, g.Key.Soon, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var leaseCounters = CountLeases(groups.Select(g => (g.Status, g.Ended, g.Soon, g.Count)));

        var month = RentRegisterAggregates.OfMonth(
            db.RentLedgerEntries.AsNoTracking().WithinScope(scope), firstOfMonth, firstOfMonth.AddMonths(1).AddDays(-1));
        var rents = await RentRegisterAggregates.CountersAsync(month, today, cancellationToken);

        // All the installments past due, whatever their month: the landlord is behind with them until they are paid.
        // One statement, one row per lease that is behind (a handful), added up here.
        var behind = await db.RentLedgerEntries
            .AsNoTracking()
            .WithinScope(scope)
            .Where(RentInstallmentRules.OverdueOn(today))
            .GroupBy(e => e.LeaseContractId)
            .Select(g => new { LeaseId = g.Key, Count = g.Count(), Amount = g.Sum(e => e.AmountDue), Oldest = g.Min(e => e.DueDate) })
            .ToListAsync(cancellationToken);

        var leases = await LoadLeasesAsync(scope, cancellationToken);
        var checklist = new List<LongRentChecklistItem>();
        if (behind.Count > 0)
        {
            var oldest = behind.OrderBy(b => b.Oldest).ThenBy(b => b.LeaseId).First();
            checklist.Add(new LongRentChecklistItem(
                LongRentChecklistKind.RentOverdue, behind.Sum(b => b.Count), oldest.Oldest, oldest.LeaseId, behind.Sum(b => b.Amount), IsOverdue: true));
        }

        var toRegister = leases
            .Where(l => l.Status is LeaseStatus.Signed or LeaseStatus.RegistrationPending or LeaseStatus.SentToProvider)
            .Select(l => (Lease: l, Date: RliRegistrationDeadline.Resolve(l.Status, l.StipulaDate, l.StartDate, todayStored)))
            .OrderBy(x => x.Date is null ? 1 : 0)
            .ThenBy(x => x.Date)
            .ThenBy(x => x.Lease.Id)
            .FirstOrDefault();
        if (leaseCounters.ToRegister > 0)
        {
            var date = toRegister.Date is { } deadline ? RomeCalendar.DateInRome(deadline) : (DateOnly?)null;
            checklist.Add(new LongRentChecklistItem(
                LongRentChecklistKind.RliRegistration, leaseCounters.ToRegister, date, toRegister.Lease?.Id, null, date is { } d && d < today));
        }

        var questura = leases
            .Where(l => l.HasExtraEUTenant && l.QuesturaCommunicationDate is null)
            .Select(l => (Lease: l, Date: RomeCalendar.DateInRome(QuesturaCommunicationDeadline.Deadline(l.PropertyDeliveryDate ?? l.StartDate))))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Lease.Id)
            .ToList();
        if (questura.Count > 0)
        {
            checklist.Add(new LongRentChecklistItem(
                LongRentChecklistKind.Questura, questura.Count, questura[0].Date, questura[0].Lease.Id, null, questura[0].Date < today));
        }

        var toSign = leases
            .Where(l => RliRegistrationDeadline.IsBeforeFullSignature(l.Status))
            .OrderBy(l => l.StartDate)
            .ThenBy(l => l.Id)
            .FirstOrDefault();
        if (leaseCounters.ToSign > 0)
        {
            var date = toSign is null ? (DateOnly?)null : RomeCalendar.DateInRome(toSign.StartDate);
            checklist.Add(new LongRentChecklistItem(
                LongRentChecklistKind.LeaseToSign, leaseCounters.ToSign, date, toSign?.Id, null, date is { } d && d < today));
        }

        var window = new LongRentDeadlinesQuery(today, today.AddDays(LongRentDeadlineRules.DefaultWindowDays));
        var upcoming = await DeadlinesAsync(scope, window, leases, cancellationToken);
        return new LongRentOverview(
            today,
            leaseCounters,
            firstOfMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            rents,
            checklist,
            upcoming.Items.FirstOrDefault(d => !d.IsOverdue));
    }

    /// <summary>The counters of the views of the lease list, from the groups (status, ended, ending soon) of the leases.</summary>
    internal static LongRentLeaseCounters CountLeases(IEnumerable<(LeaseStatus Status, bool Ended, bool Soon, int Count)> groups)
    {
        int active = 0, expiring = 0, toSign = 0, toRegister = 0, ended = 0;
        foreach (var (status, hasEnded, endsSoon, count) in groups)
        {
            switch (status)
            {
                case LeaseStatus.Registered when hasEnded:
                    ended += count;
                    break;
                case LeaseStatus.Registered:
                    active += count;
                    if (endsSoon)
                        expiring += count;
                    break;
                case LeaseStatus.Rejected:
                    ended += count;
                    break;
                case LeaseStatus.Draft or LeaseStatus.AwaitingSignature or LeaseStatus.PartiallySigned:
                    toSign += count;
                    break;
                default:
                    toRegister += count;
                    break;
            }
        }

        return new LongRentLeaseCounters(active, expiring, toSign + toRegister, toSign, toRegister, ended);
    }

    // ---------------------------------------------------------------- leases

    /// <summary>
    /// The leases of the scope that can still carry a deadline: not rejected, not ended (a lease that ended with its registration
    /// or its Questura communication never done is a closed matter, not an agenda item). Scalars only, nothing of the parties but
    /// the first tenant's name and whether a tenant is extra-EU.
    /// </summary>
    private async Task<List<LeaseRow>> LoadLeasesAsync(HostScope scope, CancellationToken cancellationToken)
    {
        var todayStored = _clock.TodayInRome();
        var rows = await db.LeaseContracts
            .AsNoTracking()
            .WithinScope(scope)
            .Where(l => l.Status != LeaseStatus.Rejected && l.EndDate >= todayStored)
            .Select(l => new
            {
                l.Id,
                l.PropertyId,
                PropertyName = l.Property.Name,
                l.Status,
                l.ContractType,
                l.StartDate,
                l.EndDate,
                l.StipulaDate,
                l.PropertyDeliveryDate,
                l.QuesturaCommunicationDate,
                HasExtraEUTenant = l.Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU),
                TenantFirstName = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.FirstName).FirstOrDefault(),
                TenantLastName = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.LastName).FirstOrDefault(),
                TenantAnonymized = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.AnonymizedAt != null).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new LeaseRow(
                r.Id,
                r.PropertyId,
                r.PropertyName,
                r.Status,
                r.ContractType,
                r.StartDate,
                r.EndDate,
                r.StipulaDate,
                r.PropertyDeliveryDate,
                r.QuesturaCommunicationDate,
                r.HasExtraEUTenant,
                r.TenantFirstName,
                r.TenantLastName,
                r.TenantAnonymized))
            .ToList();
    }

    private sealed record LeaseRow(
        Guid Id,
        Guid PropertyId,
        string PropertyName,
        LeaseStatus Status,
        LeaseContractType ContractType,
        DateTime StartDate,
        DateTime EndDate,
        DateTime? StipulaDate,
        DateTime? PropertyDeliveryDate,
        DateTime? QuesturaCommunicationDate,
        bool HasExtraEUTenant,
        string? TenantFirstName,
        string? TenantLastName,
        bool TenantAnonymized);
}
