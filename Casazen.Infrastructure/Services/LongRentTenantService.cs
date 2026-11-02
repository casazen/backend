using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ILongRentTenantService"/>
/// <remarks>
/// <para>Read side first: every query starts from the leases of the caller's org narrowed to the properties of the scope in SQL
/// (<see cref="LongRentScopeQueries"/>), so a person who has no lease in the caller's reach is never loaded. The people are then grouped in
/// memory by <see cref="LongRentTenantRules.PersonKey"/>: the tenants of an org are a few hundred rows at most, and the number of statements
/// is the same whatever the number of people (the parties, the lease list of the area, and the ledger of the page).</para>
/// <para>Nothing personal is logged: the logs carry counts. The erasure goes through <see cref="ILeasePartyPrivacyService"/>, lease by
/// lease, so its rules (a lease that has not ended is never touched before its end, a person also on another running lease is kept until
/// that one ends, the signed contract and the receipts are never removed) are those of the lease button and are written nowhere else.</para>
/// </remarks>
public sealed class LongRentTenantService(
    AppDbContext db,
    ILeaseContractRepository leaseRepository,
    ILeasePartyPrivacyService partyPrivacy,
    IConfiguration configuration,
    ILogger<LongRentTenantService> logger,
    TimeProvider? timeProvider = null) : ILongRentTenantService
{
    /// <summary>The installments the sheet lists.</summary>
    private const int PaymentsShown = 6;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>A tenant party of a lease of the scope, with the two dates that order the leases of a person.</summary>
    private sealed record PartyRow(
        Guid PartyId,
        Guid LeaseId,
        string FiscalCode,
        string FirstName,
        string LastName,
        string ContactEmail,
        bool IsExtraEU,
        DateTime LeaseCreatedAt,
        DateTime LeaseStartDate);

    /// <summary>
    /// A person: their party rows (one per lease). <c>Id</c> is the party row of the oldest lease, so it stays the same when the person
    /// gets another lease; <c>Latest</c> is the row of the lease they started last, which names them on the page.
    /// </summary>
    private sealed record Person(Guid Id, IReadOnlyList<PartyRow> Rows, PartyRow Latest)
    {
        public List<Guid> LeaseIds => Rows.Select(r => r.LeaseId).Distinct().ToList();
    }

    /// <summary>A person with the leases of the scope they are on (newest first) and whether all of them concluded.</summary>
    private sealed record PersonView(Person Person, IReadOnlyList<TenantLease> Leases, bool IsFormer);

    private sealed record LedgerRow(Guid Id, Guid LeaseId, DateOnly DueDate, decimal Amount, RentLedgerStatus Status, DateOnly? PaidOn);

    public async Task<TenantPage> GetTenantsAsync(HostScope scope, TenantListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, TenantListQuery.MaxPageSize);
        var todayDate = _clock.TodayInRome();
        var today = DateOnly.FromDateTime(todayDate);

        var people = GroupPeople(await LoadPartyRowsAsync(scope, null, cancellationToken));
        var summaries = await LoadSummariesAsync(scope, todayDate);
        var views = people.Select(person => ViewOf(person, summaries)).ToList();

        // The search narrows every count; the view only narrows the list.
        if (query.NormalizedSearch is { } term)
            views = views.Where(view => Matches(view, term)).ToList();

        var counts = new TenantCounts(views.Count, views.Count(v => !v.IsFormer), views.Count(v => v.IsFormer));
        var selected = query.View switch
        {
            TenantListView.Current => views.Where(v => !v.IsFormer).ToList(),
            TenantListView.Former => views.Where(v => v.IsFormer).ToList(),
            _ => views,
        };

        var ordered = selected
            .OrderBy(v => v.Person.Latest.LastName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(v => v.Person.Latest.FirstName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(v => v.Person.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // The ledger of the people on this page only.
        var onTimeDays = OnTimeDays();
        var ledger = await LoadLedgerAsync(scope, ordered.SelectMany(v => v.Person.LeaseIds).Distinct().ToList(), today, cancellationToken);
        var items = ordered.Select(view => RowOf(view, ledger, onTimeDays)).ToList();

        return new TenantPage(items, selected.Count, page, pageSize, counts);
    }

    public async Task<TenantDetail?> GetTenantAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var person = await ResolveAsync(scope, tenantId, cancellationToken);
        if (person is null)
            return null;

        var todayDate = _clock.TodayInRome();
        var today = DateOnly.FromDateTime(todayDate);
        var summaries = await LoadSummariesAsync(scope, todayDate);
        var view = ViewOf(person, summaries);
        var onTimeDays = OnTimeDays();
        var ledger = await LoadLedgerAsync(scope, person.LeaseIds, today, cancellationToken);

        var propertyNames = view.Leases.ToDictionary(l => l.LeaseId, l => l.PropertyName);
        var payments = ledger
            .OrderByDescending(e => e.DueDate)
            .ThenBy(e => e.Id)
            .Take(PaymentsShown)
            .Select(e => new TenantPayment(
                e.Id,
                e.LeaseId,
                propertyNames.GetValueOrDefault(e.LeaseId, string.Empty),
                e.DueDate,
                e.Amount,
                e.Status,
                e.PaidOn,
                RentInstallmentRules.IsOverdue(e.Status, e.DueDate, today),
                RentInstallmentRules.DaysOverdue(e.Status, e.DueDate, today)))
            .ToList();

        var overdue = ledger.Where(e => RentInstallmentRules.IsOverdue(e.Status, e.DueDate, today)).ToList();
        TenantOverdue? lateRent = null;
        if (overdue.Count > 0)
        {
            var oldest = overdue.OrderBy(e => e.DueDate).ThenBy(e => e.Id).First();
            lateRent = new TenantOverdue(overdue.Count, overdue.Sum(e => e.Amount), oldest.Id, oldest.DueDate, today.DayNumber - oldest.DueDate.DayNumber);
        }

        return new TenantDetail(RowOf(view, ledger, onTimeDays), payments, lateRent, onTimeDays);
    }

    public async Task<IReadOnlyList<TenantLeaseRef>?> FindLeasesAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var person = await ResolveAsync(scope, tenantId, cancellationToken);
        if (person is null)
            return null;

        var leaseIds = person.LeaseIds;
        var propertyByLease = await db.LeaseContracts
            .AsNoTracking()
            .WithinScope(scope)
            .Where(l => leaseIds.Contains(l.Id))
            .Select(l => new { l.Id, l.PropertyId })
            .ToListAsync(cancellationToken);
        return propertyByLease.Select(l => new TenantLeaseRef(l.Id, l.PropertyId)).ToList();
    }

    public async Task<TenantDataExport?> ExportAsync(
        HostScope scope, Guid tenantId, IReadOnlySet<Guid> allowedLeaseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(allowedLeaseIds);

        var person = await ResolveAsync(scope, tenantId, cancellationToken);
        if (person is null)
            return null;

        // The person's own party row of each lease, and nothing of the other parties of it.
        var rows = person.Rows.Where(r => allowedLeaseIds.Contains(r.LeaseId)).ToList();
        var leaseIds = rows.Select(r => r.LeaseId).Distinct().ToList();
        var partyIds = rows.Select(r => r.PartyId).ToList();

        var parties = (await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id);
        var leases = await db.LeaseContracts
            .AsNoTracking()
            .WithinScope(scope)
            .Where(l => leaseIds.Contains(l.Id))
            .OrderBy(l => l.StartDate)
            .ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                PropertyName = l.Property.Name,
                PropertyAddress = l.Property.Address,
                PropertyCity = l.Property.City,
                l.Status,
                l.ContractType,
                l.StartDate,
                l.EndDate,
                l.CurrentTermEndDate,
                l.MonthlyRent,
                l.SecurityDeposit,
                l.NoticeOn,
                l.TerminatedOn,
                l.ErasureRequested,
            })
            .ToListAsync(cancellationToken);
        var rents = (await db.RentLedgerEntries
                .AsNoTracking()
                .WithinScope(scope)
                .Where(e => leaseIds.Contains(e.LeaseContractId))
                .OrderBy(e => e.DueDate)
                .ThenBy(e => e.Id)
                .Select(e => new { e.LeaseContractId, e.DueDate, e.PeriodStart, e.PeriodEnd, e.AmountDue, e.Status, e.PaidOn, e.PaidVia })
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.LeaseContractId);
        // The type and the day of each event, never its payload.
        var events = (await db.LeaseEvents
                .AsNoTracking()
                .Where(e => leaseIds.Contains(e.LeaseContractId))
                .OrderBy(e => e.OccurredAt)
                .Select(e => new { e.LeaseContractId, e.EventType, e.OccurredAt })
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.LeaseContractId);

        var items = new List<TenantExportLease>();
        foreach (var lease in leases)
        {
            var row = rows.First(r => r.LeaseId == lease.Id);
            if (!parties.TryGetValue(row.PartyId, out var party))
                continue;

            items.Add(new TenantExportLease(
                lease.Id,
                lease.PropertyName,
                lease.PropertyAddress,
                lease.PropertyCity,
                lease.Status,
                lease.ContractType,
                lease.StartDate,
                lease.EndDate,
                lease.CurrentTermEndDate,
                lease.MonthlyRent,
                lease.SecurityDeposit,
                lease.NoticeOn,
                lease.TerminatedOn,
                lease.ErasureRequested,
                new TenantExportParty(
                    party.FirstName,
                    party.LastName,
                    party.FiscalCode,
                    party.Citizenship,
                    party.ContactEmail,
                    party.IsExtraEU,
                    party.Residence,
                    party.DocumentsVerifiedOn),
                rents[lease.Id].Select(e => new TenantExportRent(e.DueDate, e.PeriodStart, e.PeriodEnd, e.AmountDue, e.Status, e.PaidOn, e.PaidVia)).ToList(),
                events[lease.Id].Select(e => new TenantExportEvent(e.EventType, e.OccurredAt)).ToList()));
        }

        // Counts only: what is in the export is personal data and is never written to a log.
        logger.LogInformation("Tenant data export built. Leases={Leases}", items.Count);
        return new TenantDataExport(TenantDataExport.CurrentSchemaVersion, _clock.GetUtcNow().UtcDateTime, items);
    }

    public async Task<TenantErasureResult?> RequestErasureAsync(
        HostScope scope, Guid tenantId, IReadOnlySet<Guid> allowedLeaseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(allowedLeaseIds);

        var person = await ResolveAsync(scope, tenantId, cancellationToken);
        if (person is null)
            return null;

        var targets = person.Rows
            .Where(r => allowedLeaseIds.Contains(r.LeaseId))
            .OrderBy(r => r.LeaseStartDate)
            .ThenBy(r => r.LeaseId)
            .Select(r => r.LeaseId)
            .Distinct()
            .ToList();
        var propertyNames = (await db.LeaseContracts
                .AsNoTracking()
                .WithinScope(scope)
                .Where(l => targets.Contains(l.Id))
                .Select(l => new { l.Id, l.Property.Name })
                .ToListAsync(cancellationToken))
            .ToDictionary(l => l.Id, l => l.Name);

        // One lease at a time, each saved by the service: a request that stops half way is finished by asking again (it is idempotent).
        var results = new List<TenantErasureLease>();
        foreach (var leaseId in targets)
        {
            var outcome = await partyPrivacy.RequestErasureAsync(leaseId, cancellationToken);
            results.Add(new TenantErasureLease(
                leaseId,
                propertyNames.GetValueOrDefault(leaseId, string.Empty),
                outcome.Status,
                outcome.AnonymizationFrom,
                outcome.PartiesKept));
        }

        logger.LogInformation(
            "Tenant erasure requested. Leases={Leases} Anonymized={Anonymized} Scheduled={Scheduled}",
            results.Count,
            results.Count(r => r.Status == LeaseErasureStatus.Anonymized),
            results.Count(r => r.Status == LeaseErasureStatus.Scheduled));
        return new TenantErasureResult(results);
    }

    // ─── Reading the people ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant parties (not anonymized) of the leases of the scope that satisfy <paramref name="filter"/>. The filter is applied in SQL
    /// on the party, before the projection; the leases are narrowed by the scope in the same statement.
    /// </summary>
    private async Task<List<PartyRow>> LoadPartyRowsAsync(
        HostScope scope, Expression<Func<Party, bool>>? filter, CancellationToken cancellationToken)
    {
        var leases = db.LeaseContracts.AsNoTracking().WithinScope(scope);
        var parties = db.Parties
            .AsNoTracking()
            .Where(p => p.Role == PartyRole.Tenant && p.AnonymizedAt == null)
            .Where(p => leases.Any(l => l.Id == p.LeaseContractId));
        if (filter is not null)
            parties = parties.Where(filter);

        var rows = await parties
            .Select(p => new
            {
                PartyId = p.Id,
                LeaseId = p.LeaseContractId,
                p.FiscalCode,
                p.FirstName,
                p.LastName,
                p.ContactEmail,
                p.IsExtraEU,
                LeaseCreatedAt = p.LeaseContract.CreatedAt,
                LeaseStartDate = p.LeaseContract.StartDate,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new PartyRow(
                r.PartyId, r.LeaseId, r.FiscalCode, r.FirstName, r.LastName, r.ContactEmail, r.IsExtraEU, r.LeaseCreatedAt, r.LeaseStartDate))
            .ToList();
    }

    /// <summary>
    /// The person a party row stands for, with all their rows in the scope; <c>null</c> when the row is not a tenant of the scope (another
    /// org, a property out of reach, anonymized or not there). Any party row of a person resolves to the same person.
    /// </summary>
    private async Task<Person?> ResolveAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken)
    {
        var seed = (await LoadPartyRowsAsync(scope, p => p.Id == tenantId, cancellationToken)).FirstOrDefault();
        if (seed is null)
            return null;

        var code = LongRentTenantRules.NormalizeFiscalCode(seed.FiscalCode);
        var rows = code.Length == 0
            ? new List<PartyRow> { seed }
            : await LoadPartyRowsAsync(scope, p => p.FiscalCode.Trim().ToUpper() == code, cancellationToken);
        return GroupPeople(rows).FirstOrDefault(p => p.Rows.Any(r => r.PartyId == tenantId));
    }

    private static List<Person> GroupPeople(IEnumerable<PartyRow> rows) =>
        rows.GroupBy(r => LongRentTenantRules.PersonKey(r.FiscalCode, r.PartyId), StringComparer.Ordinal)
            .Select(group =>
            {
                var all = group.ToList();
                var oldest = all.OrderBy(r => r.LeaseCreatedAt).ThenBy(r => r.PartyId).First();
                var latest = all.OrderByDescending(r => r.LeaseStartDate).ThenByDescending(r => r.LeaseCreatedAt).ThenByDescending(r => r.PartyId).First();
                return new Person(oldest.PartyId, all, latest);
            })
            .ToList();

    /// <summary>The lease list of the area, one row per lease with its stage: the same read the lease list page makes.</summary>
    private async Task<Dictionary<Guid, LeaseSummaryDto>> LoadSummariesAsync(HostScope scope, DateTime todayInRome) =>
        (await leaseRepository.GetSummariesAsync(scope, new LeaseListQuery(), todayInRome)).ToDictionary(s => s.Id);

    private static PersonView ViewOf(Person person, IReadOnlyDictionary<Guid, LeaseSummaryDto> summaries)
    {
        var leases = person.LeaseIds
            .Select(id => summaries.GetValueOrDefault(id))
            .OfType<LeaseSummaryDto>()
            .OrderByDescending(s => s.StartDate)
            .ThenByDescending(s => s.CreatedAt)
            .Select(s => new TenantLease(s.Id, s.PropertyId, s.Property?.Name ?? string.Empty, s.Stage, s.MonthlyRent, s.CurrentTermEndDate))
            .ToList();
        return new PersonView(person, leases, LongRentTenantRules.IsFormer(leases.Select(l => l.Stage).ToList()));
    }

    /// <summary>The name, in either order, or the property of any of the person's leases, without regard to case.</summary>
    private static bool Matches(PersonView view, string term) =>
        view.Person.Rows.Any(r =>
            $"{r.FirstName} {r.LastName}".Contains(term, StringComparison.OrdinalIgnoreCase)
            || $"{r.LastName} {r.FirstName}".Contains(term, StringComparison.OrdinalIgnoreCase))
        || view.Leases.Any(l => l.PropertyName.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static TenantRow RowOf(PersonView view, IReadOnlyList<LedgerRow> ledger, int onTimeDays)
    {
        var latest = view.Person.Latest;
        var leaseIds = view.Person.LeaseIds.ToHashSet();
        var due = ledger.Where(e => leaseIds.Contains(e.LeaseId)).ToList();
        var punctuality = new TenantPunctuality(
            due.Count(e => LongRentTenantRules.IsOnTime(e.Status, e.DueDate, e.PaidOn, onTimeDays)),
            due.Count);

        return new TenantRow(
            view.Person.Id,
            latest.FirstName,
            latest.LastName,
            latest.IsExtraEU ? TenantCitizenship.NonEu : TenantCitizenship.Eu,
            PersonalDataMasking.MaskEmail(latest.ContactEmail),
            view.IsFormer,
            view.Leases,
            punctuality);
    }

    /// <summary>
    /// The installments of the leases that count for punctuality: due on or before <paramref name="today"/> and not cancelled. One
    /// statement for all the leases asked for, restricted to the scope like every other read.
    /// </summary>
    private async Task<List<LedgerRow>> LoadLedgerAsync(
        HostScope scope, List<Guid> leaseIds, DateOnly today, CancellationToken cancellationToken)
    {
        if (leaseIds.Count == 0)
            return [];

        var rows = await db.RentLedgerEntries
            .AsNoTracking()
            .WithinScope(scope)
            .Where(e => leaseIds.Contains(e.LeaseContractId) && e.Status != RentLedgerStatus.Cancelled && e.DueDate <= today)
            .Select(e => new { e.Id, e.LeaseContractId, e.DueDate, e.AmountDue, e.Status, e.PaidOn })
            .ToListAsync(cancellationToken);
        return rows.Select(e => new LedgerRow(e.Id, e.LeaseContractId, e.DueDate, e.AmountDue, e.Status, e.PaidOn)).ToList();
    }

    private int OnTimeDays() => LongRentTenantRules.OnTimeDays(configuration[LongRentTenantRules.OnTimeDaysKey]);
}
