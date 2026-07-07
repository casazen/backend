using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.ICalSpike;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyICalSyncService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICalImportService _importService;
    private readonly ICalExportService _exportService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PropertyICalSyncService> _logger;

    public PropertyICalSyncService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ICalImportService importService,
        ICalExportService exportService,
        IConfiguration configuration,
        ILogger<PropertyICalSyncService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
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

    public async Task SetImportUrlAndSyncAsync(
        Guid propertyId,
        Guid orgId,
        string importUrl,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(importUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Import URL must be a valid HTTPS URL.");
        }

        var feed = await GetOrCreateFeedAsync(propertyId, orgId, ct);
        feed.ImportUrl = importUrl.Trim();
        feed.LastError = null;
        await _db.SaveChangesAsync(ct);

        await SyncPropertyFeedAsync(propertyId, ct);
    }

    public async Task SyncPropertyFeedAsync(Guid propertyId, CancellationToken ct = default)
    {
        var feed = await _db.PropertyICalFeeds
            .FirstOrDefaultAsync(f => f.PropertyId == propertyId, ct);

        if (feed is null || string.IsNullOrWhiteSpace(feed.ImportUrl))
            return;

        try
        {
            using var client = _httpClientFactory.CreateClient("IcalSync");
            var icsContent = await client.GetStringAsync(feed.ImportUrl, ct);

            if (!ICalImportSpike.IsValidExportFeed(icsContent))
            {
                feed.LastImportStatus = PropertyICalImportStatus.Failure;
                feed.LastError = "iCal feed is not valid or contains no events";
                feed.LastImportAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning("Invalid iCal feed for property {PropertyId}", propertyId);
                return;
            }

            var parsed = _importService.Parse(icsContent);
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
        catch (Exception ex)
        {
            feed.LastImportStatus = PropertyICalImportStatus.Failure;
            feed.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            feed.LastImportAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogError(ex, "iCal sync failed for property {PropertyId}", propertyId);
        }
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

        var bookings = await _db.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.PropertyId == feed.PropertyId && b.Status != BookingStatus.Cancelled)
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
