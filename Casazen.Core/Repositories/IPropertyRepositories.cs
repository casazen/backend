using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPropertyRepository
{
    Task<Property?> GetByIdAsync(Guid id);

    /// <summary>
    /// The property row alone, tracked, without bookings, OTA integrations or documents (A2-32): what the record
    /// endpoints read and update. Saving it never rewrites rows of other aggregates (A2-04).
    /// </summary>
    Task<Property?> GetRecordAsync(Guid id);
    Task<IEnumerable<Property>> GetByOwnerAsync(string ownerId);

    /// <summary>
    /// Active properties of <see cref="HostScope.OrgId"/>, restricted to <see cref="HostScope.OwnerId"/> when set
    /// (TN-3 list filter, in SQL).
    /// </summary>
    Task<IEnumerable<Property>> GetByScopeAsync(HostScope scope);
    Task<IEnumerable<Property>> GetAllAsync();
    Task<IEnumerable<Property>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice);
    IQueryable<Property> GetSearchQueryable(string? city, int? bedrooms, decimal? maxPrice, Guid? orgId = null);
    Task<Property> AddAsync(Property property);
    Task<Property> UpdateAsync(Property property);
    Task DeleteAsync(Guid id);
    Task<bool> ExistsAsync(Guid id);

    /// <summary>
    /// OrgId of the property (under the caller's tenant filter), or <c>null</c> when it does not exist.
    /// Child rows (documents, OTA integrations, pricing) copy it on creation (TN-2).
    /// </summary>
    Task<Guid?> GetOrgIdAsync(Guid id);
    Task<Property?> GetPropertyDetailAsync(Guid id);

    /// <summary>
    /// Properties of <see cref="HostScope.OrgId"/> for the CIN compliance summary, restricted to
    /// <see cref="HostScope.OwnerId"/> when set (TN-3 list filter, in SQL), by name. Same reach as the property list
    /// (MO-12, A6-20): an org-wide role sees the CIN of every property of the org.
    /// </summary>
    Task<IEnumerable<Property>> GetByScopeForComplianceAsync(HostScope scope);
    Task<bool> CinCodeExistsOnOtherPropertyAsync(string cinCode, Guid excludePropertyId);
    Task<bool> SlugExistsInOrgAsync(Guid orgId, string slug, Guid? excludePropertyId = null);

    /// <summary>The cancellation policies a property can reference (global catalog), by name.</summary>
    Task<IReadOnlyList<CancellationPolicy>> GetCancellationPoliciesAsync();

    Task<bool> CancellationPolicyExistsAsync(Guid id);
}