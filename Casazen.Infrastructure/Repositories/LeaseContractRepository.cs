using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class LeaseContractRepository(AppDbContext context) : ILeaseContractRepository
{
    public async Task<LeaseContract?> GetByIdAsync(Guid id)
        => await context.LeaseContracts.FindAsync(id);

    public async Task<LeaseContract?> GetByIdWithDetailsAsync(Guid id)
        => await context.LeaseContracts
            .Include(l => l.Property)
                // The APE of the property: its code and energy class go into the contract (LT-10, LT-03).
                .ThenInclude(p => p.PropertyDocuments.Where(d => d.DocumentType == DocumentType.Ape))
            .Include(l => l.Parties)
            .Include(l => l.Registration)
            .Include(l => l.Events.OrderBy(e => e.OccurredAt))
            .FirstOrDefaultAsync(l => l.Id == id);

    public async Task<IReadOnlyList<LeaseSummaryDto>> GetSummariesAsync(HostScope scope, Guid? propertyId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var query = context.LeaseContracts
            .AsNoTracking()
            .Where(l => l.OrgId == scope.OrgId)
            .InScope(scope);

        if (propertyId.HasValue)
            query = query.Where(l => l.PropertyId == propertyId.Value);

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new LeaseSummaryDto(
                l.Id,
                l.PropertyId,
                new LeasePropertyDto(l.Property.Id, l.Property.Name, l.Property.City),
                l.Status,
                l.FiscalRegime,
                l.ContractType,
                l.TaxRegime,
                l.StartDate,
                l.EndDate,
                l.MonthlyRent,
                l.StipulaDate,
                l.RegistrationDeadline,
                l.Parties.Count,
                l.Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU),
                l.CreatedAt,
                l.UpdatedAt))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<LeaseSummaryDto>> GetSummariesAsync(HostScope scope, LeaseListQuery query, DateTime todayInRome)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var today = DateOnly.FromDateTime(todayInRome);
        var leases = context.LeaseContracts
            .AsNoTracking()
            .Where(l => l.OrgId == scope.OrgId)
            .InScope(scope);

        if (query.PropertyId is { } propertyId)
            leases = leases.Where(l => l.PropertyId == propertyId);

        if (query.View != LeaseListView.All)
            leases = leases.Where(LeaseListViews.Predicate(query.View, todayInRome));

        if (query.NormalizedSearch is { } search)
        {
            // The property (name, city, address) or any tenant whose data were not anonymized (first and last name, in
            // either order). A plain Contains: the text is a parameter, never a LIKE pattern, so % and _ are just characters.
            var term = search.ToLowerInvariant();
            leases = leases.Where(l =>
                l.Property.Name.ToLower().Contains(term)
                || l.Property.City.ToLower().Contains(term)
                || l.Property.Address.ToLower().Contains(term)
                || l.Parties.Any(p => p.Role == PartyRole.Tenant
                    && p.AnonymizedAt == null
                    && ((p.FirstName + " " + p.LastName).ToLower().Contains(term)
                        || (p.LastName + " " + p.FirstName).ToLower().Contains(term))));
        }

        // The installments still to be collected, one row per lease: the next due date, and what is past due. Joined once to
        // the list (LEFT JOIN of a grouped subquery), so the list is one statement whatever the number of leases.
        var orgId = scope.OrgId;
        // "Open" and "overdue" are RentInstallmentRules.Open and OverdueOn, written inline in the aggregates (a group cannot take
        // an expression variable); LongRentSqlShapeTests and the integration tests hold the two to the same rule.
        var rent = context.RentLedgerEntries
            .Where(e => e.OrgId == orgId)
            .Where(RentInstallmentRules.Open)
            .GroupBy(e => e.LeaseContractId)
            .Select(g => new
            {
                LeaseContractId = g.Key,
                NextDueDate = g.Min(e => e.DueDate),
                OverdueCount = g.Count(e =>
                    (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate < today),
                OverdueAmount = g.Sum(e =>
                    (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate < today
                        ? e.AmountDue
                        : 0m),
                OldestOverdue = g.Min(e =>
                    (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate < today
                        ? (DateOnly?)e.DueDate
                        : null),
            });

        var rows = await (
            from l in leases
            join r in rent on l.Id equals r.LeaseContractId into rents
            from r in rents.DefaultIfEmpty()
            orderby l.CreatedAt descending, l.Id
            select new
            {
                l.Id,
                l.PropertyId,
                PropertyName = l.Property.Name,
                PropertyCity = l.Property.City,
                l.Status,
                l.FiscalRegime,
                l.ContractType,
                l.TaxRegime,
                l.StartDate,
                l.EndDate,
                l.MonthlyRent,
                l.StipulaDate,
                l.RegistrationDeadline,
                l.CreatedAt,
                l.UpdatedAt,
                PartyCount = l.Parties.Count,
                HasExtraEUTenant = l.Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU),
                // The first tenant (the order entered), by three scalar subqueries on the unique index of the parties
                // (lease, role, position): they read one row per lease of the list, never the parties of the whole table.
                TenantFirstName = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.FirstName).FirstOrDefault(),
                TenantLastName = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.LastName).FirstOrDefault(),
                TenantAnonymized = l.Parties.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).Select(p => p.AnonymizedAt != null).FirstOrDefault(),
                // A lease with nothing to collect has no row on the right of the join: every column of it is null.
                NextDueDate = (DateOnly?)r.NextDueDate,
                OverdueCount = (int?)r.OverdueCount ?? 0,
                OverdueAmount = (decimal?)r.OverdueAmount ?? 0m,
                OldestOverdue = (DateOnly?)r.OldestOverdue,
            }).ToListAsync();

        return rows
            .Select(x => new LeaseSummaryDto(
                x.Id,
                x.PropertyId,
                new LeasePropertyDto(x.PropertyId, x.PropertyName, x.PropertyCity),
                x.Status,
                x.FiscalRegime,
                x.ContractType,
                x.TaxRegime,
                x.StartDate,
                x.EndDate,
                x.MonthlyRent,
                x.StipulaDate,
                x.RegistrationDeadline,
                x.PartyCount,
                x.HasExtraEUTenant,
                x.CreatedAt,
                x.UpdatedAt,
                // Names only while the data of that tenant were not anonymized (retention or erasure request, LT-12).
                TenantFirstName: x.TenantAnonymized ? null : x.TenantFirstName,
                TenantLastName: x.TenantAnonymized ? null : x.TenantLastName,
                TenantAnonymized: x.TenantAnonymized,
                NextRentDueDate: x.NextDueDate,
                OverdueRentCount: x.OverdueCount,
                OverdueRentAmount: x.OverdueAmount,
                OverdueDays: x.OldestOverdue is { } oldest ? today.DayNumber - oldest.DayNumber : null))
            .ToList();
    }

    public async Task<IEnumerable<LeaseContract>> GetByPropertyAsync(Guid propertyId)
        => await context.LeaseContracts
            .Include(l => l.Parties)
            .Where(l => l.PropertyId == propertyId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<LeaseContract>> GetByStatusAsync(LeaseStatus status)
        => await context.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Registration)
            .Where(l => l.Status == status)
            .ToListAsync();

    public async Task<LeaseContract> AddAsync(LeaseContract lease)
    {
        context.LeaseContracts.Add(lease);
        await context.SaveChangesAsync();
        return lease;
    }

    public async Task<LeaseContract> UpdateAsync(LeaseContract lease)
    {
        lease.UpdatedAt = DateTime.UtcNow;
        context.LeaseContracts.Update(lease);
        await context.SaveChangesAsync();
        return lease;
    }
}
