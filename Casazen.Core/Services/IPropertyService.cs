using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IPropertyService
{
    Task<Property?> GetPropertyAsync(Guid id);

    Task<Property?> GetPropertyRecordAsync(Guid id);

    Task<IReadOnlyList<CancellationPolicyOptionDto>> GetCancellationPoliciesAsync();
    Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId);

    Task<IEnumerable<Property>> GetPropertiesAsync(HostScope scope);

    Task<IEnumerable<Property>> GetPropertiesAsync(HostScope scope, RentalMode mode);
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

    Task<IEnumerable<PublicPropertyDto>> SearchAsync(PublicPropertySearchCriteria criteria);
    Task<IEnumerable<PublicPropertyDto>> SearchByOrgAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id);
    Task<PublicPropertyDetailDto?> GetPublicPropertyForOrgAsync(string slugOrId, Guid orgId);
    Task<PropertyDetailResponse> GetPropertyDetailAsync(Guid propertyId);

    /// <summary>
    /// CIN status of the properties in the caller's <paramref name="scope"/> (TN-3, same reach as the property list):
    /// the owner's own properties, every property of the org for an org-wide role (MO-12, A6-20).
    /// </summary>
    Task<OwnerCinComplianceResult> GetCinComplianceAsync(HostScope scope, string? cinStatus, int page, int pageSize);
    Task UpdatePropertyCinAsync(Guid propertyId, string? cinCode);

    Task UpdateCadastralDataAsync(Guid propertyId, PropertyCadastralData data);
}

public sealed record PropertyCadastralData(
    string? Sheet,
    string? Parcel,
    string? Subaltern,
    string? Category,
    decimal? Income);