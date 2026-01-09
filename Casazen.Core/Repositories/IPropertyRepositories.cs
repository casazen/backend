using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPropertyRepository
{
    Task<Property?> GetByIdAsync(Guid id);
    Task<IEnumerable<Property>> GetByOwnerAsync(Guid ownerId);
    Task<IEnumerable<Property>> GetAllAsync();
    Task<IEnumerable<Property>> SearchAsync(string city, int? bedrooms, decimal? maxPrice);
    Task<Property> AddAsync(Property property);
    Task<Property> UpdateAsync(Property property);
    Task DeleteAsync(Guid id);
    Task<bool> ExistsAsync(Guid id);
}