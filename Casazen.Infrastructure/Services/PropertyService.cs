using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyService(IPropertyRepository repository, ILogger<PropertyService> logger) : IPropertyService
{
    public async Task<Property?> GetPropertyAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId)
    {
        return await repository.GetByOwnerAsync(ownerId);
    }

    public async Task<IEnumerable<Property>> GetAllPropertiesAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Property> CreatePropertyAsync(Property property)
    {
        logger.LogInformation("Creating property: {Name}", property.Name);
        return await repository.AddAsync(property);
    }

    public async Task<Property> UpdatePropertyAsync(Property property)
    {
        logger.LogInformation("Updating property: {Id}", property.Id);
        return await repository.UpdateAsync(property);
    }

    public async Task<bool> DeletePropertyAsync(Guid id)
    {
        logger.LogInformation("Deleting property: {Id}", id);
        await repository.DeleteAsync(id);
        return true;
    }

    public async Task<IEnumerable<Property>> SearchAsync(string city, int? bedrooms, decimal? maxPrice)
    {
        logger.LogInformation("Searching properties: city={City}, bedrooms={Bedrooms}, maxPrice={MaxPrice}", city, bedrooms, maxPrice);
        return await repository.SearchAsync(city, bedrooms, maxPrice);
    }
}