using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed partial class CondominiumAccountingService
{
    public async Task<IReadOnlyList<PropertyCondominiumView>> ListPropertiesAsync(HostScope scope, CancellationToken cancellationToken = default)
    {
        var properties = await db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope)
            .OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken);
        var ids = properties.Select(p => p.Id).ToArray();
        var units = await db.CondominiumUnits.AsNoTracking().Include(u => u.Condominium).ThenInclude(c => c.Periods)
            .Where(u => u.OrgId == scope.OrgId && ids.Contains(u.PropertyId)).ToListAsync(cancellationToken);
        return properties.Select(p => new PropertyCondominiumView(p.Id, p.Name,
            units.Where(u => u.PropertyId == p.Id).Select(u => u.Condominium).DistinctBy(c => c.Id)
                .Select(c => new PropertyCondominiumBook(c.Id, c.Name, c.FiscalStartMonth,
                    c.Periods.OrderByDescending(y => y.StartDate).Select(ToPeriod).ToList())).ToList())).ToList();
    }

    // Creating a book from the property is allowed to a member who reaches that property.
    // The book and its link are saved together: no unlinked or partially configured book is left behind.
    public async Task<CondominiumView> SetupPropertyAsync(HostScope scope, Guid propertyId, CreateCondominiumCommand command,
        string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(propertyId, cancellationToken);
        var property = await db.Properties.Where(p => p.OrgId == scope.OrgId && p.Id == propertyId).InScope(scope)
            .SingleOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Property not found");
        var existing = await db.CondominiumUnits.Include(u => u.Condominium)
            .Where(u => u.PropertyId == propertyId && u.OrgId == scope.OrgId).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return new(existing.CondominiumId, existing.Condominium.Name, existing.Condominium.FiscalStartMonth);
        }
        RequireUser(userId);
        if (!Casazen.Core.Condominium.FiscalYear.IsValidStartMonth(command.FiscalStartMonth))
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");
        var name = RequireText(command.Name, Condominium.NameMaxLength, CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");
        var condo = new Condominium { OrgId = scope.OrgId, Name = name, FiscalStartMonth = command.FiscalStartMonth,
            CreatedByUserId = userId, CreatedAt = UtcNow, UpdatedAt = UtcNow };
        condo.Units.Add(new CondominiumUnit { OrgId = scope.OrgId, PropertyId = property.Id, CreatedAt = UtcNow, UpdatedAt = UtcNow });
        db.Condominiums.Add(condo);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(condo.Id, condo.Name, condo.FiscalStartMonth);
    }
}
