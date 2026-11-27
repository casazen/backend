using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPortalAccountHolderResolver"/>
/// <remarks>
/// It reads the rows of the org it is asked about (the property names its org), whatever the tenant of the caller: the readers are the services of the owner
/// accounting, which name the org themselves. <see cref="ForOrgAsync"/> ignores the tenant filter and names the org in every predicate: the statement of an owner
/// is read on the owner portal too, whose request has no org of its own (the filter is on and matches nothing), and there it must say what the agency's PDF and
/// the invoice of the property manager say (E19/F01/US01/T01). A stored channel that is no longer a <see cref="BookingSource"/> is skipped.
/// </remarks>
public sealed class PortalAccountHolderResolver(AppDbContext db) : IPortalAccountHolderResolver
{
    public async Task<PortalAccountHolder> ResolveAsync(Guid propertyId, BookingSource channel, CancellationToken ct = default)
    {
        var orgId = await db.Properties.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.Id == propertyId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return PortalAccountHolder.Owner;

        var name = channel.ToString();
        var row = await db.PropertyChannelAccountHolders.AsNoTracking()
            .Where(h => h.OrgId == orgId && h.PropertyId == propertyId && h.Channel == name)
            .Select(h => (PortalAccountHolder?)h.Holder)
            .FirstOrDefaultAsync(ct);
        return row ?? await DefaultOfAsync(orgId.Value, ct);
    }

    public async Task<PortalAccountHolders> ForOrgAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
            .OfOrganization(orgId)
            .Select(p => new { p.PortalAccountHolder, p.RechargePortalCommissions })
            .FirstOrDefaultAsync(ct);
        var rows = await db.PropertyChannelAccountHolders.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
            .Where(h => h.OrgId == orgId)
            .Select(h => new { h.PropertyId, h.Channel, h.Holder })
            .ToListAsync(ct);

        var overrides = new Dictionary<(Guid, BookingSource), PortalAccountHolder>();
        foreach (var row in rows)
        {
            if (Enum.TryParse<BookingSource>(row.Channel, ignoreCase: false, out var channel) && Enum.IsDefined(channel))
                overrides[(row.PropertyId, channel)] = row.Holder;
        }

        return new PortalAccountHolders(
            profile?.PortalAccountHolder ?? PortalAccountHolders.Defaults.Default,
            profile?.RechargePortalCommissions ?? PortalAccountHolders.Defaults.RechargePortalCommissions,
            overrides);
    }

    private async Task<PortalAccountHolder> DefaultOfAsync(Guid orgId, CancellationToken ct) =>
        await db.OrgFiscalIssuerProfiles.AsNoTracking()
            .OfOrganization(orgId)
            .Select(p => (PortalAccountHolder?)p.PortalAccountHolder)
            .FirstOrDefaultAsync(ct)
        ?? PortalAccountHolders.Defaults.Default;
}
