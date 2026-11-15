using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestDocumentPurgeService" />
/// <remarks>
/// <para><b>Which stays.</b> The stays that owe the communication (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, no no-show mark: the one definition) and
/// hold a photo of a document or whose booker holds a legacy scan, of the org asked for. A stay is covered when an Alloggiati receipt covers its flat on its arrival
/// day (Europe/Rome), read through <c>ManualProofCoverage</c>, the one reading the traffic light uses too, <b>and</b> the traffic light reads the stay green with that
/// receipt: the verdict is asked of the state engine (<see cref="IComplianceStatusService.GetStaysAReceiptTurnsGreenAsync"/>, <see cref="ComplianceLightRules.Alloggiati"/>),
/// not rebuilt here, so a stay whose communication failed (an error, a rejection, a guest the channel manager could not take) or was only partly accepted is not covered
/// whatever receipt exists, keeps its photos and its scan, and is a stay that still waits for its receipt for the scan of its guest. «Covered» is the same word as the
/// «Diventano in regola» the host reads when it uploads the receipt.</para>
/// <para><b>One stay, one unit of work.</b> The objects go first (<see cref="DocumentImageCleaner"/>, the same deleter an erasure uses, so a failure of the storage leaves
/// the rows for the next run), then the rows, the scan reference and the line of the send log are saved together, and the context is cleared before the next stay.
/// A stay that fails is counted and left as it is; the run goes on and fails at its end, never in silence.</para>
/// <para><b>The scan of the booker</b> is the guest's, shared by all its stays: it goes when the guest has no other stay that owes the communication and is not covered
/// yet (a stay that has not arrived is never covered). <see cref="GuestDataEraser.DeleteDocumentScanAsync"/> deletes it, so an object another guest record still points to
/// is kept, as for an erasure.</para>
/// <para><b>Nothing personal in the log.</b> Counts and the ids of the org and the stay. The exception of a failed storage call is not logged here: it is the one the
/// run fails with, which Hangfire keeps.</para>
/// </remarks>
public sealed class GuestDocumentPurgeService(
    AppDbContext db,
    IFileStorage storage,
    GuestDataEraser eraser,
    IComplianceStatusService status,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<GuestDocumentPurgeService> logger) : IGuestDocumentPurgeService
{
    public async Task<GuestDocumentPurgeResult> PurgeAsync(Guid? orgId, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.PurgeDocumentsAfterReceipt))
        {
            logger.LogDebug("The deletion of the documents after the receipt is off: nothing is read or deleted");
            return GuestDocumentPurgeResult.Off;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await CandidatesAsync(orgId, ct);
        var covered = await CoveredAsync(candidates, ct);

        int stays = 0, images = 0, scans = 0, failed = 0;
        Exception? firstFailure = null;
        foreach (var stay in candidates.Where(candidate => covered.Contains(candidate.Id)))
        {
            try
            {
                var (removedImages, removedScan) = await PurgeStayAsync(stay, now, ct);
                if (removedImages == 0 && removedScan == 0)
                    continue;

                stays++;
                images += removedImages;
                scans += removedScan;
                logger.LogInformation(
                    "Documents of a stay deleted after its receipt. OrgId={OrgId} BookingId={BookingId} Images={Images} Scans={Scans}",
                    stay.OrgId, stay.Id, removedImages, removedScan);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What this stay staged is dropped, so the next stay starts clean; its rows and references are still there for the next run.
                db.ChangeTracker.Clear();
                failed++;
                firstFailure ??= ex;
                logger.LogWarning(
                    "The documents of a stay could not be deleted after its receipt. OrgId={OrgId} BookingId={BookingId}",
                    stay.OrgId, stay.Id);
            }
        }

        logger.LogInformation(
            "Documents deleted after the receipt: {Stays} stays, {Images} document photos, {Scans} scans, {Failed} stays failed",
            stays, images, scans, failed);

        // Nothing is kept past its receipt in silence: the run fails (and is repeated) while a stay could not be cleared; what was deleted stays deleted.
        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"The documents of {failed} stay(s) covered by a receipt could not be deleted from the storage.",
                firstFailure);
        }

        return new GuestDocumentPurgeResult(true, stays, images, scans);
    }

    /// <summary>
    /// The stays that may have something to delete: those that owe the communication and hold a photo of a document (a row of <c>DocumentImages</c>, marked deleted or not)
    /// or whose booker holds a legacy scan. Read as a few ids and one day, oldest arrival first: a stay with nothing left is not here, so the set follows the stays that
    /// wait for their receipt.
    /// </summary>
    private async Task<List<StayRow>> CandidatesAsync(Guid? orgId, CancellationToken ct)
    {
        var stays = db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null);
        if (orgId is { } onlyOrg)
            stays = stays.Where(b => b.OrgId == onlyOrg);

        return await stays
            .Where(b => db.DocumentImages.Any(image => image.BookingId == b.Id && image.OrgId == b.OrgId)
                        || (b.Guest.DocumentScanUrl != null && b.Guest.DocumentScanUrl != string.Empty))
            .OrderBy(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .Select(b => new StayRow(b.Id, b.OrgId, b.PropertyId, b.GuestId, b.CheckInDate))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The stays among <paramref name="stays"/> that an Alloggiati receipt covers: a receipt of their org that names their flat on their arrival day, and that the traffic
    /// light reads green with that receipt (the verdict of the state engine: a stay whose communication failed or was only partly accepted is not covered). Read for one org
    /// at a time, so a receipt of another org never covers a stay and the engine is asked about the stays of one org only.
    /// </summary>
    private async Task<HashSet<Guid>> CoveredAsync(IReadOnlyList<StayRow> stays, CancellationToken ct)
    {
        var covered = new HashSet<Guid>();
        foreach (var org in stays.GroupBy(stay => stay.OrgId))
        {
            var arrivals = org.Select(stay => (Stay: stay, Day: RomeCalendar.DateInRome(stay.CheckInDate))).ToList();
            var proofs = await ManualProofCoverage.ReadAsync(
                db,
                org.Key,
                arrivals.Select(arrival => arrival.Stay.PropertyId).Distinct().ToList(),
                arrivals.Min(arrival => arrival.Day),
                arrivals.Max(arrival => arrival.Day),
                ct);
            var receipts = proofs
                .Where(proof => proof.Kind == ManualProofKind.AlloggiatiReceipt)
                .Select(proof => (proof.PropertyId, proof.Day))
                .ToHashSet();
            var withReceipt = arrivals
                .Where(arrival => receipts.Contains((arrival.Stay.PropertyId, arrival.Day)))
                .Select(arrival => arrival.Stay.Id)
                .ToList();
            if (withReceipt.Count > 0)
                covered.UnionWith(await status.GetStaysAReceiptTurnsGreenAsync(org.Key, withReceipt, ct));
        }

        return covered;
    }

    /// <summary>
    /// Deletes what one covered stay holds and saves it with its line of the send log, in one unit of work; the context is cleared after it. Nothing is written when
    /// the stay holds nothing any more.
    /// </summary>
    private async Task<(int Images, int Scans)> PurgeStayAsync(StayRow stay, DateTime now, CancellationToken ct)
    {
        var photos = await db.DocumentImages
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(image => image.OrgId == stay.OrgId && image.BookingId == stay.Id)
            .ToListAsync(ct);
        if (photos.Count > 0)
            await DocumentImageCleaner.DeleteAsync(db, storage, logger, photos, ct);

        var scan = await DeleteScanOfBookerAsync(stay, ct);
        if (photos.Count == 0 && !scan.ReferenceCleared)
            return (0, 0);

        // The line of the send log of the stay, with the date it was deleted, only for what really left the storage: the photos, and a scan whose object was deleted. A scan
        // whose object another guest record still points to (a snapshot of the same guest) or whose reference is not a storage key only loses its reference here: that is
        // saved all the same, but it is neither counted nor written as a deletion. Added through the set (its id is preset).
        if (photos.Count > 0 || scan.ObjectsDeleted > 0)
        {
            db.ComplianceSendLogEntries.Add(new ComplianceSendLogEntry
            {
                OrgId = stay.OrgId,
                PropertyId = stay.PropertyId,
                BookingId = stay.Id,
                Kind = ComplianceSendKind.DocumentsDeleted,
                Sender = ComplianceSender.CasaZen,
                SentAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return (photos.Count, scan.ObjectsDeleted);
    }

    /// <summary>
    /// Clears the reference of the legacy scan of the booker of the stay and deletes its object (<see cref="ScanOutcome.ObjectsDeleted"/> says whether it was deleted), unless
    /// the guest has another stay that owes the communication and is not covered yet: the scan is the guest's, and that stay may still need it. Nothing is cleared when the
    /// booker holds no scan. The object is kept while another guest record still points to it: it goes with the last reference.
    /// </summary>
    private async Task<ScanOutcome> DeleteScanOfBookerAsync(StayRow stay, CancellationToken ct)
    {
        // The reference first and nothing else: most covered stays have no legacy scan, and the record of the guest is loaded only for the one that has.
        var scan = await db.Guests
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(guest => guest.OrgId == stay.OrgId && guest.Id == stay.GuestId)
            .Select(guest => guest.DocumentScanUrl)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(scan))
            return ScanOutcome.Untouched;

        var others = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => b.OrgId == stay.OrgId && b.GuestId == stay.GuestId && b.Id != stay.Id)
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null)
            .Select(b => new StayRow(b.Id, b.OrgId, b.PropertyId, b.GuestId, b.CheckInDate))
            .ToListAsync(ct);
        var coveredOthers = await CoveredAsync(others, ct);
        if (others.Any(other => !coveredOthers.Contains(other.Id)))
            return ScanOutcome.Untouched;

        var booker = await db.Guests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(guest => guest.OrgId == stay.OrgId && guest.Id == stay.GuestId, ct);
        if (booker is null)
            return ScanOutcome.Untouched;

        var objectsDeleted = await eraser.DeleteDocumentScanAsync(booker, ct);
        return new ScanOutcome(true, objectsDeleted);
    }

    private sealed record StayRow(Guid Id, Guid OrgId, Guid PropertyId, Guid GuestId, DateTime CheckInDate);

    /// <summary>What happened to the scan of a booker: whether its reference was cleared (to be saved), and how many objects left the storage (0 when another record still points to it).</summary>
    private sealed record ScanOutcome(bool ReferenceCleared, int ObjectsDeleted)
    {
        public static ScanOutcome Untouched { get; } = new(false, 0);
    }
}
