using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services.ICal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public partial class PropertyICalSyncService
{
    private readonly AppDbContext _db;
    private readonly ISafeExternalHttpClient _externalHttpClient;
    private readonly ICalImportService _importService;
    private readonly ICalExportService _exportService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptions<ICalImportOptions> _importOptions;
    private readonly ILogger<PropertyICalSyncService> _logger;
    private readonly TimeProvider _clock;
    private readonly INotificationService _notifications;

    public PropertyICalSyncService(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        ICalImportService importService,
        ICalExportService exportService,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptions<ICalImportOptions> importOptions,
        ILogger<PropertyICalSyncService> logger,
        TimeProvider clock,
        INotificationService notifications)
    {
        _db = db;
        _externalHttpClient = externalHttpClient;
        _importService = importService;
        _exportService = exportService;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _importOptions = importOptions;
        _logger = logger;
        _clock = clock;
        _notifications = notifications;
    }

    /// <summary>Import feeds of the property, oldest first (PC-11). Read-only.</summary>
    public async Task<IReadOnlyList<PropertyICalFeed>> ListFeedsAsync(Guid propertyId, CancellationToken ct = default) =>
        await _db.PropertyICalFeeds
            .AsNoTracking()
            .Where(f => f.PropertyId == propertyId)
            .OrderBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);

    /// <summary>Number of blocks imported by each feed of the property (feeds without blocks are missing).</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> GetBlockCountsByFeedAsync(Guid propertyId, CancellationToken ct = default) =>
        await _db.CalendarBlocks
            .Where(b => b.PropertyId == propertyId && b.FeedId != null)
            .GroupBy(b => b.FeedId!.Value)
            .Select(g => new { FeedId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FeedId, x => x.Count, ct);

    /// <summary>
    /// The export link of the property, created on first use. Created under the property's advisory lock: two
    /// concurrent first requests get the same token.
    /// </summary>
    public async Task<PropertyICalExport> GetOrCreateExportAsync(Guid propertyId, Guid orgId, CancellationToken ct = default)
    {
        var export = await _db.PropertyICalExports.FirstOrDefaultAsync(e => e.PropertyId == propertyId, ct);
        if (export is not null)
            return export;

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            _db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()));

        export = await _db.PropertyICalExports.FirstOrDefaultAsync(e => e.PropertyId == propertyId, ct);
        if (export is null)
        {
            export = new PropertyICalExport
            {
                PropertyId = propertyId,
                OrgId = orgId,
                ExportToken = PropertyICalExport.NewExportToken(),
                CreatedAt = DateTime.UtcNow,
            };
            _db.PropertyICalExports.Add(export);
            await _db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return export;
    }

    /// <summary>
    /// Gives the export link of the property a new token (PC-12, A2-22): from now on the old link answers 404, so a
    /// link that leaked (or was pasted on a channel the host left) stops working. The host pastes the new link on the
    /// OTAs. Creates the link when the property has none yet. Under the property's advisory lock, like the creation.
    /// </summary>
    public async Task<PropertyICalExport> RegenerateExportTokenAsync(Guid propertyId, Guid orgId, CancellationToken ct = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            _db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()));

        var export = await _db.PropertyICalExports.FirstOrDefaultAsync(e => e.PropertyId == propertyId, ct);
        if (export is null)
        {
            export = new PropertyICalExport { PropertyId = propertyId, OrgId = orgId, CreatedAt = DateTime.UtcNow };
            _db.PropertyICalExports.Add(export);
        }

        export.ExportToken = PropertyICalExport.NewExportToken();
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        // Never the token: the link gives access to the feed.
        _logger.LogInformation("iCal export link of property {PropertyId} regenerated", propertyId);
        return export;
    }

    // IgnoreQueryFilters (here and in BuildPublicExportAsync): the public export is authorized by the
    // unguessable ExportToken, not by a user, and is scoped to that export's property.
    public async Task<PropertyICalExport?> GetExportByTokenAsync(Guid exportToken, CancellationToken ct = default) =>
        await _db.PropertyICalExports
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ExportToken == exportToken, ct);

    public string BuildExportUrl(Guid exportToken)
    {
        var apiBase = _configuration["App:ApiBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
        return $"{apiBase}/api/public/ical/{exportToken}";
    }

    public async Task<int> GetBlockCountAsync(Guid propertyId, CancellationToken ct = default) =>
        await _db.CalendarBlocks.CountAsync(b => b.PropertyId == propertyId, ct);

    /// <summary>
    /// Adds an import feed to the property and marks it <see cref="PropertyICalImportStatus.Syncing"/> (PC-11). It does
    /// not download the feed: the caller queues <see cref="SyncFeedAsync"/> in a background job (A2-21), so a slow or
    /// hostile feed never runs inside the request. The URL is stored encrypted (A2-20).
    /// </summary>
    /// <param name="channel">Airbnb, Booking.com or other; null: taken from the host of the URL.</param>
    /// <param name="label">Optional free text, trimmed (empty: none).</param>
    /// <exception cref="DomainRuleException">
    /// <see cref="ICalErrorCodes.InvalidUrl"/>: not an allowed external https URL (FD-16).
    /// <see cref="ICalFeedErrorCodes.InvalidLabel"/>: label too long or with control characters.
    /// <see cref="ICalFeedErrorCodes.LimitReached"/>: the property has <c>ICalImport:MaxFeedsPerProperty</c> feeds.
    /// </exception>
    /// <exception cref="DomainConflictException"><see cref="ICalFeedErrorCodes.Duplicate"/>: the property already imports that URL.</exception>
    public async Task<PropertyICalFeed> AddFeedAsync(
        Guid propertyId,
        Guid orgId,
        ICalFeedChannel? channel,
        string? label,
        string? importUrl,
        CancellationToken ct = default)
    {
        if (importUrl is null
            || importUrl.Trim().Length > PropertyICalFeed.ImportUrlMaxLength
            || !_externalHttpClient.TryValidateUrl(importUrl, out var uri))
        {
            throw new DomainRuleException(
                ICalErrorCodes.InvalidUrl,
                ICalErrorCodes.MessageKey(ICalErrorCodes.InvalidUrl));
        }

        var normalizedLabel = NormalizeLabel(label);
        var url = importUrl.Trim();

        // Count and duplicate check under the property's lock: two concurrent adds cannot both pass them.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            _db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()));

        var existingUrls = await _db.PropertyICalFeeds
            .Where(f => f.PropertyId == propertyId)
            .Select(f => f.ImportUrl)
            .ToListAsync(ct);

        var maxFeeds = _importOptions.Value.EffectiveMaxFeedsPerProperty;
        if (existingUrls.Count >= maxFeeds)
        {
            throw new DomainRuleException(
                ICalFeedErrorCodes.LimitReached, ICalFeedErrorCodes.LimitReachedMessageKey, maxFeeds);
        }

        var key = ComparableUrl(uri);
        if (existingUrls.Any(existing => existing is not null
                                         && _externalHttpClient.TryValidateUrl(existing, out var existingUri)
                                         && ComparableUrl(existingUri) == key))
        {
            throw new DomainConflictException(ICalFeedErrorCodes.Duplicate, ICalFeedErrorCodes.DuplicateMessageKey);
        }

        var feed = new PropertyICalFeed
        {
            PropertyId = propertyId,
            OrgId = orgId,
            Channel = channel ?? InferChannel(uri),
            Label = normalizedLabel,
            ImportUrl = url,
            CreatedAt = DateTime.UtcNow,
            LastImportStatus = PropertyICalImportStatus.Syncing,
        };
        _db.PropertyICalFeeds.Add(feed);
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        _logger.LogInformation("iCal feed {FeedId} added to property {PropertyId} ({Channel})", feed.Id, propertyId, feed.Channel);
        return feed;
    }

    /// <summary>
    /// Removes an import feed and every block it imported, and only those (PC-11): the dates it held become free, the
    /// blocks of the other feeds stay. Under the property's lock, so a sync of the feed running meanwhile writes
    /// nothing. Returns the number of blocks removed.
    /// </summary>
    /// <exception cref="NotFoundException"><see cref="ICalFeedErrorCodes.NotFound"/>: not a feed of the property.</exception>
    public async Task<int> RemoveFeedAsync(Guid propertyId, Guid feedId, CancellationToken ct = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            _db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, propertyId.ToString()));

        var feed = await _db.PropertyICalFeeds.FirstOrDefaultAsync(f => f.Id == feedId && f.PropertyId == propertyId, ct)
            ?? throw FeedNotFound(feedId);

        // Also deleted by the FK cascade; removed here so every provider (and the change tracker) agrees.
        var blocks = await _db.CalendarBlocks.Where(b => b.FeedId == feedId).ToListAsync(ct);
        _db.CalendarBlocks.RemoveRange(blocks);
        _db.PropertyICalFeeds.Remove(feed);
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "iCal feed {FeedId} removed from property {PropertyId} with {BlockCount} blocks", feedId, propertyId, blocks.Count);
        return blocks.Count;
    }

    /// <summary>
    /// "Sync now" of one feed: marks it <see cref="PropertyICalImportStatus.Syncing"/> and tells the caller to queue
    /// <see cref="SyncFeedAsync"/>. A feed already syncing is left as it is and nothing is queued: repeated clicks
    /// never pile up downloads (the 15-minute job syncs it anyway).
    /// </summary>
    /// <exception cref="NotFoundException"><see cref="ICalFeedErrorCodes.NotFound"/>: not a feed of the property.</exception>
    public async Task<(PropertyICalFeed Feed, bool Queue)> RequestSyncAsync(Guid propertyId, Guid feedId, CancellationToken ct = default)
    {
        var feed = await _db.PropertyICalFeeds.FirstOrDefaultAsync(f => f.Id == feedId && f.PropertyId == propertyId, ct)
            ?? throw FeedNotFound(feedId);

        if (feed.LastImportStatus == PropertyICalImportStatus.Syncing)
            return (feed, false);

        feed.LastImportStatus = PropertyICalImportStatus.Syncing;
        await _db.SaveChangesAsync(ct);
        return (feed, true);
    }

    /// <summary>
    /// Downloads, reads and applies one import feed; every outcome is stored on the feed, so the caller never sees an
    /// exception for a bad feed (PC-10, A2-10, A2-12). Only the blocks of this feed are touched (PC-11): the other
    /// feeds of the property keep theirs whatever happens here.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A valid calendar is a success even with no event: the blocks of the feed missing from it are removed
    /// (a reservation cancelled on the OTA frees its dates).</item>
    /// <item>Only a download failure (<see cref="ICalErrorCodes.FromFetchFailure"/>), a document that is not a readable
    /// iCalendar (<see cref="ICalErrorCodes.InvalidFormat"/>) or any other failure, e.g. of the database
    /// (<see cref="ICalErrorCodes.SyncFailed"/>), is an error; the existing blocks are then kept.</item>
    /// <item>The blocks are written in one transaction under the property's advisory lock: two runs (the 15-minute job
    /// and a first sync or "sync now") wait for each other instead of inserting the same UIDs twice. A run whose feed
    /// was removed or changed meanwhile writes nothing.</item>
    /// <item>After a failed write the context is cleared before the failure is stored (no second save of the same
    /// rejected changes).</item>
    /// </list>
    /// Logs name the feed and property ids, never the URL (import and export links carry secret tokens). An unknown
    /// id (e.g. a job queued before PC-11 with a property id) does nothing.
    /// </remarks>
    public async Task SyncFeedAsync(Guid feedId, CancellationToken ct = default)
    {
        var feed = await _db.PropertyICalFeeds
            .AsNoTracking()
            .Where(f => f.Id == feedId)
            .Select(f => new { f.Id, f.PropertyId, f.ImportUrl })
            .FirstOrDefaultAsync(ct);

        if (feed is null || string.IsNullOrWhiteSpace(feed.ImportUrl))
            return;

        try
        {
            await SyncFeedCoreAsync(feed.Id, feed.PropertyId, feed.ImportUrl, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Unexpected failure (database, client, ...): the feed gets its own error state, the blocks are kept.
            _logger.LogError(ex, "iCal sync failed for feed {FeedId} of property {PropertyId}", feed.Id, feed.PropertyId);
            _db.ChangeTracker.Clear();
            await SaveFailureAsync(feed.Id, feed.ImportUrl, ICalErrorCodes.SyncFailed, ct);
        }
    }

    private async Task SyncFeedCoreAsync(Guid feedId, Guid propertyId, string importUrl, CancellationToken ct)
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

        var applied = await ApplyBlocksAsync(feedId, propertyId, importUrl, incoming.Blocks, parsed.UnreadableUids, ct);
        if (applied is not { } outcome)
            return;

        _logger.LogInformation(
            "iCal sync completed for feed {FeedId} of property {PropertyId}: {BlockCount} blocks, {Removed} removed, {Cancelled} cancelled and {Transparent} free events ignored, {Merged} duplicates merged",
            feedId, propertyId, incoming.Blocks.Count, outcome.Removed, parsed.CancelledEvents, parsed.TransparentEvents, incoming.MergedDuplicates);

        await AlertOtaStayReviewsAsync(feedId, outcome.Reviews, ct);
    }

    // After the commit: the stays are already marked "da verificare", an alert that cannot be delivered never undoes that
    // nor turns the sync into a failure.
    private async Task AlertOtaStayReviewsAsync(Guid feedId, IReadOnlyList<OtaStayReviewAlert> reviews, CancellationToken ct)
    {
        foreach (var review in reviews)
        {
            try
            {
                await _notifications.SendOtaStayReviewAlertAsync(review, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(
                    ex, "Review alert of OTA stay {BookingId} (feed {FeedId}, {Reason}) not delivered", review.BookingId, feedId, review.Reason);
            }
        }
    }

    /// <summary>
    /// Replaces the blocks of the feed with <paramref name="incoming"/> and marks the feed
    /// <see cref="PropertyICalImportStatus.Success"/>. The blocks of <paramref name="unreadableUids"/> (events still in
    /// the feed that could not be read) are kept. Returns the number of blocks removed and the OTA stays marked "da
    /// verificare" (CO-21), or null when the feed was removed or its URL changed during the download (nothing is written).
    /// </summary>
    private async Task<(int Removed, IReadOnlyList<OtaStayReviewAlert> Reviews)?> ApplyBlocksAsync(
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
            .Where(b => b.FeedId == feedId && b.Source == CalendarBlockSource.ICalImport)
            .ToListAsync(ct);
        var existingByUid = existing
            .Where(b => b.ExternalUid is not null)
            .GroupBy(b => b.ExternalUid!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Dates of the blocks turned into OTA stays before this sync (CO-21): a change is reported, never applied to the stay.
        var linkedDates = existing
            .Where(b => b.BookingId is not null)
            .ToDictionary(b => b.Id, b => (Start: b.StartUtc.Date, End: b.EndUtc.Date));
        var added = new List<CalendarBlock>();

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

            var newBlock = new CalendarBlock
            {
                PropertyId = propertyId,
                OrgId = feed.OrgId,
                FeedId = feedId,
                Source = CalendarBlockSource.ICalImport,
                ExternalUid = block.ExternalUid,
                StartUtc = block.StartUtc,
                EndUtc = block.EndUtc,
                Summary = block.Summary,
                LastSyncedAt = now,
            };
            _db.CalendarBlocks.Add(newBlock);
            added.Add(newBlock);
        }

        // An empty feed removes every block of this feed (A2-10, A9-13), never those of the property's other feeds. An
        // event still in the feed but unreadable is not proof that its reservation is gone: its blocks stay.
        var incomingUids = incoming.Select(b => b.ExternalUid).ToHashSet(StringComparer.Ordinal);
        var orphans = existing
            .Where(b => b.ExternalUid is not null
                        && !incomingUids.Contains(b.ExternalUid)
                        && !ICalImportService.IsKeyOfAny(b.ExternalUid, unreadableUids))
            .ToList();
        _db.CalendarBlocks.RemoveRange(orphans);

        var reviews = await ReviewOtaStaysAsync(feedId, existing, linkedDates, orphans, added, now, ct);

        feed.LastImportStatus = PropertyICalImportStatus.Success;
        feed.LastError = null;
        feed.LastImportAt = now;
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return (orphans.Count, reviews);
    }

    /// <summary>
    /// OTA stays created from blocks of this feed (CO-21, decision D7). A sync never changes nor cancels them: the
    /// reservation may have been cancelled on the channel, or the feed may be wrong for a while.
    /// <list type="bullet">
    /// <item>A linked block gone from the feed while its stay is not over (<see cref="OtaStays.IsReviewable"/>): the stay
    /// becomes "da verificare" (<see cref="OtaStayReviewReason.BlockRemoved"/>), stays confirmed and keeps its dates taken.
    /// Once a stay is over the OTAs drop its reservation from the feed: nothing is reported.</item>
    /// <item>A linked block with other dates than at the previous sync, and than its stay:
    /// <see cref="OtaStayReviewReason.BlockDatesChanged"/>. The block keeps following the feed and takes its new nights on
    /// its own (<see cref="PropertyOccupancy.BlockTakesNightIn"/>); the stay keeps its dates until the host applies the new
    /// ones.</item>
    /// <item>A new block with the UID of a stay of this feed whose block had left it (the reservation is back) is linked
    /// to it again; other dates than the stay's are reported as a change.</item>
    /// </list>
    /// Each change found is reported once (an alert per change, after the commit); the host clears the mark.
    /// </summary>
    private async Task<IReadOnlyList<OtaStayReviewAlert>> ReviewOtaStaysAsync(
        Guid feedId,
        IReadOnlyList<CalendarBlock> existing,
        IReadOnlyDictionary<Guid, (DateTime Start, DateTime End)> linkedDates,
        IReadOnlyCollection<CalendarBlock> orphans,
        IReadOnlyList<CalendarBlock> added,
        DateTime now,
        CancellationToken ct)
    {
        var removed = orphans.Where(b => b.BookingId is not null).ToList();
        var moved = existing
            .Where(b => b.BookingId is not null
                        && !orphans.Contains(b)
                        && linkedDates.TryGetValue(b.Id, out var before)
                        && (before.Start != b.StartUtc.Date || before.End != b.EndUtc.Date))
            .ToList();

        var addedUids = added.Select(b => b.ExternalUid!).ToList();
        var comeBack = addedUids.Count == 0
            ? []
            : await _db.Bookings
                .Where(s => s.ICalFeedId == feedId
                            && addedUids.Contains(s.ExternalId)
                            && s.Status != BookingStatus.Cancelled
                            && !_db.CalendarBlocks.Any(b => b.BookingId == s.Id))
                .ToListAsync(ct);

        var stayIds = removed.Select(b => b.BookingId!.Value).Concat(moved.Select(b => b.BookingId!.Value)).ToList();
        var stays = stayIds.Count == 0
            ? []
            : await _db.Bookings.Where(s => stayIds.Contains(s.Id)).ToListAsync(ct);
        var staysById = stays.Concat(comeBack).DistinctBy(s => s.Id).ToDictionary(s => s.Id);

        var today = _clock.TodayInRome();
        var reviews = new List<OtaStayReviewAlert>();

        foreach (var block in removed)
        {
            if (staysById.TryGetValue(block.BookingId!.Value, out var stay)
                && OtaStays.IsReviewable(stay, today)
                && stay.OtaReviewReason != OtaStayReviewReason.BlockRemoved)
            {
                MarkForReview(stay, OtaStayReviewReason.BlockRemoved);
                reviews.Add(new OtaStayReviewAlert(stay.Id, OtaStayReviewReason.BlockRemoved));
            }
        }

        foreach (var block in moved)
        {
            if (staysById.TryGetValue(block.BookingId!.Value, out var stay))
                ReportDates(stay, block);
        }

        foreach (var stay in comeBack)
        {
            var block = added.FirstOrDefault(b => string.Equals(b.ExternalUid, stay.ExternalId, StringComparison.Ordinal));
            if (block is null)
                continue;

            block.BookingId = stay.Id;
            _logger.LogInformation(
                "iCal block of feed {FeedId} linked again to OTA stay {BookingId}: it is back in the feed", feedId, stay.Id);
            ReportDates(stay, block);
        }

        return reviews;

        void ReportDates(Booking stay, CalendarBlock block)
        {
            var channelIn = block.StartUtc.Date;
            var channelOut = block.EndUtc.Date;
            if (!OtaStays.IsReviewable(stay, today)
                || (channelIn == stay.CheckInDate.Date && channelOut == stay.CheckOutDate.Date))
                return;

            MarkForReview(stay, OtaStayReviewReason.BlockDatesChanged);
            reviews.Add(new OtaStayReviewAlert(stay.Id, OtaStayReviewReason.BlockDatesChanged, channelIn, channelOut));
        }

        void MarkForReview(Booking stay, OtaStayReviewReason reason)
        {
            stay.OtaReviewReason = reason;
            stay.OtaReviewRaisedAt = now;
            stay.UpdatedAt = now;
            _logger.LogWarning(
                "OTA stay {BookingId} of feed {FeedId} to check: {Reason}; the stay was not changed", stay.Id, feedId, reason);
        }
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
    /// Syncs every import feed with a URL, least recently synced first. Each feed runs in its own DI scope (its own
    /// <see cref="AppDbContext"/>) and its own try/catch: a feed that fails, even unexpectedly, never stops the others,
    /// including the other feeds of the same property (A2-12, PC-11). Only cancellation stops the batch.
    /// </summary>
    public async Task SyncAllFeedsAsync(CancellationToken ct = default)
    {
        var feedIds = await _db.PropertyICalFeeds
            .AsNoTracking()
            .Where(f => f.ImportUrl != null && f.ImportUrl != "")
            .OrderBy(f => f.LastImportAt.HasValue)
            .ThenBy(f => f.LastImportAt)
            .Select(f => f.Id)
            .ToListAsync(ct);

        var failed = 0;
        foreach (var feedId in feedIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<PropertyICalSyncService>()
                    .SyncFeedAsync(feedId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                _logger.LogError(ex, "iCal sync of feed {FeedId} failed; continuing with the next feed", feedId);
            }
        }

        _logger.LogInformation(
            "Batch property iCal sync completed for {Count} feeds ({Failed} failed unexpectedly)", feedIds.Count, failed);
    }

    /// <summary>
    /// Channel of a URL whose channel the host did not choose: <c>airbnb.&lt;tld&gt;</c> (and its subdomains) is
    /// Airbnb, <c>booking.com</c> (and its subdomains) is Booking.com, anything else is Other. The migration
    /// <c>AddICalMultiFeed</c> applies the same rule to the feeds saved before PC-11. Display only, never a security
    /// decision.
    /// </summary>
    public static ICalFeedChannel InferChannel(Uri url)
    {
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (AirbnbHost().IsMatch(host))
            return ICalFeedChannel.Airbnb;

        return BookingHost().IsMatch(host) ? ICalFeedChannel.BookingCom : ICalFeedChannel.Other;
    }

    [GeneratedRegex(@"(^|\.)airbnb\.[a-z]{2,}(\.[a-z]{2,})?$")]
    private static partial Regex AirbnbHost();

    [GeneratedRegex(@"(^|\.)booking\.com$")]
    private static partial Regex BookingHost();

    // Label trimmed, empty → none. Control characters (line breaks included) are refused: the label is shown as is.
    private static string? NormalizeLabel(string? label)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (trimmed.Length > PropertyICalFeed.LabelMaxLength || trimmed.Any(char.IsControl))
        {
            throw new DomainRuleException(
                ICalFeedErrorCodes.InvalidLabel, ICalFeedErrorCodes.InvalidLabelMessageKey, PropertyICalFeed.LabelMaxLength);
        }

        return trimmed;
    }

    // Same feed for the duplicate check: scheme, host (lower case), port, path and query as written.
    private static string ComparableUrl(Uri url) =>
        url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

    private static NotFoundException FeedNotFound(Guid feedId) =>
        new($"iCal feed {feedId} not found")
        {
            Code = ICalFeedErrorCodes.NotFound,
            MessageKey = ICalFeedErrorCodes.NotFoundMessageKey,
        };

    /// <summary>
    /// The public iCal feed of the export link <paramref name="exportToken"/> (PC-12, A2-22; content and format in
    /// <see cref="ICalExportService"/>): the bookings that take their dates by the occupancy rule of the booking site
    /// (BK-05), minus pending "pay at the property" requests and OTA stays, and the host's manual blocks. Blocks imported
    /// from the OTA feeds are never exported (no echo).
    /// </summary>
    /// <param name="busySummary">SUMMARY of every event, localized by the caller.</param>
    /// <exception cref="NotFoundException">No export link has this token (never existed or regenerated).</exception>
    public async Task<string> BuildPublicExportAsync(Guid exportToken, string busySummary, CancellationToken ct = default)
    {
        var export = await GetExportByTokenAsync(exportToken, ct)
            ?? throw new NotFoundException("iCal export token not found");

        // Expired checkout holds no longer take their dates, even before the expiry job cancels them (BK-21). A pending
        // "pay at the property" request is left out until the host accepts it: an anonymous request must not block the
        // OTAs (BK-06, A3-06); the approval checks the imported OTA blocks again.
        // Same clock as the public availability (BK-05): a hold is expired for both at the same instant.
        var expiredHoldCutoff = CheckoutHolds.CutoffAt(
            _clock.GetUtcNow().UtcDateTime, CheckoutHolds.GetTtlMinutes(_configuration));
        var bookings = await _db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.PropertyId == export.PropertyId)
            .Where(CheckoutHolds.OccupiesDates(expiredHoldCutoff))
            .Where(OnSiteRequests.IsExportedToOtas())
            .Where(ICalExportService.ExportsBooking)
            .ToListAsync(ct);

        var blocks = await _db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.PropertyId == export.PropertyId)
            .Where(ICalExportService.ExportsBlock)
            .ToListAsync(ct);

        return _exportService.BuildPropertyFeed(bookings, blocks, busySummary);
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
    /// True when a calendar block (iCal import or manual) of the property takes a night of [<paramref name="checkIn"/>,
    /// <paramref name="checkOut"/>): the single rule of the booking checks, also used by the late-payment
    /// reconfirmation of the payment webhook (BK-04), with the nights of the public availability (PropertyOccupancy, BK-05).
    /// </summary>
    internal static async Task<bool> HasOverlappingBlockAsync(
        AppDbContext db,
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken ct = default) =>
        await db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(propertyId, checkIn, checkOut), ct);
}
