using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Deletes the photos of identity documents that an identity verification kept (E03/F04/US02/T04, ADR-013-R7): the encrypted object in the private
/// bucket <b>and</b> its <see cref="DocumentImage"/> row. The one place, used by the receipt of the Alloggiati communication
/// (<c>AlloggiatiReceiptJob</c>) and by the erasure of a guest (<see cref="GuestDataEraser"/>, art. 17 GDPR). The objects are deleted at once,
/// before the caller saves, so a failure of the storage leaves the rows in place and the operation can be repeated; the rows are staged for removal.
/// </summary>
public static class DocumentImageCleaner
{
    /// <summary>Deletes the objects of <paramref name="images"/> and stages their rows for removal; returns the objects deleted.</summary>
    public static async Task<int> DeleteAsync(
        AppDbContext db,
        IFileStorage storage,
        ILogger logger,
        IReadOnlyCollection<DocumentImage> images,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(images);

        var objects = 0;
        foreach (var image in images)
        {
            objects += await DeleteObjectAsync(storage, logger, image.Id, image.FrontImageUrl, ct);
            objects += await DeleteObjectAsync(storage, logger, image.Id, image.BackImageUrl, ct);
            db.DocumentImages.Remove(image);
        }

        return objects;
    }

    private static async Task<int> DeleteObjectAsync(IFileStorage storage, ILogger logger, Guid imageId, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return 0;

        if (!StorageKeys.IsValid(key))
        {
            // A reference that is not a storage key (never written by the flow): the row goes, there is nothing to delete.
            logger.LogWarning("Document image {ImageId} does not point to a storage key: row removed, nothing deleted from the storage", imageId);
            return 0;
        }

        await storage.DeleteAsync(StorageBucket.Private, key, ct);
        return 1;
    }
}
