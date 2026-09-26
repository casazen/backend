using Casazen.Core.Entities;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Retention of guest data per category (CO-15, A5-12, docs/runbooks/gdpr.md), run nightly by <c>GdprDataRetentionJob</c>
/// over every org (a system job: the tenant filter is off, every query below spans all orgs on purpose). Each category
/// has its own period in <c>Gdpr:Retention:{Category}</c> (<see cref="RetentionPeriodOptions"/>) and its own reference
/// date; a period ends the calendar day (Europe/Rome) after reference date + period. <b>A category without a period and a
/// cited source is not applied</b>: nothing is deleted and a warning is logged at every run.
/// <list type="bullet">
/// <item><see cref="GuestDataCategory.DocumentScans"/>: reference = latest check-out of the guest's bookings (creation
/// date without bookings); deletes the scan object and its reference.</item>
/// <item><see cref="GuestDataCategory.AlloggiatiData"/>: every guest of a stay after that stay's check-out (all kinds, CO-12
/// rule), then the booker's own birth, citizenship, sex, document and scan after its latest check-out.</item>
/// <item><see cref="GuestDataCategory.Marketing"/>: the consent ends after the period from the day it was given.</item>
/// <item><see cref="GuestDataCategory.FiscalData"/>: after the latest check-out the whole record is anonymized, as by an
/// erasure but without marking it deleted.</item>
/// </list>
/// Idempotent: processed rows carry a marker (<see cref="StayGuest.AnonymizedAt"/>, <see cref="Guest.AlloggiatiDataErasedAt"/>,
/// <see cref="Guest.DataAnonymizedDate"/>, consent withdrawn) and are not selected again; an audit entry without personal
/// data is written per guest changed.
/// </summary>
public sealed class GuestDataRetentionService(
    AppDbContext db,
    GuestDataEraser eraser,
    IOptions<GdprOptions> gdprOptions,
    TimeProvider timeProvider,
    ILogger<GuestDataRetentionService> logger) : IGuestDataRetentionService
{
    private const int ChunkSize = 100;
    private static readonly BookingStatus[] OpenBookingStatuses =
        [BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.CheckedIn];

    /// <summary>Reference date of the stay-based categories: the latest check-out, or the creation date without bookings.</summary>
    public static DateTime StayReferenceDate(DateTime? latestCheckout, DateTime createdAt) => (latestCheckout ?? createdAt).Date;

    public async Task<GuestRetentionRunResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var retention = gdprOptions.Value.Retention;
        var today = timeProvider.TodayInRome();
        var results = new List<GuestRetentionCategoryResult>();

        foreach (var category in Enum.GetValues<GuestDataCategory>())
        {
            var period = retention.For(category);
            if (!period.IsConfigured)
            {
                logger.LogWarning(
                    "GDPR retention: category {Category} not applied ({Problem}); nothing of it is deleted until " +
                    "Gdpr:Retention:{Category} has a period and its source (docs/runbooks/gdpr.md)",
                    category, period.ConfigurationProblem, category);
                results.Add(new GuestRetentionCategoryResult(category, false, 0, 0, 0));
                continue;
            }

            var result = category switch
            {
                GuestDataCategory.DocumentScans => await ApplyDocumentScansAsync(period, today, cancellationToken),
                GuestDataCategory.AlloggiatiData => await ApplyAlloggiatiDataAsync(period, today, cancellationToken),
                GuestDataCategory.Marketing => await ApplyMarketingAsync(period, today, cancellationToken),
                GuestDataCategory.FiscalData => await ApplyFiscalDataAsync(period, today, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
            };
            results.Add(result);
            logger.LogInformation(
                "GDPR retention: category {Category} applied: {Guests} guests, {StayGuests} stay guests, {Files} files deleted",
                category, result.Guests, result.StayGuests, result.FilesDeleted);
        }

        return new GuestRetentionRunResult(results);
    }

    private async Task<GuestRetentionCategoryResult> ApplyDocumentScansAsync(
        RetentionPeriodOptions period,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var dueIds = await DueGuestIdsAsync(
            db.Guests.Where(g => g.DocumentScanUrl != null && g.DocumentScanUrl != ""), period, today, cancellationToken);

        int guests = 0, files = 0;
        foreach (var chunk in dueIds.Chunk(ChunkSize))
        {
            var now = UtcNow();
            foreach (var guest in await db.Guests.Where(g => chunk.Contains(g.Id)).ToListAsync(cancellationToken))
            {
                var deleted = await eraser.DeleteDocumentScanAsync(guest, cancellationToken);
                guest.UpdatedAt = now;
                AddAudit(guest.OrgId, guest.Id, GuestDataCategory.DocumentScans, now, 0, deleted);
                guests++;
                files += deleted;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new GuestRetentionCategoryResult(GuestDataCategory.DocumentScans, true, guests, 0, files);
    }

    private async Task<GuestRetentionCategoryResult> ApplyAlloggiatiDataAsync(
        RetentionPeriodOptions period,
        DateTime today,
        CancellationToken cancellationToken)
    {
        // 1. Every guest of each stay whose check-out is past the period.
        var candidateStartBefore = period.CandidateStartBefore(today);
        var stays = await db.StayGuests
            .Where(s => s.AnonymizedAt == null
                && !OpenBookingStatuses.Contains(s.Booking.Status)
                && s.Booking.CheckOutDate < candidateStartBefore)
            .Select(s => new { s.BookingId, s.Booking.CheckOutDate })
            .Distinct()
            .ToListAsync(cancellationToken);
        var dueBookings = stays.Where(s => period.HasEnded(s.CheckOutDate, today)).Select(s => s.BookingId).ToList();

        var stayGuests = 0;
        foreach (var chunk in dueBookings.Chunk(ChunkSize))
        {
            var now = UtcNow();
            var perBooker = await eraser.AnonymizeStayGuestsOfBookingsAsync(chunk, now, cancellationToken);
            foreach (var ((orgId, guestId), count) in perBooker)
            {
                AddAudit(orgId, guestId, GuestDataCategory.AlloggiatiData, now, count, 0);
                stayGuests += count;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // 2. The Alloggiati data kept on the booker's own record.
        var dueIds = await DueGuestIdsAsync(db.Guests.Where(g => g.AlloggiatiDataErasedAt == null), period, today, cancellationToken);
        int guests = 0, files = 0;
        foreach (var chunk in dueIds.Chunk(ChunkSize))
        {
            var now = UtcNow();
            foreach (var guest in await db.Guests.Where(g => chunk.Contains(g.Id)).ToListAsync(cancellationToken))
            {
                var outcome = await eraser.EraseAlloggiatiDataAsync(guest, now, cancellationToken);
                if (!outcome.Changed)
                    continue;
                AddAudit(guest.OrgId, guest.Id, GuestDataCategory.AlloggiatiData, now, 0, outcome.FilesDeleted);
                guests++;
                files += outcome.FilesDeleted;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new GuestRetentionCategoryResult(GuestDataCategory.AlloggiatiData, true, guests, stayGuests, files);
    }

    private async Task<GuestRetentionCategoryResult> ApplyMarketingAsync(
        RetentionPeriodOptions period,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var candidateStartBefore = period.CandidateStartBefore(today);
        var candidates = await db.Guests
            .Where(g => g.MarketingConsent && g.MarketingConsentDate != null && g.MarketingConsentDate < candidateStartBefore)
            .Select(g => new { g.Id, GrantedAt = g.MarketingConsentDate!.Value })
            .ToListAsync(cancellationToken);
        var dueIds = candidates.Where(c => period.HasEnded(c.GrantedAt, today)).Select(c => c.Id).ToList();

        var guests = 0;
        foreach (var chunk in dueIds.Chunk(ChunkSize))
        {
            var now = UtcNow();
            var grants = await db.GuestConsentRecords.AsNoTracking()
                .Where(r => chunk.Contains(r.GuestId) && r.Purpose == GuestConsentPurpose.Marketing && r.Action == GuestConsentAction.Granted)
                .Select(r => new { r.GuestId, r.Version, r.RecordedAt })
                .ToListAsync(cancellationToken);
            var grantVersions = grants
                .GroupBy(r => r.GuestId)
                .ToDictionary(g => g.Key, g => g.MaxBy(r => r.RecordedAt)!.Version);

            foreach (var guest in await db.Guests.Where(g => chunk.Contains(g.Id)).ToListAsync(cancellationToken))
            {
                guest.MarketingConsent = false;
                guest.MarketingConsentDate = now;
                guest.UpdatedAt = now;
                db.GuestConsentRecords.Add(new GuestConsentRecord
                {
                    OrgId = guest.OrgId,
                    GuestId = guest.Id,
                    Purpose = GuestConsentPurpose.Marketing,
                    Action = GuestConsentAction.Expired,
                    Version = grantVersions.GetValueOrDefault(guest.Id) ?? string.Empty,
                    Source = GuestConsentSource.RetentionPolicy,
                    RecordedAt = now,
                });
                AddAudit(guest.OrgId, guest.Id, GuestDataCategory.Marketing, now, 0, 0);
                guests++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new GuestRetentionCategoryResult(GuestDataCategory.Marketing, true, guests, 0, 0);
    }

    private async Task<GuestRetentionCategoryResult> ApplyFiscalDataAsync(
        RetentionPeriodOptions period,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var dueIds = await DueGuestIdsAsync(db.Guests.Where(g => g.DataAnonymizedDate == null), period, today, cancellationToken);

        int guests = 0, stayGuests = 0, files = 0;
        foreach (var chunk in dueIds.Chunk(ChunkSize))
        {
            var now = UtcNow();
            foreach (var guest in await db.Guests.Where(g => chunk.Contains(g.Id)).ToListAsync(cancellationToken))
            {
                var outcome = await eraser.AnonymizeAsync(guest, now, cancellationToken);
                if (!outcome.Changed)
                    continue;
                AddAudit(guest.OrgId, guest.Id, GuestDataCategory.FiscalData, now, outcome.StayGuestsAnonymized, outcome.FilesDeleted);
                guests++;
                stayGuests += outcome.StayGuestsAnonymized;
                files += outcome.FilesDeleted;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new GuestRetentionCategoryResult(GuestDataCategory.FiscalData, true, guests, stayGuests, files);
    }

    /// <summary>
    /// Ids of the guests of <paramref name="query"/> whose stay reference date (<see cref="StayReferenceDate"/>) plus the
    /// period ended before today: coarse filter in SQL, exact check in memory.
    /// </summary>
    private async Task<List<Guid>> DueGuestIdsAsync(
        IQueryable<Guest> query,
        RetentionPeriodOptions period,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var candidateStartBefore = period.CandidateStartBefore(today);
        var bookings = db.Bookings.IgnoreQueryFilters().AsNoTracking();
        var candidates = await query
            .Select(g => new
            {
                g.Id,
                g.CreatedAt,
                LatestCheckout = bookings.Where(b => b.GuestId == g.Id).Max(b => (DateTime?)b.CheckOutDate),
                HasOpenBooking = bookings.Any(b => b.GuestId == g.Id && OpenBookingStatuses.Contains(b.Status)),
            })
            .Where(g => !g.HasOpenBooking && (g.LatestCheckout ?? g.CreatedAt) < candidateStartBefore)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(c => period.HasEnded(StayReferenceDate(c.LatestCheckout, c.CreatedAt), today))
            .Select(c => c.Id)
            .ToList();
    }

    private void AddAudit(Guid orgId, Guid guestId, GuestDataCategory category, DateTime now, int stayGuests, int files) =>
        db.GuestPrivacyAuditEntries.Add(new GuestPrivacyAuditEntry
        {
            OrgId = orgId,
            GuestId = guestId,
            Action = GuestPrivacyAuditAction.RetentionApplied,
            Category = category,
            ActorUserId = null,
            StayGuestsAnonymized = stayGuests,
            FilesDeleted = files,
            OccurredAt = now,
        });

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
