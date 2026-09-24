using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.ICalSpike;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class CalendarSyncService
{
    private readonly AppDbContext _db;
    private readonly ISafeExternalHttpClient _externalHttpClient;
    private readonly ILogger<CalendarSyncService> _logger;

    public CalendarSyncService(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        ILogger<CalendarSyncService> logger)
    {
        _db = db;
        _externalHttpClient = externalHttpClient;
        _logger = logger;
    }

    public async Task SyncIcalFeedAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await _db.SupplierProfiles
            .FirstOrDefaultAsync(sp => sp.OrgId == orgId, ct);

        if (profile is null || string.IsNullOrWhiteSpace(profile.IcalFeedUrl))
            return;

        string icsContent;
        try
        {
            // Anti-SSRF download (FD-16): https only, public addresses only, size and time limits.
            icsContent = await _externalHttpClient.GetStringAsync(profile.IcalFeedUrl, ct);
        }
        catch (ExternalFetchException ex)
        {
            _logger.LogWarning(ex, "iCal download failed for supplier {OrgId} ({Failure})", orgId, ex.Failure);
            await SaveFailureAsync(profile, ICalErrorCodes.FromFetchFailure(ex.Failure), ct);
            return;
        }

        IReadOnlyList<CalendarBlockSlice> blocks;
        try
        {
            if (!ICalImportSpike.IsValidExportFeed(icsContent))
            {
                _logger.LogWarning("Invalid iCal feed for supplier {OrgId}", orgId);
                await SaveFailureAsync(profile, ICalErrorCodes.InvalidFormat, ct);
                return;
            }

            blocks = ICalImportSpike.ParseImport(icsContent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unparsable iCal feed for supplier {OrgId}", orgId);
            await SaveFailureAsync(profile, ICalErrorCodes.InvalidFormat, ct);
            return;
        }

        try
        {
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "iCal sync failed for supplier {OrgId}", orgId);
            await SaveFailureAsync(profile, ICalErrorCodes.SyncFailed, ct);
        }
    }

    // Stores the stable error code, never the exception message (FD-16, A4-10: the message told the supplier
    // what the server could reach).
    private async Task SaveFailureAsync(SupplierProfile profile, string errorCode, CancellationToken ct)
    {
        profile.CalendarSyncError = errorCode;
        profile.CalendarLastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
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

    private const int MaxDatesPerSync = 366; // 1 year max per supplier per sync

    private static IEnumerable<DateOnly> EnumerateDates(DateTime start, DateTime end)
    {
        var d = DateOnly.FromDateTime(start.Date);
        var last = DateOnly.FromDateTime(end.Date);
        var count = 0;
        while (d <= last && count++ < MaxDatesPerSync)
        {
            yield return d;
            d = d.AddDays(1);
        }
    }
}
