using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The images of the supplier's showcase (SP-13b, <c>api/supplier/showcase/media</c>) on <see cref="SupplierShowcaseMedia"/> and the public bucket
/// of <see cref="IFileStorage"/>: the logo, the cover and the photos of the jobs, and the daily cleanup of the ones nobody uses.
/// </summary>
/// <remarks>
/// <para><b>An upload is validated entirely before anything is stored.</b> The kind, the size of the file, its type (read from its first bytes,
/// never from its name or its declared type), the rewriting without metadata (<see cref="ShowcaseImageSanitizer"/>) and the size of the picture
/// all come first; the object is then stored under a random name outside the lock, the row is added under the supplier's content lock with the
/// limit checked again, and the object of a refused or failed upload is removed again.</para>
/// <para><b>Tenancy.</b> The table is keyed by the supplier org and is not tenant-filtered (allow-list of the TN-2 test): every statement here
/// carries the explicit <c>OrgId</c> predicate (<see cref="Rows"/>), except the pass of the cleanup, which is a system job over the rows that
/// are old enough and then judges each supplier alone, under its lock.</para>
/// <para><b>The cleanup never removes a file in use.</b> Whether a file is in use is asked of <see cref="ISupplierShowcaseContentService"/>, which looks
/// for it in the draft, the published version and the version published before (the undo of five minutes), under the same lock a save takes.
/// A file that is used is kept whatever its age; one that is not is removed only once it is older than
/// <see cref="ShowcaseMediaLimits.OrphanGrace"/>, the time to save it into the draft. And since a save accepts only the images the upload stored
/// (their rows), a stale editor that points to a removed file gets a 422 and not a broken picture.</para>
/// <para><b>What it keeps out of the logs.</b> Ids and counts, never the name of an uploaded file.</para>
/// </remarks>
public sealed class SupplierShowcaseMediaService(
    AppDbContext db,
    IFileStorage storage,
    ISupplierShowcaseContentService content,
    ILogger<SupplierShowcaseMediaService> logger,
    TimeProvider? timeProvider = null) : ISupplierShowcaseMediaService
{
    internal const string RunLockKey = "supplier-showcase-media-cleanup";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseMediaItem> UploadAsync(
        Guid supplierOrgId,
        string? kind,
        IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        if (!ShowcaseMediaKinds.TryParse(kind, out var mediaKind))
            throw ShowcaseMediaErrors.BadKind();

        if (file is null || file.Length == 0)
            throw ShowcaseMediaErrors.NoFile();

        if (file.Length > ShowcaseMediaKinds.MaxBytesOf(mediaKind))
            throw ShowcaseMediaErrors.Large(mediaKind);

        var original = new byte[file.Length];
        await using (var input = file.OpenReadStream())
            await input.ReadExactlyAsync(original, cancellationToken);

        var type = ImageSignature.Detect(original.AsSpan(0, Math.Min(original.Length, ImageSignature.HeaderLength)));
        if (type is null)
            throw ShowcaseMediaErrors.WrongType();

        var clean = ShowcaseImageSanitizer.Clean(original, type) ?? throw ShowcaseMediaErrors.NotAnImage();

        (int Width, int Height)? measured;
        await using (var stream = new MemoryStream(clean, writable: false))
            measured = await ImageSizeReader.ReadAsync(stream, cancellationToken);

        if (measured is not { } size)
            throw ShowcaseMediaErrors.NotAnImage();

        if (size.Width > ShowcaseMediaLimits.MaxSidePixels
            || size.Height > ShowcaseMediaLimits.MaxSidePixels
            || (long)size.Width * size.Height > ShowcaseMediaLimits.MaxPixels)
            throw ShowcaseMediaErrors.TooManyPixels();

        if (await Rows(supplierOrgId).CountAsync(cancellationToken) >= ShowcaseMediaLimits.MaxStoredFiles)
            throw ShowcaseMediaErrors.TooManyFiles();

        var id = Guid.NewGuid();
        var extension = type switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg",
        };
        var key = StorageKeys.SupplierShowcaseImage(supplierOrgId, $"{mediaKind}-{id:N}{extension}");

        await using (var stored = new MemoryStream(clean, writable: false))
            await storage.PutAsync(StorageBucket.Public, key, stored, type, cancellationToken);

        try
        {
            await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

            if (await Rows(supplierOrgId).CountAsync(cancellationToken) >= ShowcaseMediaLimits.MaxStoredFiles)
                throw ShowcaseMediaErrors.TooManyFiles();

            var row = new SupplierShowcaseMedia
            {
                Id = id,
                OrgId = supplierOrgId,
                Kind = mediaKind,
                StorageKey = key,
                ContentType = type,
                Bytes = clean.Length,
                Width = size.Width,
                Height = size.Height,
                CreatedAt = Now(),
            };
            db.SupplierShowcaseMedia.Add(row);
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Supplier {OrgId}: showcase image stored (kind {Kind}, {Bytes} bytes)", supplierOrgId, mediaKind, clean.Length);
            return ToItem(row);
        }
        catch
        {
            await RemoveObjectAsync(key);
            throw;
        }
    }

    public async Task DeleteAsync(Guid supplierOrgId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

        var row = await Rows(supplierOrgId).FirstOrDefaultAsync(m => m.Id == mediaId, cancellationToken)
                  ?? throw ShowcaseMediaErrors.Missing();

        if ((await content.FindKeysInUseAsync(supplierOrgId, [row.StorageKey], cancellationToken)).Count > 0)
            throw ShowcaseMediaErrors.Used();

        // The object first: if the storage fails the row stays and the supplier can try again, instead of leaving an object nobody can reach.
        await storage.DeleteAsync(StorageBucket.Public, row.StorageKey, cancellationToken);
        db.SupplierShowcaseMedia.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Supplier {OrgId}: showcase image removed", supplierOrgId);
    }

    public async Task<ShowcaseMediaCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.SupplierShowcaseMediaCleanupRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Showcase media cleanup skipped: another run is in progress");
            return ShowcaseMediaCleanupResult.SkippedRun;
        }

        var cutoff = Now() - ShowcaseMediaLimits.OrphanGrace;

        var orgIds = await db.SupplierShowcaseMedia
            .AsNoTracking()
            .Where(m => m.CreatedAt < cutoff)
            .Select(m => m.OrgId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var removed = 0;
        foreach (var orgId in orgIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (removed >= ShowcaseMediaLimits.CleanupBatch)
                break;

            removed += await CleanOrgAsync(orgId, cutoff, cancellationToken);
        }

        if (removed > 0)
            logger.LogInformation("Showcase media cleanup: {Removed} files nobody uses removed", removed);

        return new ShowcaseMediaCleanupResult(false, removed);
    }

    /// <summary>
    /// Removes the files of one supplier that are older than <paramref name="cutoff"/> and that the content does not point to, under the
    /// supplier's content lock; returns how many. A supplier that cannot be cleaned (the storage fails) is left for the next run and never
    /// stops the others.
    /// </summary>
    private async Task<int> CleanOrgAsync(Guid orgId, DateTime cutoff, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await LockAsync(orgId, cancellationToken);
            db.ChangeTracker.Clear();

            var old = await Rows(orgId)
                .Where(m => m.CreatedAt < cutoff)
                .OrderBy(m => m.CreatedAt)
                .Take(ShowcaseMediaLimits.CleanupBatch)
                .ToListAsync(cancellationToken);
            if (old.Count == 0)
                return 0;

            var inUse = await content.FindKeysInUseAsync(orgId, [.. old.Select(m => m.StorageKey)], cancellationToken);

            var gone = new List<SupplierShowcaseMedia>();
            foreach (var row in old.Where(m => !inUse.Contains(m.StorageKey)))
            {
                try
                {
                    await storage.DeleteAsync(StorageBucket.Public, row.StorageKey, cancellationToken);
                    gone.Add(row);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Showcase media cleanup: a file could not be removed and is left. OrgId={OrgId}", orgId);
                }
            }

            if (gone.Count == 0)
                return 0;

            db.SupplierShowcaseMedia.RemoveRange(gone);
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return gone.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Showcase media cleanup: a supplier could not be cleaned and is left for the next run. OrgId={OrgId}", orgId);
            return 0;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private IQueryable<SupplierShowcaseMedia> Rows(Guid orgId) => db.SupplierShowcaseMedia.Where(m => m.OrgId == orgId);

    private Task<IDbContextTransaction?> LockAsync(Guid orgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SupplierShowcaseContent, orgId.ToString("N")));

    private ShowcaseMediaItem ToItem(SupplierShowcaseMedia row) =>
        new(row.Id, row.Kind, storage.GetPublicUrl(row.StorageKey), row.ContentType, row.Bytes, row.Width, row.Height, row.CreatedAt);

    private async Task RemoveObjectAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Public, key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the object of a showcase image that was not added");
        }
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
