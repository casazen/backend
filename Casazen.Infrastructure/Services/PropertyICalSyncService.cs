using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services.ICal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyICalSyncService
{
    private readonly AppDbContext _db;
    private readonly ISafeExternalHttpClient _externalHttpClient;
    private readonly ICalImportService _importService;
    private readonly ICalExportService _exportService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PropertyICalSyncService> _logger;

    public PropertyICalSyncService(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        ICalImportService importService,
        ICalExportService exportService,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PropertyICalSyncService> logger)
    {
        _db = db;
        _externalHttpClient = externalHttpClient;
        _importService = importService;
        _exportService = exportService;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PropertyICalFeed> GetOrCreateFeedAsync(Guid propertyId, Guid orgId, CancellationToken ct = default)
    {
        var feed = await _db.PropertyICalFeeds
            .FirstOrDefaultAsync(f => f.PropertyId == propertyId, ct);

        if (feed is not null)
            return feed;

        feed = new PropertyICalFeed
        {
            PropertyId = propertyId,
            OrgId = orgId,
            ExportToken = Guid.NewGuid(),
        };
        _db.PropertyICalFeeds.Add(feed);
        await _db.SaveChangesAsync(ct);
        return feed;
    }

    public async Task<PropertyICalFeed?> GetFeedAsync(Guid propertyId, CancellationToken ct = default) =>
        await _db.PropertyICalFeeds.FirstOrDefaultAsync(f => f.PropertyId == propertyId, ct);

    // IgnoreQueryFilters (here and in BuildPublicExportAsync): the public export is authorized by the
    // unguessable ExportToken, not by a user, and is scoped to that feed's property.
    public async Task<PropertyICalFeed?> GetFeedByExportTokenAsync(Guid exportToken, CancellationToken ct = default) =>
        await _db.PropertyICalFeeds
            .IgnoreQueryFilters()
            .Include(f => f.Property)
            .FirstOrDefaultAsync(f => f.ExportToken == exportToken, ct);

    public string BuildExportUrl(Guid exportToken)
    {
        var apiBase = _configuration["App:ApiBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
        return $"{apiBase}/api/public/ical/{exportToken}";
    }

    public async Task<int> GetBlockCountAsync(Guid propertyId, CancellationToken ct = default) =>
        await _db.CalendarBlocks.CountAsync(b => b.PropertyId == propertyId, ct);

    /// <summary>
    /// Saves the import URL and marks the feed <see cref="PropertyICalImportStatus.Syncing"/>. It does not download
    /// the feed: the caller queues <see cref="SyncPropertyFeedAsync"/> in a background job (A2-21), so a slow or
    /// hostile feed never runs inside the request.
    /// </summary>
    /// <exception cref="DomainRuleException">Code <see cref="ICalErrorCodes.InvalidUrl"/>: not an allowed external https URL.</exception>
    public async Task<PropertyICalFeed> SetImportUrlAsync(
        Guid propertyId,
        Guid orgId,
        string? importUrl,
        CancellationToken ct = default)
    {
        if (!_externalHttpClient.TryValidateUrl(importUrl, out _))
        {
            throw new DomainRuleException(
                ICalErrorCodes.InvalidUrl,
                ICalErrorCodes.MessageKey(ICalErrorCodes.InvalidUrl));
        }

        var feed = await GetOrCreateFeedAsync(propertyId, orgId, ct);
        feed.ImportUrl = importUrl!.Trim();
        feed.LastImportStatus = PropertyICalImportStatus.Syncing;
        feed.LastError = null;
        await _db.SaveChangesAsync(ct);
        return feed;
    }

    /// <summary>
    /// Downloads, reads and applies the import feed of one property; every outcome is stored on the feed, so the
    /// caller never sees an exception for a bad feed (PC-10, A2-10, A2-12).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A valid calendar is a success even with no event: the imported blocks missing from the feed are removed
    /// (a reservation cancelled on the OTA frees its dates).</item>
    /// <item>Only a download failure (<see cref="ICalErrorCodes.FromFetchFailure"/>), a document that is not a readable
    /// iCalendar (<see cref="ICalErrorCodes.InvalidFormat"/>) or any other failure, e.g. of the database
    /// (<see cref="ICalErrorCodes.SyncFailed"/>), is an error; the existing blocks are then kept.</item>
    /// <item>The blocks are written in one transaction under a per-property advisory lock: two runs for the same
    /// property (the 15-minute job and the first sync of a new URL) wait for each other instead of inserting the same
    /// UIDs twice. A run whose URL was replaced meanwhile writes nothing: the run of the new URL does.</item>
    /// <item>After a failed write the context is cleared before the failure is stored (no second save of the same
    /// rejected changes).</item>
    /// </list>
    /// Logs name the feed and property ids, never the URL (export links carry secret tokens).
    /// </remarks>
    public async Task SyncPropertyFeedAsync(Guid propertyId, CancellationToken ct = default)
    {
        var feed = await _db.PropertyICalFeeds
            .AsNoTracking()
            .Where(f => f.PropertyId == propertyId)
            .Select(f => new { f.Id, f.ImportUrl })
            .FirstOrDefaultAsync(ct);

        if (feed is null || string.IsNullOrWhiteSpace(feed.ImportUrl))
            return;

        try
        {
            await SyncFeedAsync(feed.Id, propertyId, feed.ImportUrl, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Unexpected failure (database, client, ...): the feed gets its own error state, the blocks are kept.
            _logger.LogError(ex, "iCal sync failed for feed {FeedId} of property {PropertyId}", feed.Id, propertyId);
            _db.ChangeTracker.Clear();
            await SaveFailureAsync(feed.Id, feed.ImportUrl, ICalErrorCodes.SyncFailed, ct);
        }
    }

    private async Task SyncFeedAsync(Guid feedId, Guid propertyId, string importUrl, CancellationToken ct)
    {
        string icsContent;
        try
        {
            // Anti-SSRF download (FD-16): https only, public addresses only, size and time limits.
            icsContent = await _externalHttpClient.GetStringAsync(importUrl, ct);
        }
        catch (ExternalFetchException ex)
        {
            _logger.LogWarning(
                ex, "iCal download failed for feed {FeedId} of property {PropertyId} ({Failure})", feedId, propertyId, ex.Failure);
            await SaveFailureAsync(feedId, importUrl, ICalErrorCodes.FromFetchFailure(ex.Failure), ct);
            return;
        }

        ICalFeedParseResult parsed;
        PropertyICalBlocks incoming;
        try
        {
            parsed = _importService.Parse(icsContent);
            incoming = ICalImportService.ToPropertyBlocks(parsed.Occurrences);
        }
        catch (ICalFormatException ex)
        {
            // Type only: the parser's message can quote the downloaded document.
            _logger.LogWarning(
                "iCal feed {FeedId} of property {PropertyId} is not a readable iCalendar ({Failure}, {ErrorType})",
                feedId, propertyId, ex.Failure, ex.InnerException?.GetType().Name);
            await SaveFailureAsync(feedId, importUrl, ICalErrorCodes.InvalidFormat, ct);
            return;
        }

        if (parsed.SkippedEvents > 0)
        {
            _logger.LogWarning(
                "iCal feed {FeedId} of property {PropertyId}: {Unreadable} unreadable events and {Unsupported} sub-daily recurrences skipped (first: {FirstError})",
                feedId, propertyId, parsed.UnreadableEvents, parsed.UnsupportedRecurrences, parsed.FirstUnreadableError);
        }

        var removed = await ApplyBlocksAsync(feedId, propertyId, importUrl, incoming.Blocks, parsed.UnreadableUids, ct);
        if (removed is null)
            return;

        _logger.LogInformation(
            "iCal sync completed for feed {FeedId} of property {PropertyId}: {BlockCount} blocks, {Removed} removed, {Cancelled} cancelled and {Transparent} free events ignored, {Merged} duplicates merged",
            feedId, propertyId, incoming.Blocks.Count, removed, parsed.CancelledEvents, parsed.TransparentEvents, incoming.MergedDuplicates);
    }

    /// <summary>
    /// Replaces the imported blocks of the property with <paramref name="incoming"/> and marks the feed
    /// <see cref="PropertyICalImportStatus.Success"/>. The blocks of <paramref name="unreadableUids"/> (events still in
    /// the feed that could not be read) are kept. Returns the number of blocks removed, or null when the import URL
    /// changed during the download (nothing is written).
    /// </summary>
    private async Task<int?> ApplyBlocksAsync(
        Guid feedId,
        Guid propertyId,
        string importUrl,
        IReadOnlyList<ParsedCalendarBlock> incoming,
        IReadOnlySet<string> unreadableUids,
        CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            _db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()));

        // Read under the lock: a run that held it has committed, READ COMMITTED sees its blocks.
        var feed = await _db.PropertyICalFeeds.FirstOrDefaultAsync(f => f.Id == feedId, ct);
        if (feed is null || feed.ImportUrl != importUrl)
        {
            _logger.LogInformation(
                "iCal feed {FeedId} of property {PropertyId} changed during the sync: result discarded", feedId, propertyId);
            return null;
        }

        var existing = await _db.CalendarBlocks
            .Where(b => b.PropertyId == propertyId && b.Source == CalendarBlockSource.ICalImport)
            .ToListAsync(ct);
        var existingByUid = existing
            .Where(b => b.ExternalUid is not null)
            .GroupBy(b => b.ExternalUid!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        foreach (var block in incoming)
        {
            if (existingByUid.TryGetValue(block.ExternalUid, out var row))
            {
                row.StartUtc = block.StartUtc;
                row.EndUtc = block.EndUtc;
                row.Summary = block.Summary;
                row.LastSyncedAt = now;
                continue;
            }

            _db.CalendarBlocks.Add(new CalendarBlock
            {
                PropertyId = propertyId,
                OrgId = feed.OrgId,
                Source = CalendarBlockSource.ICalImport,
                ExternalUid = block.ExternalUid,
                StartUtc = block.StartUtc,
                EndUtc = block.EndUtc,
                Summary = block.Summary,
                LastSyncedAt = now,
            });
        }

        // An empty feed removes every imported block of the property (A2-10, A9-13). An event still in the feed but
        // unreadable is not proof that its reservation is gone: its blocks stay.
        var incomingUids = incoming.Select(b => b.ExternalUid).ToHashSet(StringComparer.Ordinal);
        var orphans = existing
            .Where(b => b.ExternalUid is not null
                        && !incomingUids.Contains(b.ExternalUid)
                        && !ICalImportService.IsKeyOfAny(b.ExternalUid, unreadableUids))
            .ToList();
        _db.CalendarBlocks.RemoveRange(orphans);

        feed.LastImportStatus = PropertyICalImportStatus.Success;
        feed.LastError = null;
        feed.LastImportAt = now;
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return orphans.Count;
    }

    // Stores the stable error code, never the exception message (FD-16: no oracle on what the server can reach). The
    // context holds no pending change here. A feed whose URL was replaced meanwhile is left to the run of the new URL.
    private async Task SaveFailureAsync(Guid feedId, string importUrl, string errorCode, CancellationToken ct)
    {
        var feed = await _db.PropertyICalFeeds.FirstOrDefaultAsync(f => f.Id == feedId, ct);
        if (feed is null || feed.ImportUrl != importUrl)
            return;

        feed.LastImportStatus = PropertyICalImportStatus.Failure;
        feed.LastError = errorCode;
        feed.LastImportAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Syncs every property feed with an import URL, least recently synced first. Each feed runs in its own DI scope
    /// (its own <see cref="AppDbContext"/>) and its own try/catch: a feed that fails, even unexpectedly, never stops
    /// the others (A2-12). Only cancellation stops the batch.
    /// </summary>
    public async Task SyncAllFeedsAsync(CancellationToken ct = default)
    {
        var propertyIds = await _db.PropertyICalFeeds
            .AsNoTracking()
            .Where(f => f.ImportUrl != null && f.ImportUrl != "")
            .OrderBy(f => f.LastImportAt.HasValue)
            .ThenBy(f => f.LastImportAt)
            .Select(f => f.PropertyId)
            .ToListAsync(ct);

        var failed = 0;
        foreach (var propertyId in propertyIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<PropertyICalSyncService>()
                    .SyncPropertyFeedAsync(propertyId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                _logger.LogError(ex, "iCal sync of property {PropertyId} failed; continuing with the next feed", propertyId);
            }
        }

        _logger.LogInformation(
            "Batch property iCal sync completed for {Count} feeds ({Failed} failed unexpectedly)", propertyIds.Count, failed);
    }

    public async Task<string> BuildPublicExportAsync(Guid exportToken, CancellationToken ct = default)
    {
        var feed = await GetFeedByExportTokenAsync(exportToken, ct)
            ?? throw new InvalidOperationException("Export token not found");

        // Expired checkout holds no longer take their dates, even before the expiry job cancels them (BK-21). A pending
        // "pay at the property" request is left out until the host accepts it: an anonymous request must not block the
        // OTAs (BK-06, A3-06); the approval checks the imported OTA blocks again.
        var expiredHoldCutoff = CheckoutHolds.CutoffAt(DateTime.UtcNow, CheckoutHolds.GetTtlMinutes(_configuration));
        var bookings = await _db.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.PropertyId == feed.PropertyId)
            .Where(CheckoutHolds.OccupiesDates(expiredHoldCutoff))
            .Where(OnSiteRequests.IsExportedToOtas())
            .ToListAsync(ct);

        var blocks = await _db.CalendarBlocks
            .IgnoreQueryFilters()
            .Where(b => b.PropertyId == feed.PropertyId)
            .ToListAsync(ct);

        return _exportService.BuildPropertyFeed(bookings, blocks);
    }

    public async Task<IReadOnlyList<CalendarBlock>> GetBlocksInRangeAsync(
        Guid propertyId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken ct = default) =>
        await _db.CalendarBlocks
            .Where(b => b.PropertyId == propertyId &&
                        b.StartUtc < endUtc &&
                        b.EndUtc > startUtc)
            .ToListAsync(ct);

    public Task<bool> HasOverlappingBlockAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken ct = default) =>
        HasOverlappingBlockAsync(_db, propertyId, checkIn, checkOut, ct);

    /// <summary>
    /// True when a calendar block (iCal import) of the property overlaps [<paramref name="checkIn"/>,
    /// <paramref name="checkOut"/>): the single rule of the booking checks, also used by the late-payment
    /// reconfirmation of the payment webhook (BK-04).
    /// </summary>
    internal static async Task<bool> HasOverlappingBlockAsync(
        AppDbContext db,
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken ct = default)
    {
        var checkInDate = checkIn.Date;
        var checkOutDate = checkOut.Date;

        return await db.CalendarBlocks.AnyAsync(
            b => b.PropertyId == propertyId &&
                 b.StartUtc.Date < checkOutDate &&
                 b.EndUtc.Date > checkInDate,
            ct);
    }
}
