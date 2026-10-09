using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>
/// The supplier's catalog of services with prices (SP-02, <c>api/supplier/services</c>): create, read, replace, delete,
/// publish, pause, duplicate and add photos to the <see cref="SupplierServiceListing"/>s of one supplier org.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> Every method takes the supplier org id (resolved from the caller's own supplier link, never from
/// the request) and reads and writes only the rows of that org: the table is not tenant-filtered (it is keyed by the
/// supplier org, see <see cref="SupplierServiceListing"/>), so every query carries an explicit <c>OrgId</c> predicate. A
/// service of another supplier, or a deleted one, is 404 <see cref="SupplierServiceCatalogErrors.NotFound"/>.</para>
/// <para><b>Errors.</b> <see cref="Exceptions.NotFoundException"/> (404), <see cref="Exceptions.DomainRuleException"/> (422,
/// codes of <see cref="SupplierServiceCatalogErrors"/> and <c>invalid_service_category</c>) and
/// <see cref="Exceptions.DomainConflictException"/> (409 <c>supplier_service_changed</c>: the service was changed since
/// the client read it).</para>
/// <para><b>Serialization.</b> The changes of one supplier's catalog run one at a time (PostgreSQL advisory lock per
/// supplier org), so the limit of services, the slugs and the photo list are never decided on a stale read.</para>
/// </remarks>
public interface ISupplierServiceCatalogService
{
    /// <summary>The services of the supplier that are not deleted, by sort position then creation. Read-only.</summary>
    Task<IReadOnlyList<SupplierServiceListing>> ListAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>One service of the supplier. Read-only.</summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>.</exception>
    Task<SupplierServiceListing> GetAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The service as a service request needs it (SP-04), whatever its status; <c>null</c> when it is not one of the supplier's
    /// or was deleted. The caller decides what a draft or paused service means (<see cref="SupplierServiceForRequest.IsRequestable"/>).
    /// Read-only.
    /// </summary>
    Task<SupplierServiceForRequest?> FindForRequestAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>How many published (<c>Active</c>) services the supplier has: a step of the console checklist (SP-04). Read-only.</summary>
    Task<int> CountActiveAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The services the public may see (SP-09): the <c>Active</c> ones, not deleted, of <paramref name="supplierOrgId"/> <b>when
    /// that supplier is itself <c>Active</c></b> (the supplier's status is part of the same statement: an org that is not an
    /// active supplier has none, whatever the caller checked). By position then creation date. Only
    /// <see cref="SupplierPublicService"/>, which carries nothing the console keeps to itself. Read-only; for the anonymous
    /// reads, the org having been found from an active supplier's showcase slug.
    /// </summary>
    Task<IReadOnlyList<SupplierPublicService>> ListPublicAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One service the public may see (<see cref="ListPublicAsync"/> rules) by its slug (lowercase, as stored); <c>null</c> for
    /// an unknown slug, a draft, a paused, a deleted service, a service of another supplier and a supplier that is not active.
    /// Read-only.
    /// </summary>
    Task<SupplierPublicService?> FindPublicAsync(Guid supplierOrgId, string serviceSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same service as <see cref="FindPublicAsync"/>, found by the same statement and under the same rules, with the id of
    /// the catalog row (SP-10: the booking from the showcase keeps it on the request). <c>null</c> for an unknown slug, a draft,
    /// a paused, a deleted service, a service of another supplier and a supplier that is not active. Read-only. Only the booking
    /// service asks for it: a public read never carries an id.
    /// </summary>
    Task<SupplierBookableService?> FindBookableAsync(Guid supplierOrgId, string serviceSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a service as a draft: the slug comes from the name (unique among the supplier's services), the position is
    /// the last one unless the input gives it. Photos are not part of it (<see cref="AddPhotosAsync"/>).
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <c>invalid_service_category</c>; <c>supplier_service_invalid</c> (names the fields, also <c>photoUrls</c> when the
    /// input has some); <c>supplier_service_limit_reached</c>.
    /// </exception>
    Task<SupplierServiceListing> CreateAsync(
        Guid supplierOrgId,
        SupplierServiceListingInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the content of a service (every field of <paramref name="input"/>; the photos and the position only when
    /// the input carries them). <paramref name="version"/> is the version the client read: another one is a 409. The slug,
    /// the status and the creation date stay, except that a draft's slug follows a change of its name. A published
    /// service must still meet the publication requirements.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <c>invalid_service_category</c>; <c>supplier_service_invalid</c> (also a photo that is not one of the service's);
    /// <c>supplier_service_not_publishable</c> for a published service.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException"><c>supplier_service_changed</c>.</exception>
    Task<SupplierServiceListing> UpdateAsync(
        Guid supplierOrgId,
        Guid id,
        uint version,
        SupplierServiceListingInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Soft delete: the service disappears from every read and its slug is free again.</summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>, also when it is already deleted.</exception>
    Task DeleteAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a draft or paused service (<see cref="SupplierServiceListingRules.EnsurePublishable"/>); an active one is
    /// returned as it is.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><c>supplier_service_not_publishable</c> (names what is missing).</exception>
    Task<SupplierServiceListing> PublishAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Pauses an active service; a paused one is returned as it is.</summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><c>supplier_service_cannot_pause</c>: a draft is not published.</exception>
    Task<SupplierServiceListing> PauseAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// A draft copy of a service: the same content and photos, a new slug, the name followed by
    /// <paramref name="copyNameSuffix"/> (shortened to fit), the last position.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><c>supplier_service_limit_reached</c>.</exception>
    Task<SupplierServiceListing> DuplicateAsync(
        Guid supplierOrgId,
        Guid id,
        string copyNameSuffix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores <paramref name="files"/> (all or none: JPEG, PNG or WebP checked on their content, each at most
    /// <see cref="SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes"/>) and appends them to the service's photos. Returns
    /// the service after the change (its version changed).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>supplier_service_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <c>supplier_service_photo_none</c>, <c>supplier_service_photo_invalid_type</c>,
    /// <c>supplier_service_photo_invalid_size</c> or <c>supplier_service_photo_limit_reached</c>.
    /// </exception>
    Task<SupplierServiceListing> AddPhotosAsync(
        Guid supplierOrgId,
        Guid id,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken = default);
}
