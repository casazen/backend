using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPropertyService
{
    Task<Property?> GetPropertyAsync(Guid id);

    /// <summary>
    /// The property row alone (no bookings, OTA integrations or documents), tracked: for <c>GET /properties/{id}</c>
    /// and its update (A2-32, A2-04).
    /// </summary>
    Task<Property?> GetPropertyRecordAsync(Guid id);

    /// <summary>The cancellation policies a property can reference (A2-04).</summary>
    Task<IReadOnlyList<CancellationPolicyOptionDto>> GetCancellationPoliciesAsync();
    Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId);

    /// <summary>Active properties of <paramref name="scope"/> (built by the web layer from the caller, TN-3).</summary>
    Task<IEnumerable<Property>> GetPropertiesAsync(HostScope scope);
    Task<Property> CreatePropertyAsync(Property property);

    /// <summary>
    /// Saves a property changed in place: normalizes the CIN, checks the slug (409 <c>duplicate_property_slug</c>) and
    /// the cancellation policy (422 <c>cancellation_policy_not_found</c>).
    /// </summary>
    Task<Property> UpdatePropertyAsync(Property property);

    /// <summary>
    /// Pauses a property (PC-03, A2-05): hidden from public search, its public page and new guest bookings until
    /// reactivated. Still counts against the plan's property limit and stays fully visible and editable to the host;
    /// its existing bookings are untouched. Idempotent: pausing an already-paused property leaves <c>PausedAt</c> as
    /// it was.
    /// </summary>
    Task<Property> PausePropertyAsync(Property property);

    /// <summary>Reactivates a paused property (PC-03, A2-05). Idempotent when already active.</summary>
    Task<Property> ActivatePropertyAsync(Property property);

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