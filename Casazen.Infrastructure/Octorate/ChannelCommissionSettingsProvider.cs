using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IChannelCommissionSettingsProvider"/> on the portal cache of the marketplace (E09/F01/US01/T02, <see cref="MarketplacePortal"/>): how each channel
/// settles its commission, from the two flags of its portal. It reads CasaZen's own database, never Octorate, and adds no client method.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The channel of a portal</b> is the one the booking import gives a reservation of it (<see cref="BookingStateMapper.ToSource"/>: the portal code is the
/// channel id, its name the second key), so a report and an import never disagree on which channel a portal is.</item>
/// <item><b>What it does not claim.</b> A portal that maps to no channel of its own (<see cref="BookingSource.OtherChannel"/>: many portals share it), one the
/// catalog no longer lists, and the booking engine of the agency (a direct channel, no commission) are left out; two portals of one channel that disagree make it
/// unknown; and an org that is not connected to Octorate in this environment gets nothing at all, so the report works from the stored amounts alone.</item>
/// <item><b>Only a flag that is true counts</b> (<see cref="ChannelCostRules.ModeOf"/>): the cache stores a flag Octorate did not send as false, which is not a "no".</item>
/// </list>
/// </remarks>
public sealed class ChannelCommissionSettingsProvider(
    AppDbContext db,
    BookingStateMapper mapper,
    IHostEnvironment hostEnvironment) : IChannelCommissionSettingsProvider
{
    public async Task<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>> GetModesAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connected = await db.OctorateConnections
            .AsNoTracking()
            .AnyAsync(c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected, cancellationToken);
        if (!connected)
            return new Dictionary<BookingSource, ChannelCommissionMode>();

        var portals = await db.MarketplacePortals
            .AsNoTracking()
            .Where(p => p.IsListed)
            .Select(p => new { p.PortalCode, p.Name, p.CommissionIncludedInPayments, p.CommissionsInvoiced })
            .ToListAsync(cancellationToken);

        return portals
            .Select(p => (Source: mapper.ToSource(p.PortalCode, p.Name), Mode: ChannelCostRules.ModeOf(p.CommissionIncludedInPayments, p.CommissionsInvoiced)))
            .Where(x => x.Source != BookingSource.OtherChannel && ChannelCostRules.KindOf(x.Source) == ChannelCostKind.ChannelCommission)
            .GroupBy(x => x.Source)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Mode).Distinct().Count() == 1 ? g.First().Mode : ChannelCommissionMode.Unknown);
    }
}
