using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public sealed record PropertySiteMedia(IReadOnlyList<PropertyFloorPlan> FloorPlans, IReadOnlyList<PropertyPoi> Pois);

public interface IPropertyRepository
{
    Task<Property?> GetByIdAsync(Guid id);

    Task<Property?> GetRecordAsync(Guid id);

    /// <summary>
    /// The floor plan levels (lowest floor first) and the points of interest (in the host's order) of one property, for its public page
    /// (DB-11a). The caller has already resolved the property as published; the tenant filter is ignored and the property named, so a signed-in
    /// host of another org reads what everybody reads.
    /// </summary>
    Task<PropertySiteMedia> GetSiteMediaAsync(Guid propertyId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Property>> GetByOwnerAsync(string ownerId);

    Task<IEnumerable<Property>> GetByScopeAsync(HostScope scope);

    Task<IEnumerable<Property>> GetByScopeAsync(HostScope scope, RentalMode mode);
    Task<IEnumerable<Property>> GetAllAsync();
    Task<IEnumerable<Property>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice);
    IQueryable<Property> GetSearchQueryable(PublicPropertySearchCriteria criteria, Guid? orgId = null);
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

    Task<Guid?> GetOrgIdAsync(Guid id);
    Task<Property?> GetPropertyDetailAsync(Guid id);

    /// <summary>
    /// Short-rent properties (<see cref="RentalMode.Short"/>, PM-01: a long-term property has no CIN obligation) of
    /// <see cref="HostScope.OrgId"/> for the CIN compliance summary, restricted to
    /// <see cref="HostScope.OwnerId"/> when set (TN-3 list filter, in SQL), by name. Same reach as the property list
    /// (MO-12, A6-20): an org-wide role sees the CIN of every short-rent property of the org.
    /// </summary>
    Task<IEnumerable<Property>> GetByScopeForComplianceAsync(HostScope scope);
    Task<bool> CinCodeExistsOnOtherPropertyAsync(string cinCode, Guid excludePropertyId);
    Task<bool> SlugExistsInOrgAsync(Guid orgId, string slug, Guid? excludePropertyId = null);

    Task<IReadOnlyList<CancellationPolicy>> GetCancellationPoliciesAsync();

    Task<bool> CancellationPolicyExistsAsync(Guid id);
}

public enum PropertySoftDeleteOutcome
{
    Deleted,

    NotFound,

    HasUpcomingStays,

    HasActiveLeases,
}
