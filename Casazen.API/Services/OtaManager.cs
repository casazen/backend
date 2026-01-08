using Casazen.API.Data;
using Casazen.API.Infrastructure;
using Casazen.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Casazen.API.Services;

public interface IOtaManager
{
    Task SyncAll(Guid propertyId);
    Task<bool> Sync(string otaName, string externalId);
    Task<bool> UpdatePricing(Guid propertyId, decimal newPrice);
}

public class OtaManager : IOtaManager
{
    private readonly AppDbContext _context;
    private readonly Dictionary<string, IChannelAdapter> _adapters;

    public OtaManager(AppDbContext context)
    {
        _context = context;
        _adapters = new()
        {
            {"airbnb", new AirbnbAdapter()},
            {"booking", new BookingComAdapter()},
            {"expedia", new ExpediaAdapter()},
            {"vrbo", new VrboAdapter()},
            {"tripadvisor", new TripAdvisorAdapter()},
            {"agoda", new AgodaAdapter()},
        };
    }

    public async Task SyncAll(Guid propertyId)
    {
        var property = await _context.Properties
            .Include(p => p.OtaIntegrations)
            .FirstOrDefaultAsync(p => p.Id == propertyId);

        if (property == null) return;

        var syncTasks = property.OtaIntegrations
            .Where(i => i.IsActive)
            .Select(i => Sync(i.Platform, i.ExternalPropertyId));

        await Task.WhenAll(syncTasks);
    }

    public async Task<bool> Sync(string otaName, string externalId)
    {
        if (!_adapters.TryGetValue(otaName.ToLower(), out var adapter))
            return false;

        try
        {
            await adapter.FetchAndSync(externalId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> UpdatePricing(Guid propertyId, decimal newPrice)
    {
        var integrations = await _context.OtaIntegrations
            .Where(i => i.PropertyId == propertyId && i.IsActive)
            .ToListAsync();

        var updateTasks = integrations.Select(i =>
        {
            if (_adapters.TryGetValue(i.Platform.ToLower(), out var adapter))
                return adapter.UpdatePrice(i.ExternalPropertyId, newPrice);
            return Task.FromResult(false);
        });

        var results = await Task.WhenAll(updateTasks);
        return results.All(r => r);
    }
}