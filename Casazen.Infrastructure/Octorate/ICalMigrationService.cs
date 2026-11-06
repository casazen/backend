using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Hands the calendar of a channel over from the iCal import to Octorate (E02/F02/US01/T03, ADR-002 amended 2026-11-03, R11, R14, NF-28): once the
/// Octorate calendar of a property's listing has been read, the iCal feeds of the channels that listing is connected to are marked
/// <see cref="PropertyICalFeed.Deprecated"/>, and the batch sync (<c>PropertyICalSyncService.SyncAllFeedsAsync</c>) stops importing them. Until
/// then the iCal feed keeps being imported: no gap and no double import.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The caller's promise.</b> <see cref="DeprecateCoveredFeedsAsync"/> is called by <c>CalendarSyncJob</c> right after a successful read of the
/// Octorate calendar of the listing. This class checks the other half of the rule, that the mapping is <see cref="OctorateMappingStatus.Confirmed"/>;
/// a failed read never reaches it, so the feeds stay as they are.</item>
/// <item><b>Which feeds.</b> The feeds of the property whose channel (<see cref="ICalFeedChannel.Airbnb"/>, <see cref="ICalFeedChannel.BookingCom"/>) is a
/// portal of the listing (<see cref="OctorateListingMapping.ChannelIdsJson"/>: 274 Airbnb, 142 Booking.com). A feed of channel
/// <see cref="ICalFeedChannel.Other"/> is never deprecated automatically: Octorate does not know its source.</item>
/// <item><b>What stays.</b> The feed and its <see cref="CalendarBlock"/>s are kept, so are the stays the host made from them, and the iCal export is
/// untouched (ADR-002-R13). The blocks of a deprecated feed are no longer refreshed: the host removes the feed when it is no longer needed.</item>
/// <item><b>Once, and per org.</b> Already deprecated feeds are left as they are (idempotent); only the feeds of the mapping's org are read or changed,
/// under the iCal lock of the property. The log line has the property and feed ids, never an address.</item>
/// <item><b>Not one way.</b> When the Octorate calendar of a property is no longer read (its mapping is not confirmed any more, or its connection is
/// not connected), <see cref="ReinstateFeedsAsync"/> clears <see cref="PropertyICalFeed.Deprecated"/> and the feeds are imported again: nothing else
/// would keep the calendar of the property up to date. A read that merely failed changes nothing.</item>
/// </list>
/// </remarks>
public sealed class ICalMigrationService(AppDbContext db, TimeProvider timeProvider, ILogger<ICalMigrationService> logger)
{
    /// <summary>The Octorate portal code of Airbnb.</summary>
    public const string AirbnbPortal = "274";

    /// <summary>The Octorate portal code of Booking.com.</summary>
    public const string BookingComPortal = "142";

    /// <summary>
    /// Deprecates the iCal feeds of <paramref name="propertyId"/> that the Octorate listing makes redundant; returns the ids of the feeds
    /// deprecated now (none when there is nothing to do).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DeprecateCoveredFeedsAsync(Guid propertyId, CancellationToken ct)
    {
        var mapping = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new { m.OrgId, m.ChannelIdsJson })
            .FirstOrDefaultAsync(ct);
        if (mapping is null)
            return [];

        var portals = PortalCodesOf(mapping.ChannelIdsJson);
        var covered = new List<ICalFeedChannel>();
        if (portals.Contains(AirbnbPortal))
            covered.Add(ICalFeedChannel.Airbnb);
        if (portals.Contains(BookingComPortal))
            covered.Add(ICalFeedChannel.BookingCom);
        if (covered.Count == 0)
            return [];

        // Most runs find nothing left to do (the job calls this after every successful read): look before taking the lock.
        var pending = await db.PropertyICalFeeds
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(f => f.OrgId == mapping.OrgId && f.PropertyId == propertyId && !f.Deprecated && covered.Contains(f.Channel), ct);
        if (!pending)
            return [];

        // The iCal lock of the property: a sync of a feed that is running finishes before the feed is marked, and the next one skips it.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()));

        // Loaded and saved like every other write to a feed; nothing here reads the address of a feed, and nothing logs it.
        var feeds = await db.PropertyICalFeeds
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(f => f.OrgId == mapping.OrgId && f.PropertyId == propertyId && !f.Deprecated && covered.Contains(f.Channel))
            .OrderBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
        if (feeds.Count == 0)
            return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var feed in feeds)
        {
            feed.Deprecated = true;
            feed.DeprecatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var ids = feeds.Select(f => f.Id).ToList();
        logger.LogInformation(
            "iCal feeds of property {PropertyId} deprecated, the Octorate calendar replaces them: {FeedIds}", propertyId, string.Join(", ", ids));
        return ids;
    }

    /// <summary>
    /// Gives back to the iCal import the deprecated feeds of every property whose Octorate calendar is not read any more: its mapping is no longer
    /// confirmed (the host rejected the listing or mapped the property again), or its connection is no longer connected (the token was refused, the
    /// host disconnected). The hand-over is not one way: with the calendar of Octorate no longer read nothing else would keep the calendar of the
    /// property up to date. <paramref name="readPropertyIds"/> are the properties the calendar job reads now (confirmed mapping with its room, on a
    /// connected connection): their feeds stay deprecated, and so do the feeds of a property whose read merely failed (that changes nothing).
    /// Returns the ids of the feeds given back (none when there is nothing to do).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ReinstateFeedsAsync(IReadOnlyCollection<Guid> readPropertyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readPropertyIds);

        // Most runs find nothing left to do: look before taking any lock.
        var read = readPropertyIds.ToList();
        var pending = await db.PropertyICalFeeds
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(f => f.Deprecated && !read.Contains(f.PropertyId))
            .Select(f => new { f.OrgId, f.PropertyId })
            .Distinct()
            .ToListAsync(ct);

        var returned = new List<Guid>();
        foreach (var target in pending)
        {
            // The iCal lock of the property, like the hand-over itself: a sync of a feed that is running finishes before the feed is given back.
            await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, target.PropertyId.ToString()));

            var feeds = await db.PropertyICalFeeds
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(f => f.OrgId == target.OrgId && f.PropertyId == target.PropertyId && f.Deprecated)
                .OrderBy(f => f.CreatedAt)
                .ThenBy(f => f.Id)
                .ToListAsync(ct);
            if (feeds.Count == 0)
                continue;

            foreach (var feed in feeds)
            {
                feed.Deprecated = false;
                feed.DeprecatedAt = null;
            }

            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            var ids = feeds.Select(f => f.Id).ToList();
            db.ChangeTracker.Clear();
            returned.AddRange(ids);
            logger.LogInformation(
                "iCal feeds of property {PropertyId} given back to the import, the Octorate calendar is not read any more: {FeedIds}",
                target.PropertyId,
                string.Join(", ", ids));
        }

        return returned;
    }

    /// <summary>The portal codes of the listing: the keys of <c>{ "142": "1234567" }</c>; none for a column that is empty or not an object.</summary>
    internal static HashSet<string> PortalCodesOf(string? channelIdsJson)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(channelIdsJson))
            return codes;

        try
        {
            using var document = JsonDocument.Parse(channelIdsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return codes;

            foreach (var property in document.RootElement.EnumerateObject())
                codes.Add(property.Name.Trim());
        }
        catch (JsonException)
        {
            // A column CasaZen did not write: no portal is read from it.
        }

        return codes;
    }
}
