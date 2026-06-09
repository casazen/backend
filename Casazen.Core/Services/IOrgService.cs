using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Read access to <see cref="Org"/> tenants. US-004 is read-only for org;
/// org management / plan switching is owned by <c>spec-saas-billing</c>.
/// </summary>
public interface IOrgService
{
    /// <summary>Returns the org by id, or <c>null</c> if not found.</summary>
    Task<Org?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the org the given user belongs to, or <c>null</c> if the user has none.</summary>
    Task<Org?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the org by slug for public (branded) surfaces. Referenced by later specs
    /// (<c>spec-branded-booking-site</c>); not exposed via an endpoint in US-004.
    /// </summary>
    Task<Org?> GetPublicBySlugAsync(string slug, CancellationToken cancellationToken = default);
}
