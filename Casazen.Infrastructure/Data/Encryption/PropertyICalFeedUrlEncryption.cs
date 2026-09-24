using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// Encryption at rest of <see cref="PropertyICalFeed.ImportUrl"/> (PC-11, A2-20): Data Protection value converter of
/// <see cref="AppDbContext"/>, same mechanism as the OTA partner credentials (FD-07, FD-20), with its own purpose.
/// </summary>
public static class PropertyICalFeedUrlEncryption
{
    /// <summary>Data Protection purpose of the import URLs (never change it: stored values could not be read).</summary>
    public const string Purpose = "Casazen.PropertyICalFeed.ImportUrl";

    private const int BatchSize = 200;

    /// <summary>
    /// True for a URL stored in clear before PC-11. A Data Protection payload is base64url (it starts with
    /// <c>CfDJ8</c>) and never contains <c>://</c>.
    /// </summary>
    public static bool IsLegacyPlaintext(string stored) =>
        stored.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || stored.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the model of <paramref name="db"/> encrypts the import URLs (Data Protection configured).</summary>
    public static bool IsConfigured(AppDbContext db) =>
        db.Model.FindEntityType(typeof(PropertyICalFeed))?
            .FindProperty(nameof(PropertyICalFeed.ImportUrl))?
            .GetValueConverter() is EncryptedStringConverter;

    /// <summary>
    /// Rewrites, encrypted, the import URLs still stored in clear (saved before PC-11). Idempotent: run at every
    /// startup after the migrations; returns the number of URLs encrypted. The values go through the EF converter,
    /// the same protector every later read uses. Throws when the context has no Data Protection: the application
    /// must not run while it would store the URLs in clear.
    /// </summary>
    public static async Task<int> EncryptLegacyPlaintextUrlsAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (!IsConfigured(db))
        {
            throw new InvalidOperationException(
                "PropertyICalFeed.ImportUrl is not encrypted: AppDbContext was built without Data Protection.");
        }

        // Raw SQL on purpose: the column holds the stored text, not what the converter returns.
        var ids = await db.Database
            .SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM "PropertyICalFeeds"
                WHERE "ImportUrl" ILIKE 'https://%' OR "ImportUrl" ILIKE 'http://%'
                """)
            .ToListAsync(ct);

        foreach (var batch in ids.Chunk(BatchSize))
        {
            // Startup maintenance of every org: no tenant, the rows are only re-encrypted.
            var feeds = await db.PropertyICalFeeds
                .IgnoreQueryFilters()
                .Where(f => batch.Contains(f.Id))
                .ToListAsync(ct);

            foreach (var feed in feeds)
                db.Entry(feed).Property(f => f.ImportUrl).IsModified = true;

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        if (ids.Count > 0)
            logger.LogInformation("Encrypted {Count} iCal import URLs stored in clear before PC-11", ids.Count);

        return ids.Count;
    }
}
