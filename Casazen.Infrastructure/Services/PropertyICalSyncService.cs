using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.ICalSpike;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyICalSyncService
{
    private readonly AppDbContext _db;
    private readonly ISafeExternalHttpClient _externalHttpClient;
    private readonly ICalImportService _importService;
    private readonly ICalExportService _exportService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PropertyICalSyncService> _logger;

    public PropertyICalSyncService(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        ICalImportService importService,
        ICalExportService exportService,
        IConfiguration configuration,
        ILogger<PropertyICalSyncService> logger)
    {
        _db = db;
        _externalHttpClient = externalHttpClient;
        _importService = importService;
        _exportService = exportService;
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

    public async Task SyncPropertyFeedAsync(Guid propertyId, CancellationToken ct = default)
    {
        var feed = await _db.PropertyICalFeeds
            .FirstOrDefaultAsync(f => f.PropertyId == propertyId, ct);

        if (feed is null || string.IsNullOrWhiteSpace(feed.ImportUrl))
            return;

        string icsContent;
        try
        {
            // Anti-SSRF download (FD-16): https only, public addresses only, size and time limits.
            icsContent = await _externalHttpClient.GetStringAsync(feed.ImportUrl, ct);
        }
        catch (ExternalFetchException ex)
        {
            _logger.LogWarning(ex, "iCal download failed for property {PropertyId} ({Failure})", propertyId, ex.Failure);
            await SaveFailureAsync(feed, ICalErrorCodes.FromFetchFailure(ex.Failure), ct);
            return;
        }

        IReadOnlyList<ParsedCalendarBlock> parsed;
        try
        {
            if (!ICalImportSpike.IsValidExportFeed(icsContent))
            {
                _logger.LogWarning("Invalid iCal feed for property {PropertyId}", propertyId);
                await SaveFailureAsync(feed, ICalErrorCodes.InvalidFormat, ct);
                return;
            }

            parsed = _importService.Parse(icsContent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unparsable iCal feed for property {PropertyId}", propertyId);
            await SaveFailureAsync(feed, ICalErrorCodes.InvalidFormat, ct);
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var incomingUids = parsed.Select(p => p.ExternalUid).ToHashSet();

            var existing = await _db.CalendarBlocks
                .Where(b => b.PropertyId == propertyId && b.Source == CalendarBlockSource.ICalImport)
                .ToListAsync(ct);

            foreach (var block in parsed)
            {
                var row = existing.FirstOrDefault(e => e.ExternalUid == block.ExternalUid);
                if (row is null)
                {
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
                else
                {
                    row.StartUtc = block.StartUtc;
                    row.EndUtc = block.EndUtc;
                    row.Summary = block.Summary;
                    row.LastSyncedAt = now;
                }
            }

            var orphans = existing.Where(e => e.ExternalUid is not null && !incomingUids.Contains(e.ExternalUid)).ToList();
            if (orphans.Count > 0)
                _db.CalendarBlocks.RemoveRange(orphans);

            feed.LastImportStatus = PropertyICalImportStatus.Success;
            feed.LastError = null;
            feed.LastImportAt = now;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "iCal sync completed for property {PropertyId}: {BlockCount} blocks, {Removed} orphans removed",
                propertyId, parsed.Count, orphans.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "iCal sync failed for property {PropertyId}", propertyId);
            await SaveFailureAsync(feed, ICalErrorCodes.SyncFailed, ct);
        }
    }

    // Stores the stable error code, never the exception message (FD-16: no oracle on what the server can reach).
    private async Task SaveFailureAsync(PropertyICalFeed feed, string errorCode, CancellationToken ct)
    {
        feed.LastImportStatus = PropertyICalImportStatus.Failure;
        feed.LastError = errorCode;
        feed.LastImportAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SyncAllFeedsAsync(CancellationToken ct = default)
    {
        var propertyIds = await _db.PropertyICalFeeds
            .Where(f => f.ImportUrl != null && f.ImportUrl != "")
            .Select(f => f.PropertyId)
            .ToListAsync(ct);

        foreach (var propertyId in propertyIds)
        {
            await SyncPropertyFeedAsync(propertyId, ct);
        }

        _logger.LogInformation("Batch property iCal sync completed for {Count} feeds", propertyIds.Count);
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

    public async Task<bool> HasOverlappingBlockAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken ct = default)
    {
        var checkInDate = checkIn.Date;
        var checkOutDate = checkOut.Date;

        return await _db.CalendarBlocks.AnyAsync(
            b => b.PropertyId == propertyId &&
                 b.StartUtc.Date < checkOutDate &&
                 b.EndUtc.Date > checkInDate,
            ct);
    }
}
