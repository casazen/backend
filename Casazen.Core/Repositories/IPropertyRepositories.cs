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
    /// cedolare secca) stays intact. Refused, changing nothing, while the property has a stay still to come (pending,
    /// confirmed or checked-in, check-out on <paramref name="todayInRome"/> or later) or a lease in force (not draft
    /// nor rejected, end date on <paramref name="todayInRome"/> or later). On PostgreSQL the check and the update run
    /// under the property's booking lock, so a booking created at the same time is either seen or waits.
    /// </summary>
    /// <param name="deletedAtUtc">UTC instant recorded in <see cref="Property.DeletedAt"/>.</param>
    /// <param name="todayInRome">Today's calendar date in Europe/Rome (stay dates are calendar dates).</param>
    Task<PropertySoftDeleteOutcome> SoftDeleteAsync(
        Guid id,
        DateTime deletedAtUtc,
        DateTime todayInRome,
        CancellationToken cancellationToken = default);

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

/// <summary>Result of <see cref="IPropertyRepository.SoftDeleteAsync"/> (PC-05).</summary>
public enum PropertySoftDeleteOutcome
{
    /// <summary>The property is now soft-deleted.</summary>
    Deleted,

    /// <summary>No such property for the caller (other org, never existed, or already deleted): nothing changed.</summary>
    NotFound,

    /// <summary>A pending, confirmed or checked-in stay has not checked out yet: nothing changed.</summary>
    HasUpcomingStays,

    /// <summary>A lease is in force or in progress (not draft nor rejected) and has not ended yet: nothing changed.</summary>
    HasActiveLeases,
}
