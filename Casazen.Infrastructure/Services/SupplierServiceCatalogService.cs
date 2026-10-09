using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The supplier's catalog of services with prices (SP-02, <c>api/supplier/services</c>) on
/// <see cref="SupplierServiceListing"/>.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> The table is keyed by the supplier org and is <b>not</b> tenant-filtered (the TN-2 allow-list says
/// why: a supplier-only account has no <c>User.OrgId</c>). Every read and write here goes through
/// <see cref="Listings"/>, which carries the explicit <c>OrgId</c> predicate; the only other statement is the insert of a
/// row that has its <c>OrgId</c> set. An architecture test forbids any other code from using the table.</para>
/// <para><b>Serialization.</b> Every change takes the supplier's catalog lock
/// (<see cref="PostgresAdvisoryLocks.Scope.SupplierServiceCatalog"/>) and reads the row after taking it, so two requests
/// never both see room for the thirtieth service, never choose the same slug, and never overwrite each other's photo
/// list. On top of that, an update carries the version the client read (<c>xmin</c>) and a stale one is a 409.</para>
/// <para><b>Photos.</b> The objects live in the public bucket under <c>suppliers/{orgId}/photos/</c>
/// (<see cref="IImageStorageService.UploadSupplierPhotoAsync"/>). An upload is validated entirely before anything is
/// stored (all or none) and the objects of a failed upload are removed again. A photo that leaves a service is deleted
/// from the bucket only when it is in the supplier's own folder and no other service of the supplier (duplicates share
/// the objects) still uses it.</para>
/// </remarks>
public class SupplierServiceCatalogService(
    AppDbContext db,
    IFileStorage storage,
    IImageStorageService images,
    ILogger<SupplierServiceCatalogService> logger,
    TimeProvider? timeProvider = null) : ISupplierServiceCatalogService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<SupplierServiceListing>> ListAsync(
        Guid supplierOrgId,
        CancellationToken cancellationToken = default) =>
        await Listings(supplierOrgId)
            .AsNoTracking()
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);

    public async Task<SupplierServiceListing> GetAsync(
        Guid supplierOrgId,
        Guid id,
        CancellationToken cancellationToken = default) =>
        await Listings(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
        ?? throw SupplierServiceCatalogErrors.ServiceNotFound(id);

    public async Task<SupplierServiceForRequest?> FindForRequestAsync(
        Guid supplierOrgId,
        Guid id,
        CancellationToken cancellationToken = default) =>
        await Listings(supplierOrgId)
            .AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new SupplierServiceForRequest(
                l.Id,
                l.Name,
                l.Category,
                l.Status,
                l.DurationMinutes,
                l.MinNoticeHours,
                l.WeekdaysMask,
                l.PriceFromCents,
                l.PriceUnit,
                l.RequiresQuote,
                l.Slug))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int> CountActiveAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        await Listings(supplierOrgId).CountAsync(l => l.Status == SupplierServiceListingStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<SupplierPublicService>> ListPublicAsync(
        Guid supplierOrgId,
        CancellationToken cancellationToken = default) =>
        (await PublicListingsOf(db, supplierOrgId)
            .AsNoTracking()
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .ToListAsync(cancellationToken))
        .Select(ToPublic)
        .ToList();

    public async Task<SupplierPublicService?> FindPublicAsync(
        Guid supplierOrgId,
        string serviceSlug,
        CancellationToken cancellationToken = default)
    {
        var listing = await PublicListingsOf(db, supplierOrgId)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Slug == serviceSlug, cancellationToken);
        return listing is null ? null : ToPublic(listing);
    }

    public async Task<SupplierBookableService?> FindBookableAsync(
        Guid supplierOrgId,
        string serviceSlug,
        CancellationToken cancellationToken = default)
    {
        // The same statement as FindPublicAsync: the booking may use only what the public may see, with the id it needs to keep.
        var listing = await PublicListingsOf(db, supplierOrgId)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Slug == serviceSlug, cancellationToken);
        return listing is null ? null : new SupplierBookableService(listing.Id, ToPublic(listing));
    }

    public async Task<SupplierServiceListing> CreateAsync(
        Guid supplierOrgId,
        SupplierServiceListingInput input,
        CancellationToken cancellationToken = default)
    {
        var content = SupplierServiceListingRules.Normalize(input);

        // A new service has no photos: they are uploaded afterwards, so the URLs of the input cannot be the service's own.
        if (content.PhotoUrls is { Count: > 0 })
            throw SupplierServiceCatalogErrors.InvalidFields([SupplierServiceFields.PhotoUrls]);

        await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);

        await EnsureRoomForAnotherServiceAsync(supplierOrgId, cancellationToken);

        var now = Now();
        var listing = new SupplierServiceListing
        {
            OrgId = supplierOrgId,
            Slug = await NextSlugAsync(supplierOrgId, content.Name, exceptId: null, cancellationToken),
            Status = SupplierServiceListingStatus.Draft,
            SortOrder = content.SortOrder ?? await NextSortOrderAsync(supplierOrgId, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        content.ApplyTo(listing);

        db.SupplierServiceListings.Add(listing);
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: service {ServiceId} created as a draft", supplierOrgId, listing.Id);
        return listing;
    }

    public async Task<SupplierServiceListing> UpdateAsync(
        Guid supplierOrgId,
        Guid id,
        uint version,
        SupplierServiceListingInput input,
        CancellationToken cancellationToken = default)
    {
        var content = SupplierServiceListingRules.Normalize(input);

        await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);
        var listing = await FindAsync(supplierOrgId, id, cancellationToken);

        // The version the client read must be the current one: another tab, another member of the supplier or a photo
        // upload changed the service since, and this replacement would silently undo it.
        if (listing.Version != version)
        {
            logger.LogInformation("Supplier {OrgId}: update of service {ServiceId} refused, the version is stale", supplierOrgId, id);
            throw SupplierServiceCatalogErrors.ServiceChanged();
        }

        var removedPhotos = new List<string>();
        if (content.PhotoUrls is not null)
        {
            // Only photos the service already has, in the order sent: this is how a photo is removed or moved, never how a
            // URL that was not uploaded here gets into a public page.
            var current = SupplierServiceListingJson.ReadStrings(listing.PhotoUrlsJson);
            if (content.PhotoUrls.Any(url => !current.Contains(url, StringComparer.Ordinal)))
                throw SupplierServiceCatalogErrors.InvalidFields([SupplierServiceFields.PhotoUrls]);

            removedPhotos.AddRange(current.Where(url => !content.PhotoUrls.Contains(url, StringComparer.Ordinal)));
            listing.PhotoUrlsJson = SupplierServiceListingJson.Serialize(content.PhotoUrls);
        }

        content.ApplyTo(listing);
        if (content.SortOrder is { } sortOrder)
            listing.SortOrder = sortOrder;

        // A draft was never public, so its slug may follow its name; the slug of a published or paused service never changes.
        if (listing.Status == SupplierServiceListingStatus.Draft
            && !SupplierServiceListingRules.SlugFollowsName(listing.Slug, SupplierServiceListingRules.SlugFromName(listing.Name)))
        {
            listing.Slug = await NextSlugAsync(supplierOrgId, listing.Name, listing.Id, cancellationToken);
        }

        // A published service shown to customers stays complete: an edit cannot take that away.
        if (listing.Status == SupplierServiceListingStatus.Active)
            SupplierServiceListingRules.EnsurePublishable(listing);

        listing.UpdatedAt = Now();
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        await DeleteUnreferencedPhotosAsync(supplierOrgId, listing.Id, removedPhotos);
        logger.LogInformation("Supplier {OrgId}: service {ServiceId} updated", supplierOrgId, listing.Id);
        return listing;
    }

    public async Task DeleteAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);
        var listing = await FindAsync(supplierOrgId, id, cancellationToken);

        var now = Now();
        listing.DeletedAt = now;
        listing.UpdatedAt = now;
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: service {ServiceId} deleted (soft)", supplierOrgId, id);
    }

    public async Task<SupplierServiceListing> PublishAsync(
        Guid supplierOrgId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);
        var listing = await FindAsync(supplierOrgId, id, cancellationToken);

        if (listing.Status == SupplierServiceListingStatus.Active)
            return listing;

        SupplierServiceListingRules.EnsurePublishable(listing);
        listing.Status = SupplierServiceListingStatus.Active;
        listing.UpdatedAt = Now();
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: service {ServiceId} published", supplierOrgId, id);
        return listing;
    }

    public async Task<SupplierServiceListing> PauseAsync(
        Guid supplierOrgId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);
        var listing = await FindAsync(supplierOrgId, id, cancellationToken);

        if (listing.Status == SupplierServiceListingStatus.Paused)
            return listing;
        if (listing.Status == SupplierServiceListingStatus.Draft)
            throw new DomainRuleException(SupplierServiceCatalogErrors.CannotPause, "SupplierServiceCannotPause");

        listing.Status = SupplierServiceListingStatus.Paused;
        listing.UpdatedAt = Now();
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: service {ServiceId} paused", supplierOrgId, id);
        return listing;
    }

    public async Task<SupplierServiceListing> DuplicateAsync(
        Guid supplierOrgId,
        Guid id,
        string copyNameSuffix,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);
        var source = await Listings(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw SupplierServiceCatalogErrors.ServiceNotFound(id);

        await EnsureRoomForAnotherServiceAsync(supplierOrgId, cancellationToken);

        var name = SupplierServiceListingRules.CopyName(source.Name, copyNameSuffix);
        var now = Now();
        var copy = new SupplierServiceListing
        {
            OrgId = supplierOrgId,
            Slug = await NextSlugAsync(supplierOrgId, name, exceptId: null, cancellationToken),
            Name = name,
            Category = source.Category,
            Summary = source.Summary,
            Description = source.Description,
            PriceFromCents = source.PriceFromCents,
            PriceUnit = source.PriceUnit,
            PricesIncludeVat = source.PricesIncludeVat,
            RequiresQuote = source.RequiresQuote,
            DurationMinutes = source.DurationMinutes,
            MinNoticeHours = source.MinNoticeHours,
            WeekdaysMask = source.WeekdaysMask,
            SupplementsJson = source.SupplementsJson,
            IncludedJson = source.IncludedJson,
            ExcludedJson = source.ExcludedJson,
            // The copy shares the photo objects with the original (a photo is deleted from the bucket only when no service uses it).
            PhotoUrlsJson = source.PhotoUrlsJson,
            Status = SupplierServiceListingStatus.Draft,
            SortOrder = await NextSortOrderAsync(supplierOrgId, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.SupplierServiceListings.Add(copy);
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation(
            "Supplier {OrgId}: service {ServiceId} duplicated as the draft {CopyId}", supplierOrgId, id, copy.Id);
        return copy;
    }

    public async Task<SupplierServiceListing> AddPhotosAsync(
        Guid supplierOrgId,
        Guid id,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new DomainRuleException(SupplierServiceCatalogErrors.PhotoNone, "SupplierServicePhotoNone");

        // Nothing is stored unless every file is acceptable.
        foreach (var file in files)
            await ValidatePhotoAsync(file, cancellationToken);

        // The early answer, before uploading anything: the service exists and has room (checked again under the lock).
        var existing = await Listings(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw SupplierServiceCatalogErrors.ServiceNotFound(id);
        EnsurePhotoRoom(SupplierServiceListingJson.ReadStrings(existing.PhotoUrlsJson).Count, files.Count);

        var uploaded = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
                uploaded.Add(await images.UploadSupplierPhotoAsync(file, supplierOrgId));

            await using var transaction = await LockCatalogAsync(supplierOrgId, cancellationToken);
            var listing = await FindAsync(supplierOrgId, id, cancellationToken);

            var photos = SupplierServiceListingJson.ReadStrings(listing.PhotoUrlsJson);
            EnsurePhotoRoom(photos.Count, uploaded.Count);

            listing.PhotoUrlsJson = SupplierServiceListingJson.Serialize(photos.Concat(uploaded));
            listing.UpdatedAt = Now();
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            logger.LogInformation(
                "Supplier {OrgId}: {Count} photo(s) added to service {ServiceId}", supplierOrgId, uploaded.Count, id);
            return listing;
        }
        catch
        {
            await RemoveObjectsAsync(supplierOrgId, uploaded);
            throw;
        }
    }

    /// <summary>
    /// The services of <paramref name="orgId"/> that are not deleted. <b>The only way</b> this service reads or changes a
    /// row: the table is not tenant-filtered, so the supplier org is always an explicit predicate.
    /// </summary>
    private IQueryable<SupplierServiceListing> Listings(Guid orgId) => ListingsOf(db, orgId);

    /// <inheritdoc cref="Listings"/>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<SupplierServiceListing> ListingsOf(AppDbContext db, Guid orgId) =>
        db.SupplierServiceListings.Where(l => l.OrgId == orgId && l.DeletedAt == null);

    /// <summary>
    /// The services the public may see (SP-09): <see cref="ListingsOf"/> (the supplier org, not deleted) that are <c>Active</c>,
    /// of a supplier whose profile is itself <c>Active</c> (one statement, a join on the profile of the same org): the
    /// anonymous reads can never reach a draft, a paused service, a deleted one, another supplier's or a pending or suspended
    /// supplier's, even when the caller passes a wrong org.
    /// </summary>
    internal static IQueryable<SupplierServiceListing> PublicListingsOf(AppDbContext db, Guid orgId) =>
        ListingsOf(db, orgId).Where(l =>
            l.Status == SupplierServiceListingStatus.Active && l.SupplierProfile.Status == SupplierStatus.Active);

    /// <summary>The public form of a service: the only type that leaves the catalog toward an anonymous read.</summary>
    internal static SupplierPublicService ToPublic(SupplierServiceListing listing) =>
        new(
            listing.Slug,
            listing.Name,
            listing.Category,
            listing.Summary,
            listing.Description,
            listing.PriceFromCents,
            listing.PriceUnit,
            listing.PricesIncludeVat,
            listing.RequiresQuote,
            listing.DurationMinutes,
            SupplierServiceListingJson.ReadSupplements(listing.SupplementsJson),
            SupplierServiceListingJson.ReadStrings(listing.IncludedJson),
            SupplierServiceListingJson.ReadStrings(listing.ExcludedJson),
            SupplierServiceListingJson.ReadStrings(listing.PhotoUrlsJson),
            listing.MinNoticeHours,
            listing.WeekdaysMask);

    /// <summary>The slugs, among <paramref name="listings"/> other than <paramref name="except"/>, that start with <paramref name="baseSlug"/>.</summary>
    internal static IQueryable<string> SlugsStartingWith(IQueryable<SupplierServiceListing> listings, string baseSlug, Guid except) =>
        listings.Where(l => l.Id != except && l.Slug.StartsWith(baseSlug)).Select(l => l.Slug);

    /// <summary>The service tracked for change, read after the catalog lock was taken (this context has not read it before).</summary>
    private async Task<SupplierServiceListing> FindAsync(Guid orgId, Guid id, CancellationToken cancellationToken) =>
        await Listings(orgId).FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
        ?? throw SupplierServiceCatalogErrors.ServiceNotFound(id);

    private Task<IDbContextTransaction?> LockCatalogAsync(Guid orgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SupplierServiceCatalog, orgId.ToString("N")));

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Saves, turning a lost race into 409 <c>supplier_service_changed</c>: a concurrent change of the row (<c>xmin</c>) or
    /// the unique slug index. Under the catalog lock neither should happen; this is the guarantee if it does.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Supplier service not saved: the row changed since it was read");
            throw SupplierServiceCatalogErrors.ServiceChanged();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogWarning("Supplier service not saved: unique violation (slug)");
            throw SupplierServiceCatalogErrors.ServiceChanged();
        }
    }

    private async Task EnsureRoomForAnotherServiceAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var count = await Listings(orgId).CountAsync(cancellationToken);
        if (count >= SupplierServiceCatalogLimits.MaxServicesPerSupplier)
            throw new DomainRuleException(
                SupplierServiceCatalogErrors.LimitReached,
                "SupplierServiceLimitReached",
                SupplierServiceCatalogLimits.MaxServicesPerSupplier);
    }

    private async Task<int> NextSortOrderAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var last = await Listings(orgId).MaxAsync(l => (int?)l.SortOrder, cancellationToken);
        return Math.Min((last ?? -1) + 1, SupplierServiceCatalogLimits.MaxSortOrder);
    }

    /// <summary>
    /// The slug of <paramref name="name"/> that no other service of the supplier uses (<paramref name="exceptId"/> is the
    /// service being renamed, which does not collide with itself). Decided under the catalog lock.
    /// </summary>
    private async Task<string> NextSlugAsync(Guid orgId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var baseSlug = SupplierServiceListingRules.SlugFromName(name);
        var used = await SlugsStartingWith(Listings(orgId), baseSlug, exceptId ?? Guid.Empty).ToListAsync(cancellationToken);
        return SupplierServiceListingRules.NextFreeSlug(baseSlug, used.ToHashSet(StringComparer.Ordinal));
    }

    private static void EnsurePhotoRoom(int current, int adding)
    {
        if (current + adding > SupplierServiceCatalogLimits.MaxPhotos)
            throw new DomainRuleException(
                SupplierServiceCatalogErrors.PhotoLimitReached,
                "SupplierServicePhotoLimitReached",
                SupplierServiceCatalogLimits.MaxPhotos,
                current);
    }

    /// <summary>
    /// 422 for a file that is empty, too large, not JPEG/PNG/WebP by extension or declared type, or whose content is not
    /// the image it claims to be (the declared type and the extension are the client's word: the content decides).
    /// </summary>
    private async Task ValidatePhotoAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var name = DisplayName(file);
        if (file is null || file.Length == 0 || file.Length > SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes)
            throw new DomainRuleException(
                SupplierServiceCatalogErrors.PhotoInvalidSize,
                "SupplierServicePhotoInvalidSize",
                name,
                SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes / (1024 * 1024));

        if (!images.ValidateImage(file))
            throw InvalidPhotoType(name);

        await using var content = file.OpenReadStream();
        var detected = await ImageSignature.DetectAsync(content, cancellationToken);
        if (detected is null
            || !string.Equals(detected, file.ContentType, StringComparison.OrdinalIgnoreCase)
            || !ExtensionMatches(Path.GetExtension(file.FileName), detected))
        {
            throw InvalidPhotoType(name);
        }
    }

    private static bool ExtensionMatches(string extension, string contentType) =>
        (extension.ToLowerInvariant(), contentType) switch
        {
            (".jpg" or ".jpeg", "image/jpeg") => true,
            (".png", "image/png") => true,
            (".webp", "image/webp") => true,
            _ => false,
        };

    private static DomainRuleException InvalidPhotoType(string fileName) =>
        new(SupplierServiceCatalogErrors.PhotoInvalidType, "SupplierServicePhotoInvalidType", fileName);

    private static string DisplayName(IFormFile? file)
    {
        var name = Path.GetFileName(file?.FileName ?? string.Empty);
        return name.Length > 100 ? name[..100] : name;
    }

    /// <summary>
    /// After a photo left a service: deletes its object from the public bucket when it is in the supplier's own folder and
    /// no other service of the supplier, deleted ones included, still lists it. Best effort, after the commit: a storage
    /// failure leaves an object nobody lists, never a service without its photo list.
    /// </summary>
    private async Task DeleteUnreferencedPhotosAsync(Guid orgId, Guid listingId, IReadOnlyCollection<string> removed)
    {
        if (removed.Count == 0)
            return;

        try
        {
            // Every service of the supplier, the deleted ones too (still on file): the objects they list stay.
            var otherPhotos = await db.SupplierServiceListings
                .AsNoTracking()
                .Where(l => l.OrgId == orgId && l.Id != listingId)
                .Select(l => l.PhotoUrlsJson)
                .ToListAsync();
            var stillUsed = otherPhotos
                .SelectMany(SupplierServiceListingJson.ReadStrings)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var url in removed.Where(url => !stillUsed.Contains(url)))
                await DeleteObjectAsync(orgId, url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Supplier {OrgId}: could not remove the objects of the photos taken off service {ServiceId}", orgId, listingId);
        }
    }

    /// <summary>Best effort removal of objects whose upload did not end in a service; never hides the original error.</summary>
    private async Task RemoveObjectsAsync(Guid orgId, IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            try
            {
                await DeleteObjectAsync(orgId, url);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Supplier {OrgId}: could not remove the object of a photo that was not added", orgId);
            }
        }
    }

    /// <summary>
    /// Deletes the object of <paramref name="url"/> from the public bucket if it is in the supplier's own photo folder: a
    /// catalog can never delete what belongs to somebody else (a legacy relative path, an external URL).
    /// </summary>
    private async Task DeleteObjectAsync(Guid orgId, string url)
    {
        var key = storage.TryGetPublicKey(url);
        if (key is not null && key.StartsWith(StorageKeys.SupplierPhoto(orgId, string.Empty), StringComparison.Ordinal))
        {
            await storage.DeleteAsync(StorageBucket.Public, key);
            return;
        }

        logger.LogWarning(
            "Supplier {OrgId}: a photo URL is not an object of the supplier's own storage folder, left alone", orgId);
    }
}
