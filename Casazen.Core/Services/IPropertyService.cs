using Casazen.Core.DTOs;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPropertyService
{
    Task<Property?> GetPropertyAsync(Guid id);
    Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId);
    Task<Property> CreatePropertyAsync(Property property);
    Task<Property> UpdatePropertyAsync(Property property);
    Task<bool> DeletePropertyAsync(Guid id);
    Task<IEnumerable<PublicPropertyDto>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice);
    Task<IEnumerable<PublicPropertyDto>> SearchByOrgAsync(Guid orgId);
    Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id);
    Task<PublicPropertyDetailDto?> GetPublicPropertyForOrgAsync(Guid id, Guid orgId);
    Task<Property> AddImageAsync(Guid propertyId, string imageUrl);
    Task<Property> RemoveImageAsync(Guid propertyId, int imageIndex);
    Task<Property> ReorderImagesAsync(Guid propertyId, List<string> orderedImageUrls);
    Task<PropertyDetailResponse> GetPropertyDetailAsync(Guid propertyId);
}