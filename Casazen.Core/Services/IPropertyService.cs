using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPropertyService
{
    Task<Property?> GetPropertyAsync(Guid id);
    Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId);

    /// <summary>Active properties of <paramref name="scope"/> (built by the web layer from the caller, TN-3).</summary>
    Task<IEnumerable<Property>> GetPropertiesAsync(HostScope scope);
    Task<Property> CreatePropertyAsync(Property property);
    Task<Property> UpdatePropertyAsync(Property property);
    Task<bool> DeletePropertyAsync(Guid id);
    Task<IEnumerable<PublicPropertyDto>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice);
    Task<IEnumerable<PublicPropertyDto>> SearchByOrgAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id);
    Task<PublicPropertyDetailDto?> GetPublicPropertyForOrgAsync(string slugOrId, Guid orgId);
    Task<Property> AddImageAsync(Guid propertyId, string imageUrl);
    Task<Property> RemoveImageAsync(Guid propertyId, int imageIndex);
    Task<Property> ReorderImagesAsync(Guid propertyId, List<string> orderedImageUrls);
    Task<PropertyDetailResponse> GetPropertyDetailAsync(Guid propertyId);
    Task<OwnerCinComplianceResult> GetOwnerCinComplianceAsync(string ownerId, string? cinStatus, int page, int pageSize);
    Task UpdatePropertyCinAsync(Guid propertyId, string? cinCode);

    /// <summary>
    /// Sets the cadastral identification of the unit (LT-10): trimmed, empty values cleared, category upper-case.
    /// Only lengths are checked; the formats are not validated.
    /// </summary>
    Task UpdateCadastralDataAsync(Guid propertyId, PropertyCadastralData data);
}

/// <summary>Cadastral identification of a property (catasto fabbricati): foglio, particella, subalterno, categoria, rendita.</summary>
public sealed record PropertyCadastralData(
    string? Sheet,
    string? Parcel,
    string? Subaltern,
    string? Category,
    decimal? Income);