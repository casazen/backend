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

    /// <summary>
    /// Soft-deletes the property (PC-05, A2-18): sets <see cref="Property.IsDeleted"/> and
    /// <see cref="Property.DeletedAt"/> instead of removing the row, so its fiscal history (tourist tax, CIN,
    /// cedolare secca) stays intact. A no-op when the property does not exist or is already deleted. Callers must
    /// check <see cref="HasUpcomingConfirmedBookingsAsync"/> first: this method does not enforce that rule.
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// True when the property has a confirmed or checked-in stay whose check-out is today (Europe/Rome) or later
    /// (<see cref="Casazen.Core.Services.StayKpiRules.UpcomingCheckOut"/>, PC-05): a deletion must be refused while
    /// one exists, so a guest already booked never loses their stay.
    /// </summary>
    Task<bool> HasUpcomingConfirmedBookingsAsync(Guid propertyId, DateTime todayInRome, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id);

    /// <summary>
    /// OrgId of the property (under the caller's tenant filter), or <c>null</c> when it does not exist.
    /// Child rows (documents, OTA integrations, pricing) copy it on creation (TN-2).
    /// </summary>
    Task<Guid?> GetOrgIdAsync(Guid id);
    Task<Property?> GetPropertyDetailAsync(Guid id);
    Task<IEnumerable<Property>> GetByOwnerForComplianceAsync(string ownerId);
    Task<bool> CinCodeExistsOnOtherPropertyAsync(string cinCode, Guid excludePropertyId);
    Task<bool> SlugExistsInOrgAsync(Guid orgId, string slug, Guid? excludePropertyId = null);

    /// <summary>The cancellation policies a property can reference (global catalog), by name.</summary>
    Task<IReadOnlyList<CancellationPolicy>> GetCancellationPoliciesAsync();

    Task<bool> CancellationPolicyExistsAsync(Guid id);
}