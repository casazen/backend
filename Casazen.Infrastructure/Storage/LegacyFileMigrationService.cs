using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Storage;

/// <summary>
/// One-off, idempotent migration of the files that the pre-FD-07 services wrote on the local disk
/// (<c>ImageStorage:LocalPath</c>, <c>GuestDocumentStorage:LocalPath</c>) into <see cref="IFileStorage"/>,
/// rewriting the database references:
/// <list type="bullet">
/// <item><c>Property.PhotoUrls</c> <c>/uploads/properties/{propertyId}/{file}</c> → public URL of <c>properties/{propertyId}/photos/{file}</c></item>
/// <item><c>SupplierProfile.PhotoUrlsJson</c> <c>/uploads/properties/{orgId}/{file}</c> → public URL of <c>suppliers/{orgId}/photos/{file}</c></item>
/// <item><c>PropertyDocument.StorageUrl</c> <c>/uploads/properties/{propertyId}/documents/{file}</c> → private key <c>properties/{propertyId}/documents/{file}</c></item>
/// <item><c>Guest.DocumentScanUrl</c> <c>/uploads/guest-documents/{orgId}/{guestId}/{file}</c> → private key <c>guest-documents/{orgId}/{guestId}/{file}</c></item>
/// </list>
/// Re-running it is safe: migrated references are no longer legacy paths, and objects already in the
/// storage are not uploaded again. References whose file is missing on disk and in the storage are
/// left unchanged and reported. Command: <c>dotnet Casazen.Web.dll storage:migrate-legacy [--dry-run]</c>.
/// Its queries use <c>IgnoreQueryFilters()</c>: a maintenance command over the files of every org (TN-2).
/// </summary>
public sealed class LegacyFileMigrationService(
    AppDbContext db,
    IFileStorage storage,
    IConfiguration configuration,
    ILogger<LegacyFileMigrationService> logger)
{
    public const string CommandName = "storage:migrate-legacy";
    public const string DryRunFlag = "--dry-run";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<LegacyFileMigrationReport> MigrateAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var report = new LegacyFileMigrationReport { DryRun = dryRun };
        var images = LegacyLocation.FromConfiguration(
            configuration, "ImageStorage", Path.Combine("wwwroot", "uploads", "properties"), "/uploads/properties");
        var guestDocuments = LegacyLocation.FromConfiguration(
            configuration, "GuestDocumentStorage", Path.Combine("uploads", "guest-documents"), "/uploads/guest-documents");

        logger.LogInformation(
            "Legacy file migration started (dry run: {DryRun}). Image root: {ImageRoot}; guest document root: {GuestRoot}",
            dryRun, images.Root, guestDocuments.Root);

        await MigratePropertyPhotosAsync(images, report, cancellationToken);
        await MigrateSupplierPhotosAsync(images, report, cancellationToken);
        await MigratePropertyDocumentsAsync(images, report, cancellationToken);
        await MigrateGuestDocumentsAsync(guestDocuments, report, cancellationToken);

        if (!dryRun)
            await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Legacy file migration finished (dry run: {DryRun}): {Uploaded} uploaded, {AlreadyPresent} already in storage, " +
            "{Missing} missing, {References} references rewritten",
            dryRun, report.Uploaded, report.AlreadyPresent, report.Missing, report.ReferencesRewritten);
        return report;
    }

    private async Task MigratePropertyPhotosAsync(LegacyLocation images, LegacyFileMigrationReport report, CancellationToken ct)
    {
        var properties = await db.Properties.IgnoreQueryFilters().ToListAsync(ct);
        foreach (var property in properties)
        {
            var urls = new List<string>(property.PhotoUrls.Count);
            var changed = false;
            foreach (var url in property.PhotoUrls)
            {
                var segments = images.TryGetSegments(url);
                if (segments is not [var folder, var file] || folder != property.Id.ToString())
                {
                    urls.Add(url);
                    continue;
                }

                var key = StorageKeys.PropertyPhoto(property.Id, file);
                if (await CopyAsync(StorageBucket.Public, images.LocalFile(segments), key, report, ct))
                {
                    urls.Add(storage.GetPublicUrl(key));
                    changed = true;
                }
                else
                {
                    urls.Add(url);
                }
            }

            if (changed && !report.DryRun)
                property.PhotoUrls = urls;
        }
    }

    private async Task MigrateSupplierPhotosAsync(LegacyLocation images, LegacyFileMigrationReport report, CancellationToken ct)
    {
        var profiles = await db.SupplierProfiles.IgnoreQueryFilters().ToListAsync(ct);
        foreach (var profile in profiles)
        {
            List<string> current;
            try
            {
                current = JsonSerializer.Deserialize<List<string>>(profile.PhotoUrlsJson, JsonOpts) ?? [];
            }
            catch (JsonException)
            {
                logger.LogWarning("Supplier profile of org {OrgId} has unreadable PhotoUrlsJson; skipped", profile.OrgId);
                continue;
            }

            var urls = new List<string>(current.Count);
            var changed = false;
            foreach (var url in current)
            {
                var segments = images.TryGetSegments(url);
                if (segments is not [var folder, var file] || !Guid.TryParse(folder, out var orgId))
                {
                    urls.Add(url);
                    continue;
                }

                var key = StorageKeys.SupplierPhoto(orgId, file);
                if (await CopyAsync(StorageBucket.Public, images.LocalFile(segments), key, report, ct))
                {
                    urls.Add(storage.GetPublicUrl(key));
                    changed = true;
                }
                else
                {
                    urls.Add(url);
                }
            }

            if (changed && !report.DryRun)
                profile.PhotoUrlsJson = JsonSerializer.Serialize(urls, JsonOpts);
        }
    }

    private async Task MigratePropertyDocumentsAsync(LegacyLocation images, LegacyFileMigrationReport report, CancellationToken ct)
    {
        var documents = await db.PropertyDocuments.IgnoreQueryFilters().ToListAsync(ct);
        foreach (var document in documents)
        {
            var segments = images.TryGetSegments(document.StorageUrl);
            if (segments is not [var folder, "documents", var file] || folder != document.PropertyId.ToString())
                continue;

            var key = StorageKeys.PropertyDocument(document.PropertyId, file);
            if (await CopyAsync(StorageBucket.Private, images.LocalFile(segments), key, report, ct) && !report.DryRun)
                document.StorageUrl = key;
        }
    }

    private async Task MigrateGuestDocumentsAsync(LegacyLocation guestDocuments, LegacyFileMigrationReport report, CancellationToken ct)
    {
        var guests = await db.Guests.IgnoreQueryFilters()
            .Where(g => g.DocumentScanUrl != null && g.DocumentScanUrl != "")
            .ToListAsync(ct);
        foreach (var guest in guests)
        {
            var segments = guestDocuments.TryGetSegments(guest.DocumentScanUrl);
            if (segments is not [var orgFolder, var guestFolder, var file]
                || !Guid.TryParse(orgFolder, out var orgId)
                || !Guid.TryParse(guestFolder, out var guestId))
            {
                continue;
            }

            var key = StorageKeys.GuestDocument(orgId, guestId, file);
            if (await CopyAsync(StorageBucket.Private, guestDocuments.LocalFile(segments), key, report, ct) && !report.DryRun)
                guest.DocumentScanUrl = key;
        }
    }

    /// <summary>
    /// Ensures the object exists in the storage (uploading the local file when needed).
    /// Returns false when the file is neither in the storage nor on disk.
    /// </summary>
    private async Task<bool> CopyAsync(
        StorageBucket bucket, string localPath, string key, LegacyFileMigrationReport report, CancellationToken ct)
    {
        if (!StorageKeys.IsValid(key))
        {
            report.Missing++;
            logger.LogWarning("Legacy file {LocalPath} maps to an invalid storage key; skipped", localPath);
            return false;
        }

        if (await storage.ExistsAsync(bucket, key, ct))
        {
            report.AlreadyPresent++;
            report.ReferencesRewritten++;
            return true;
        }

        if (!File.Exists(localPath))
        {
            report.Missing++;
            logger.LogWarning("Legacy file not found on disk nor in storage: {LocalPath} ({Key})", localPath, key);
            return false;
        }

        if (!report.DryRun)
        {
            await using var content = File.OpenRead(localPath);
            await storage.PutAsync(bucket, key, content, StorageKeys.ContentTypeFor(localPath), ct);
        }

        report.Uploaded++;
        report.ReferencesRewritten++;
        return true;
    }

    /// <summary>A pre-FD-07 local folder and the relative URL prefix it was exposed with.</summary>
    private sealed record LegacyLocation(string Root, IReadOnlyList<string> UrlPrefixes)
    {
        public static LegacyLocation FromConfiguration(
            IConfiguration configuration, string section, string defaultLocalPath, string defaultBaseUrl)
        {
            // Same resolution as the old services: relative paths are relative to the working directory.
            var root = Path.GetFullPath(configuration[$"{section}:LocalPath"] is { Length: > 0 } configured
                ? configured
                : defaultLocalPath);
            var prefixes = new[] { configuration[$"{section}:BaseUrl"], defaultBaseUrl }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.TrimEnd('/') + "/")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new LegacyLocation(root, prefixes);
        }

        /// <summary>Path segments after the legacy prefix, or null when <paramref name="url"/> is not a legacy reference.</summary>
        public string[]? TryGetSegments(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var prefix = UrlPrefixes.FirstOrDefault(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null)
                return null;

            var segments = url[prefix.Length..].Split('/');
            return segments.All(s => s.Length > 0 && s != "." && s != "..") ? segments : null;
        }

        public string LocalFile(IEnumerable<string> segments) => Path.Combine([Root, .. segments]);
    }
}

public sealed class LegacyFileMigrationReport
{
    public bool DryRun { get; init; }

    /// <summary>Files uploaded from the local disk (or that would be, in a dry run).</summary>
    public int Uploaded { get; set; }

    /// <summary>Files already in the storage (a previous run uploaded them).</summary>
    public int AlreadyPresent { get; set; }

    /// <summary>Legacy references whose file is neither on disk nor in the storage: left unchanged.</summary>
    public int Missing { get; set; }

    /// <summary>Database references pointing to the storage after the run.</summary>
    public int ReferencesRewritten { get; set; }
}
