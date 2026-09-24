using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services.ICal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class CalendarSyncService
{
    private readonly AppDbContext _db;
    private readonly ISafeExternalHttpClient _externalHttpClient;
    private readonly ICalImportService _importService;
    private readonly ILogger<CalendarSyncService> _logger;

    public CalendarSyncService(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        ICalImportService importService,
        ILogger<CalendarSyncService> logger)
    {
        _db = db;
        _externalHttpClient = externalHttpClient;
        _importService = importService;
        _logger = logger;
    }

    /// <summary>
    /// Marks unavailable the days of the supplier's iCal feed. A valid calendar without events is a success (A9-13);
    /// only a failed download, a document that is not a readable iCalendar or a database failure store an error code.
    /// </summary>
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
            await SaveFailureAsync(orgId, ICalErrorCodes.FromFetchFailure(ex.Failure), ct);
            return;
        }

        IReadOnlySet<DateOnly> dateRange;
        try
        {
            var parsed = _importService.Parse(icsContent);
            dateRange = ICalImportService.ToBusyDays(parsed.Occurrences);
            if (parsed.SkippedEvents > 0)
            {
                _logger.LogWarning(
                    "iCal feed of supplier {OrgId}: {Skipped} events skipped (first: {FirstError})",
                    orgId, parsed.SkippedEvents, parsed.FirstUnreadableError);
            }
        }
        catch (ICalFormatException ex)
        {
            // Type only: the parser's message can quote the downloaded document.
            _logger.LogWarning(
                "iCal feed of supplier {OrgId} is not a readable iCalendar ({Failure}, {ErrorType})",
                orgId, ex.Failure, ex.InnerException?.GetType().Name);
            await SaveFailureAsync(orgId, ICalErrorCodes.InvalidFormat, ct);
            return;
        }

        try
        {
            // Busy days of the external calendar → SupplierAvailability marked unavailable
            var busyDates = dateRange.ToArray();
            var existing = await _db.SupplierAvailability
                .Where(sa => sa.OrgId == orgId && busyDates.Contains(sa.Date))
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
                "iCal sync completed for supplier {OrgId}: {DateCount} busy dates", orgId, dateRange.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "iCal sync failed for supplier {OrgId}", orgId);
            _db.ChangeTracker.Clear(); // never save the rejected changes a second time (A2-12)
            await SaveFailureAsync(orgId, ICalErrorCodes.SyncFailed, ct);
        }
    }

    // Stores the stable error code, never the exception message (FD-16, A4-10: the message told the supplier
    // what the server could reach).
    private async Task SaveFailureAsync(Guid orgId, string errorCode, CancellationToken ct)
    {
        var profile = await _db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, ct);
        if (profile is null)
            return;

        profile.CalendarSyncError = errorCode;
        profile.CalendarLastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Syncs every active supplier feed; a supplier whose sync fails does not stop the others.</summary>
    public async Task SyncAllIcalFeedsAsync(CancellationToken ct = default)
    {
        var orgIds = await _db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.Status == SupplierStatus.Active
                      && sp.CalendarSyncType == CalendarSyncType.ICalFeed
                      && !string.IsNullOrWhiteSpace(sp.IcalFeedUrl))
            .Select(sp => sp.OrgId)
            .ToListAsync(ct);

        foreach (var orgId in orgIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncIcalFeedAsync(orgId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "iCal sync of supplier {OrgId} failed; continuing with the next supplier", orgId);
                _db.ChangeTracker.Clear();
            }
        }

        _logger.LogInformation("Batch iCal sync completed for {Count} suppliers", orgIds.Count);
    }
}
