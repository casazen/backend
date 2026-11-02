using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ILongRentFiscalReportService"/>
/// <remarks>
/// <para>Four statements whatever the number of leases: the leases of the scope in force in the year (scalars, with the first tenant's
/// name), the first installment tracked of each, the rent paid in the year of each, and the high-tension status of the comuni of the
/// canone concordato leases with the cedolare secca (the only ones it matters for). Every one starts from the org of the scope and is
/// narrowed to the properties the caller reaches in SQL (<see cref="LongRentScopeQueries"/>). The rules are
/// <see cref="LongRentFiscalRules"/>, the same for the page and the file.</para>
/// <para>In force means signed by every party (<see cref="LeaseRenewal.InForceStatuses"/>) and not over before the year: a lease is
/// there when its first day is not after the last day of the year and the end of the period it is in now (LR-14) is not before the first.
/// A payment received in the year for a lease that was over by then is therefore not in the report: it is not in any year's. A lease
/// ended on the day it starts (or before) never ran and is left out (<see cref="LongRentFiscalRules.Ran"/>).</para>
/// <para>Nothing is written and nothing is logged: the names of the tenants never reach a log.</para>
/// </remarks>
public sealed class LongRentFiscalReportService(
    AppDbContext db,
    IHighTensionAreaComuneRepository ataComuni,
    IOptions<CedolareAdvisoryOptions> options,
    TimeProvider? timeProvider = null) : ILongRentFiscalReportService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<LongRentFiscalReport> GetAsync(HostScope scope, int year, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var firstDay = new DateOnly(year, 1, 1);
        var lastDay = new DateOnly(year, 12, 31);
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEndExclusive = yearStart.AddYears(1);
        var inForce = LeaseRenewal.InForceStatuses.ToArray();

        var found = await db.LeaseContracts
            .AsNoTracking()
            .WithinScope(scope)
            .Where(l => inForce.Contains(l.Status) && l.StartDate < yearEndExclusive && l.CurrentTermEndDate >= yearStart)
            .Select(l => new
            {
                l.Id,
                l.PropertyId,
                PropertyName = l.Property.Name,
                City = l.Property.City,
                l.FiscalRegime,
                l.ContractType,
                l.TaxRegime,
                l.MonthlyRent,
                l.StartDate,
                l.CurrentTermEndDate,
                TenantFirstName = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.FirstName).FirstOrDefault(),
                TenantLastName = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.LastName).FirstOrDefault(),
                TenantAnonymized = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.AnonymizedAt != null).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // A lease ended on the day it starts never ran: it is in no report (decision of the product owner, 2026-11-01).
        found = found
            .Where(l => LongRentFiscalRules.Ran(DateOnly.FromDateTime(l.StartDate), DateOnly.FromDateTime(l.CurrentTermEndDate)))
            .ToList();

        var leaseIds = found.Select(l => l.Id).ToList();
        var trackedFrom = new Dictionary<Guid, DateOnly>();
        var collected = new Dictionary<Guid, decimal>();
        if (leaseIds.Count > 0)
        {
            // The first installment of each lease that is still part of the rent (not cancelled): where the register starts.
            trackedFrom = await db.RentLedgerEntries
                .AsNoTracking()
                .WithinScope(scope)
                .Where(e => leaseIds.Contains(e.LeaseContractId) && e.Status != RentLedgerStatus.Cancelled)
                .GroupBy(e => e.LeaseContractId)
                .Select(g => new { LeaseId = g.Key, From = g.Min(e => e.PeriodStart) })
                .ToDictionaryAsync(x => x.LeaseId, x => x.From, cancellationToken);

            // The rent paid in the year (by the day it was paid, not the period it pays: cash), online or declared by the landlord.
            collected = await db.RentLedgerEntries
                .AsNoTracking()
                .WithinScope(scope)
                .Where(e => leaseIds.Contains(e.LeaseContractId)
                    && e.Status == RentLedgerStatus.Paid
                    && e.PaidOn >= firstDay
                    && e.PaidOn <= lastDay)
                .GroupBy(e => e.LeaseContractId)
                .Select(g => new { LeaseId = g.Key, Amount = g.Sum(e => e.AmountDue) })
                .ToDictionaryAsync(x => x.LeaseId, x => x.Amount, cancellationToken);
        }

        var ataByCity = new Dictionary<string, HighTensionAreaStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var city in found
                     .Where(l => l.ContractType == LeaseContractType.Concordato && l.TaxRegime == LeaseTaxRegime.CedolareSecca)
                     .Select(l => l.City?.Trim())
                     .Where(city => !string.IsNullOrEmpty(city))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ataByCity[city!] = HighTensionArea.StatusOf(await ataComuni.GetByComuneAsync(city!, cancellationToken));
        }

        var rows = found
            .Select(l => LongRentFiscalRules.Row(
                new LongRentFiscalLease(
                    l.Id,
                    l.PropertyId,
                    l.PropertyName,
                    l.TenantAnonymized ? null : l.TenantFirstName,
                    l.TenantAnonymized ? null : l.TenantLastName,
                    l.TenantAnonymized,
                    l.FiscalRegime,
                    l.ContractType,
                    l.TaxRegime,
                    l.MonthlyRent,
                    DateOnly.FromDateTime(l.StartDate),
                    DateOnly.FromDateTime(l.CurrentTermEndDate)),
                year,
                collected.TryGetValue(l.Id, out var paid) ? paid : null,
                trackedFrom.TryGetValue(l.Id, out var tracked) ? tracked : null,
                l.City is { } city && ataByCity.TryGetValue(city.Trim(), out var ata) ? ata : HighTensionAreaStatus.NotListed,
                options.Value))
            .OrderBy(r => r.PropertyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.StartDate)
            .ThenBy(r => r.LeaseId)
            .ToList();

        return new LongRentFiscalReport(year, _clock.TodayInRomeAsDateOnly(), rows, LongRentFiscalRules.Totals(rows));
    }
}
