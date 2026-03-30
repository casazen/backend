using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPropertyService
{
    Task<Property?> GetPropertyAsync(Guid id);
    Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId);
    Task<Property> CreatePropertyAsync(Property property);
    Task<Property> UpdatePropertyAsync(Property property);
    Task<bool> DeletePropertyAsync(Guid id);
    Task<IEnumerable<Property>> SearchAsync(string city, int? bedrooms, decimal? maxPrice);
}