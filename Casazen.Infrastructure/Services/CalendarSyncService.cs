using System.Net.Http;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.ICalSpike;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class CalendarSyncService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CalendarSyncService> _logger;

    public CalendarSyncService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<CalendarSyncService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SyncIcalFeedAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await _db.SupplierProfiles
            .FirstOrDefaultAsync(sp => sp.OrgId == orgId, ct);

        if (profile is null || string.IsNullOrWhiteSpace(profile.IcalFeedUrl))
            return;

        try
        {
            using var client = _httpClientFactory.CreateClient("IcalSync");
            client.Timeout = TimeSpan.FromSeconds(30);

            var icsContent = await client.GetStringAsync(profile.IcalFeedUrl, ct);

            if (!ICalImportSpike.IsValidExportFeed(icsContent))
            {
                profile.CalendarSyncError = "iCal feed is not valid or contains no events";
                _logger.LogWarning("Invalid iCal feed for supplier {OrgId}", orgId);
                return;
            }

            var blocks = ICalImportSpike.ParseImport(icsContent);

            // Convert busy blocks to SupplierAvailability (mark as unavailable)
            var dateRange = blocks
                .SelectMany(b => EnumerateDates(b.StartUtc, b.EndUtc))
                .Distinct()
                .ToHashSet();

            var existing = await _db.SupplierAvailability
                .Where(sa => sa.OrgId == orgId && dateRange.Contains(sa.Date))
                .ToListAsync(ct);

            foreach (var date in dateRange)
            {
                var record = existing.FirstOrDefault(e => e.Date == date);
                if (record is null)
                {
                    _db.SupplierAvailability.Add(new SupplierAvailability
                    {
                        OrgId = orgId,
                        Date = date,
                        Available = false, // busy blocks from external calendar → unavailable
                    });
                }
                else if (record.Available)
                {
                    record.Available = false; // external calendar says busy
                }
            }

            profile.CalendarLastSyncAt = DateTime.UtcNow;
            profile.CalendarSyncError = null;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "iCal sync completed for supplier {OrgId}: {BlockCount} blocks → {DateCount} dates",
                orgId, blocks.Count, dateRange.Count);
        }
        catch (Exception ex)
        {
            profile.CalendarSyncError = $"Sync failed: {ex.Message}";
            _logger.LogError(ex, "iCal sync failed for supplier {OrgId}", orgId);
        }
    }

    public async Task SyncAllIcalFeedsAsync(CancellationToken ct = default)
    {
        var profiles = await _db.SupplierProfiles
            .Where(sp => sp.Status == SupplierStatus.Active
                      && sp.CalendarSyncType == CalendarSyncType.ICalFeed
                      && !string.IsNullOrWhiteSpace(sp.IcalFeedUrl))
            .ToListAsync(ct);

        foreach (var profile in profiles)
        {
            await SyncIcalFeedAsync(profile.OrgId, ct);
        }

        _logger.LogInformation("Batch iCal sync completed for {Count} suppliers", profiles.Count);
    }

    private static IEnumerable<DateOnly> EnumerateDates(DateTime start, DateTime end)
    {
        var d = DateOnly.FromDateTime(start.Date);
        var last = DateOnly.FromDateTime(end.Date);
        while (d <= last)
        {
            yield return d;
            d = d.AddDays(1);
        }
    }
}
