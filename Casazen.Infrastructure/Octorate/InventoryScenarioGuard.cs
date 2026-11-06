using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IInventoryScenarioGuard"/> (E02/F01/US03, consumed by E02/F03/US01/T06): a listing is created on Octorate only for a property whose
/// scenario a person explicitly declared <see cref="OctorateInventoryScenario.NewInventory"/>. Existing inventory (<c>onOctorate</c>,
/// <c>onOtas</c>) is imported and mapped instead, and neither a missing declaration nor a missing mapping proves that a property is new.
/// </summary>
/// <remarks>
/// The partial unique index of the intents (<c>UIX_OctorateInventoryIntents_OrgId_PropertyId</c>) is the database half of the guard: a
/// property has one current intent, so there is no second row that could say «new» next to a first that says «existing». Every query names
/// the org: the guard also runs from a job, where no tenant filter applies.
/// </remarks>
public sealed class InventoryScenarioGuard(AppDbContext db) : IInventoryScenarioGuard
{
    public async Task<OctorateInventoryScenario?> GetScenarioAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var scenario = await db.OctorateInventoryIntents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(i => i.OrgId == orgId && i.PropertyId == propertyId)
            .Select(i => (OctorateInventoryScenario?)i.Scenario)
            .FirstOrDefaultAsync(ct);
        return scenario;
    }

    public async Task EnsureNewInventoryAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        if (await GetScenarioAsync(orgId, propertyId, ct) != OctorateInventoryScenario.NewInventory)
            throw new DomainConflictException("octorate_existing_inventory_must_import", "OctorateExistingInventoryMustImport");
    }
}
