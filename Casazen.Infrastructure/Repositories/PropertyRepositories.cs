using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class PropertyRepository(AppDbContext context) : IPropertyRepository
{
    public async Task<Property?> GetByIdAsync(Guid id)
    {
        return await context.Properties
            .Include(p => p.Bookings)
            .Include(p => p.OtaIntegrations)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IEnumerable<Property>> GetByOwnerAsync(string ownerId)
    {
        return await context.Properties
            .Where(p => p.OwnerId == ownerId && p.IsActive)
            .ToListAsync();
    }

    public async Task<IEnumerable<Property>> GetAllAsync()
    {
        return await context.Properties
            .Where(p => p.IsActive)
            .ToListAsync();
    }

    public async Task<IEnumerable<Property>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice)
    {
        return await GetSearchQueryable(city, bedrooms, maxPrice).ToListAsync();
    }

    public IQueryable<Property> GetSearchQueryable(string? city, int? bedrooms, decimal? maxPrice, Guid? orgId = null)
    {
        var query = context.Properties.AsQueryable()
            .Where(p => p.IsActive && p.ComplianceStatus == PropertyComplianceStatus.Active);

        if (orgId.HasValue)
            query = query.Where(p => p.OrgId == orgId.Value);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City.ToLower().Contains(city.ToLower()));

        if (bedrooms.HasValue)
            query = query.Where(p => p.Bedrooms >= bedrooms.Value);

        if (maxPrice.HasValue)
            query = query.Where(p => p.NightlyRate <= maxPrice.Value);

        return query;
    }

    public async Task<Property> AddAsync(Property property)
    {
        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property;
    }

    public async Task<Property> UpdateAsync(Property property)
    {
        context.Properties.Update(property);
        await context.SaveChangesAsync();
        return property;
    }

    public async Task DeleteAsync(Guid id)
    {
        var property = await context.Properties.FindAsync(id);
        if (property != null)
        {
            context.Properties.Remove(property);
            await context.SaveChangesAsync();
        }
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        return await context.Properties.AnyAsync(p => p.Id == id);
    }

    public async Task<Property?> GetPropertyDetailAsync(Guid id)
    {
        return await context.Properties
            .Include(p => p.PropertyDocuments)
            .Include(p => p.OtaIntegrations)
            .Include(p => p.Bookings)
            .Include(p => p.PricingAdapterConfig)
            .FirstOrDefaultAsync(p => p.Id == id && p.IsActive);
    }

    public async Task<IEnumerable<Property>> GetByOwnerForComplianceAsync(string ownerId)
    {
        return await context.Properties
            .Where(p => p.OwnerId == ownerId)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<bool> CinCodeExistsOnOtherPropertyAsync(string cinCode, Guid excludePropertyId)
    {
        return await context.Properties.AnyAsync(p =>
            p.CinCode == cinCode && p.Id != excludePropertyId);
    }

    public async Task<bool> SlugExistsInOrgAsync(Guid orgId, string slug, Guid? excludePropertyId = null)
    {
        var query = context.Properties.Where(p => p.OrgId == orgId && p.Slug == slug);
        if (excludePropertyId.HasValue)
            query = query.Where(p => p.Id != excludePropertyId.Value);
        return await query.AnyAsync();
    }
}
