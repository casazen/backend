using System.Security.Cryptography;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The evidence kept for the person who reviews a self-hosted verification (E73/F01/US02/T01, T02, NF-195, ADR-013-R20, decision D9), behind
/// <c>Features:CheckInIdentityReviewEvidence</c> together with <c>Features:CheckInIdentityVerification</c> and <c>Features:CheckInIdentitySelfHosted</c>: the MINIMUM a
/// reviewer needs to compare the document with the person, kept encrypted for <c>IdentityVerification:Evidence:RetentionHours</c> hours, read only with the permission of
/// the verification on a reachable property and with a line in the activity log, and deleted at the decision, at its expiry, with an abandoned session and with an erasure.
/// </summary>
/// <remarks>
/// <para><b>What is kept</b> (<see cref="SaveForReviewAsync"/>), only for a review of the closure and never for a property that keeps no document (Ticino, ADR-013-R6: zero
/// retention, no evidence at all): the crop of the portrait of the document (the box <c>DocumentPortrait</c> finds, with a margin of <see cref="PortraitMargin"/> of its size on
/// each side, its longest side at most <c>Evidence:MaxSide</c>, written as a PNG by <see cref="CardPngEncoder"/>) and at most <c>Evidence:MaxFrames</c> of the frames the server
/// chose for the match (0 keeps the crop only): a frame whose longest side fits <c>Evidence:MaxSide</c> as the JPEG received, with every application segment and comment taken
/// out (<see cref="JpegMetadata"/>: no EXIF, no data of the device); a larger frame, or one the browser turned with an EXIF tag, as a PNG of the frame the server decoded,
/// scaled. Never an embedding, a score, a value read on the document or the whole document.</para>
/// <para><b>How</b>: each picture is encrypted with Data Protection (purpose <see cref="ContentPurpose"/>, the id of the evidence as sub-purpose) and written to the private
/// bucket at <c>identity-evidence/{orgId}/{bookingId}/{evidenceId}</c>; one <see cref="IdentityReviewEvidence"/> row each, with the kind, the key, the content type, the
/// SHA-256 of the stored ciphertext (the entity's rule: a hash of the encrypted object, which says nothing of the picture, checked again before a read) and
/// <c>ExpiresAt</c>. One set per booking: the set of an earlier review of the booking is deleted first (<see cref="IdentityEvidenceDeletionReasons.Replaced"/>).</para>
/// <para><b>Optional</b>: an error of the models, of the storage or of the encryption leaves the review without evidence; it never blocks the hand-off to the operators and never
/// changes the decision. What was written before the error is deleted, best effort. The pictures in clear are zeroed in <c>finally</c>.</para>
/// <para><b>Privacy</b>: the log has ids, counts, codes and the type of an exception; never a picture, a key of an object with a name in it, a score or a name. Nothing of it
/// goes to a job, a queue or an e-mail.</para>
/// </remarks>
public sealed class IdentityReviewEvidenceStore(
    AppDbContext db,
    IFileStorage storage,
    IDataProtectionProvider dataProtection,
    IFeatureFlags featureFlags,
    IOptions<IdentityVerificationOptions> identityOptions,
    IOptions<BiometricModelOptions> biometricOptions,
    IFaceDetector detector,
    TimeProvider clock,
    ILogger<IdentityReviewEvidenceStore> logger,
    IActivityLog? activityLog = null)
{
    /// <summary>Data Protection purpose of an evidence object; the id of the evidence is its sub-purpose. Never change it: the objects kept would become unreadable.</summary>
    public const string ContentPurpose = "Casazen.IdentityEvidence.Content.v1";

    /// <summary>The start of the key of every evidence object in the private bucket.</summary>
    public const string StorageKeyPrefix = "identity-evidence/";

    /// <summary>The content type of the crop of the portrait, and of a frame that had to be scaled or turned.</summary>
    public const string PngContentType = "image/png";

    /// <summary>The content type of a frame kept as it came, without its metadata.</summary>
    public const string JpegContentType = "image/jpeg";

    /// <summary>
    /// The margin around the box of the portrait, as a share of its width (left and right) and of its height (top and bottom): the reviewer sees the whole head, not only the
    /// face the detector boxed. A choice of presentation, not a threshold: it decides nothing.
    /// </summary>
    public const double PortraitMargin = 0.25;

    /// <summary>The content type of the stored object: ciphertext.</summary>
    private const string StoredContentType = "application/octet-stream";

    /// <summary>The evidence is kept and read: the self-hosted flow and the evidence are on.</summary>
    public bool EvidenceOn =>
        featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
        && featureFlags.IsEnabled(FeatureFlags.CheckInIdentitySelfHosted)
        && featureFlags.IsEnabled(FeatureFlags.CheckInIdentityReviewEvidence);

    /// <summary>The key of an evidence object: <c>identity-evidence/{orgId}/{bookingId}/{evidenceId}</c>, ids only.</summary>
    public static string StorageKeyOf(Guid orgId, Guid bookingId, Guid evidenceId) =>
        StorageKeys.EnsureValid($"{StorageKeyPrefix}{orgId}/{bookingId}/{evidenceId}");

    /// <summary>
    /// Keeps the evidence of a review that was just committed (<see cref="ReviewEvidenceInput"/>), when the flags are on and the property keeps documents. Returns how many
    /// pictures were kept: 0 when nothing was (off, Ticino, nothing to keep, or an error, logged by its code). Never throws but for a cancellation of <paramref name="ct"/>.
    /// </summary>
    public async Task<int> SaveForReviewAsync(ReviewEvidenceInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!EvidenceOn)
            return 0;

        if (input.PropertyKeepsNoDocument)
        {
            logger.LogInformation("Review evidence of session {SessionId}: the property keeps no document, nothing is kept", input.CheckInSessionId);
            return 0;
        }

        var options = identityOptions.Value;
        if (options.Validate().Count > 0)
        {
            logger.LogWarning("Review evidence of session {SessionId}: the section IdentityVerification is not valid, nothing is kept (evidence_config_invalid)", input.CheckInSessionId);
            return 0;
        }

        var pictures = new List<EvidencePicture>();
        try
        {
            var now = UtcNow();

            // One set per booking: the set of an earlier review goes first, kept or not this time.
            var replaced = await DeleteLiveAsync(input.OrgId, input.BookingId, IdentityEvidenceDeletionReasons.Replaced, now, ct);

            if (PortraitCrop(input.DocumentFront, options.Evidence.MaxSide, input.CheckInSessionId) is { } crop)
                pictures.Add(new EvidencePicture(IdentityEvidenceKind.DocumentPortrait, PngContentType, crop));

            foreach (var index in (input.ChosenFrames ?? []).Take(options.Evidence.MaxFrames))
            {
                if (input.Sequence is not { } sequence || index < 0 || index >= sequence.Frames.Count)
                    continue;

                if (FramePicture(sequence.Frames[index], options.Evidence.MaxSide) is { } frame)
                    pictures.Add(frame);
            }

            if (pictures.Count == 0)
            {
                logger.LogInformation(
                    "Review evidence of session {SessionId}: nothing to keep ({Replaced} earlier pictures deleted)", input.CheckInSessionId, replaced);
                return 0;
            }

            var kept = await WriteAsync(input, pictures, now, options.Evidence.RetentionHours, ct);
            logger.LogInformation(
                "Review evidence of session {SessionId} kept: {Pictures} pictures for {Hours} hours ({Replaced} earlier pictures deleted)",
                input.CheckInSessionId, kept, options.Evidence.RetentionHours, replaced);
            return kept;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: a message could carry what was being read. The review goes on without evidence.
            logger.LogError("Review evidence of session {SessionId} not kept (evidence_store_failed, {ErrorType})", input.CheckInSessionId, ex.GetType().Name);
            return 0;
        }
        finally
        {
            foreach (var picture in pictures)
                ImageBytes.Zero(picture.Bytes);
        }
    }

    /// <summary>
    /// The live evidence of a booking the caller reaches (E73/F01/US02/T02): not deleted, not expired, newest last; null when the booking is not one of the org of
    /// <paramref name="scope"/> on a property it reaches (the caller answers the same 404 as for an evidence that does not exist).
    /// </summary>
    public async Task<IReadOnlyList<IdentityEvidenceItem>?> ListAsync(HostScope scope, Guid bookingId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var reachable = await db.Bookings
            .Where(booking => booking.OrgId == scope.OrgId && booking.Id == bookingId)
            .InScope(scope)
            .AnyAsync(ct);
        if (!reachable)
            return null;

        var now = UtcNow();
        return await db.IdentityReviewEvidences
            .AsNoTracking()
            .Where(evidence => evidence.OrgId == scope.OrgId && evidence.BookingId == bookingId && evidence.DeletedAt == null && evidence.ExpiresAt > now)
            .OrderBy(evidence => evidence.Kind)
            .ThenBy(evidence => evidence.CreatedAt)
            .Select(evidence => new IdentityEvidenceItem(evidence.Id, evidence.Kind, evidence.CreatedAt, evidence.ExpiresAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// One evidence of a booking the caller reaches, decrypted (E73/F01/US02/T02), with its line <see cref="OrgActivityType.IdentityEvidenceViewed"/> in the activity log of the
    /// org written FIRST, in its own save: no line, no content. Null (the same 404 for every case) when the evidence is not of that booking, of the org of
    /// <paramref name="scope"/> and of a property it reaches, is deleted or expired, its object is gone, its ciphertext is not the one stored, or it cannot be decrypted. The
    /// bytes returned are the caller's to zero once they are written to the response.
    /// </summary>
    public async Task<IdentityEvidenceContent?> ReadAsync(HostScope scope, Guid bookingId, Guid evidenceId, string actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (activityLog is null)
        {
            logger.LogWarning("Review evidence {EvidenceId} not served: no activity log to record the read (evidence_audit_unavailable)", evidenceId);
            return null;
        }

        var now = UtcNow();
        var bookings = db.Bookings.Where(booking => booking.OrgId == scope.OrgId).InScope(scope);
        var row = await db.IdentityReviewEvidences
            .AsNoTracking()
            .Where(evidence => evidence.Id == evidenceId
                               && evidence.OrgId == scope.OrgId
                               && evidence.BookingId == bookingId
                               && evidence.DeletedAt == null
                               && evidence.ExpiresAt > now
                               && bookings.Any(booking => booking.Id == evidence.BookingId))
            .FirstOrDefaultAsync(ct);
        if (row is null || !StorageKeys.IsValid(row.StorageKey))
            return null;

        byte[]? cipher = null;
        try
        {
            await using (var stream = await storage.OpenReadAsync(StorageBucket.Private, row.StorageKey, ct))
            {
                if (stream is null)
                {
                    logger.LogWarning("Review evidence {EvidenceId} not served: its object is gone (evidence_object_missing)", row.Id);
                    return null;
                }

                cipher = await ImageBytes.ReadAllAsync(stream, IdentityVerificationFlowService.MaxImageBytes * 2, ct);
            }

            if (!string.Equals(Convert.ToHexString(SHA256.HashData(cipher)), row.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Review evidence {EvidenceId} not served: the object is not the one stored (evidence_integrity)", row.Id);
                return null;
            }

            byte[] plain;
            try
            {
                plain = Protector(row.Id).Unprotect(cipher);
            }
            catch (CryptographicException)
            {
                logger.LogWarning("Review evidence {EvidenceId} not served: it cannot be decrypted (evidence_undecryptable)", row.Id);
                return null;
            }

            try
            {
                activityLog.Record(OrgActivity.Of(
                    scope.OrgId,
                    OrgActivityType.IdentityEvidenceViewed,
                    actorUserId,
                    row.BookingId.ToString(),
                    (OrgActivityDetailKeys.Part, IdentityEvidenceKinds.Code(row.Kind))));
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                Array.Clear(plain);
                throw;
            }

            logger.LogInformation("Review evidence {EvidenceId} of booking {BookingId} read by {UserId}", row.Id, row.BookingId, actorUserId);
            return new IdentityEvidenceContent(row.Id, row.Kind, row.ContentType, plain);
        }
        finally
        {
            ImageBytes.Zero(cipher);
        }
    }

    /// <summary>
    /// What the operator queue shows of the evidence of <paramref name="bookingIds"/> (E73/F01/US02/T02): how many live pictures each booking has and when the first of them
    /// expires. Empty while the evidence is off: the reviewer could not open it.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, PendingEvidenceSummary>> SummariesAsync(Guid orgId, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        if (!EvidenceOn || bookingIds.Count == 0)
            return new Dictionary<Guid, PendingEvidenceSummary>();

        var now = UtcNow();
        var ids = bookingIds.ToList();
        var groups = await db.IdentityReviewEvidences
            .AsNoTracking()
            .Where(evidence => evidence.OrgId == orgId && ids.Contains(evidence.BookingId) && evidence.DeletedAt == null && evidence.ExpiresAt > now)
            .GroupBy(evidence => evidence.BookingId)
            .Select(group => new { BookingId = group.Key, Count = group.Count(), ExpiresAt = group.Min(evidence => evidence.ExpiresAt) })
            .ToListAsync(ct);
        return groups.ToDictionary(group => group.BookingId, group => new PendingEvidenceSummary(group.Count, group.ExpiresAt));
    }

    /// <summary>
    /// Deletes the live evidence of a booking (the objects, then the rows marked with <paramref name="reason"/>, a code of <see cref="IdentityEvidenceDeletionReasons"/>), whatever
    /// the flags say: a deletion is never behind a flag. What was deleted before a failure of the storage is saved, and the failure is thrown: the purge finishes it.
    /// Returns how many pictures were deleted.
    /// </summary>
    public async Task<int> DeleteForBookingAsync(Guid orgId, Guid bookingId, string reason, CancellationToken ct)
    {
        if (!IdentityEvidenceDeletionReasons.All.Contains(reason, StringComparer.Ordinal))
            throw new ArgumentException("Not a reason of the deletion of an evidence.", nameof(reason));

        var deleted = await DeleteLiveAsync(orgId, bookingId, reason, UtcNow(), ct);
        if (deleted > 0)
            logger.LogInformation("Review evidence of booking {BookingId}: {Pictures} pictures deleted ({Reason})", bookingId, deleted, reason);

        return deleted;
    }

    private async Task<int> DeleteLiveAsync(Guid orgId, Guid bookingId, string reason, DateTime now, CancellationToken ct)
    {
        var live = await db.IdentityReviewEvidences
            .IgnoreQueryFilters()
            .Where(evidence => evidence.OrgId == orgId && evidence.BookingId == bookingId && evidence.DeletedAt == null)
            .ToListAsync(ct);
        if (live.Count == 0)
            return 0;

        var deleted = 0;
        try
        {
            deleted = await IdentityReviewEvidenceCleaner.DeleteAsync(storage, live, reason, now, ct);
        }
        finally
        {
            if (live.Any(evidence => evidence.DeletedAt is not null))
                await db.SaveChangesAsync(CancellationToken.None);
        }

        return deleted;
    }

    /// <summary>Encrypts and writes the pictures, then their rows in one save; on a failure the objects written are deleted (best effort) and the rows forgotten.</summary>
    private async Task<int> WriteAsync(ReviewEvidenceInput input, List<EvidencePicture> pictures, DateTime now, int retentionHours, CancellationToken ct)
    {
        var rows = new List<IdentityReviewEvidence>(pictures.Count);
        try
        {
            foreach (var picture in pictures)
            {
                var row = new IdentityReviewEvidence
                {
                    Id = Guid.NewGuid(),
                    OrgId = input.OrgId,
                    BookingId = input.BookingId,
                    GuestId = input.GuestId,
                    CheckInSessionId = input.CheckInSessionId,
                    VerificationLogId = input.VerificationLogId,
                    Kind = picture.Kind,
                    ContentType = picture.ContentType,
                    CreatedAt = now,
                    ExpiresAt = now.AddHours(retentionHours),
                };
                row.StorageKey = StorageKeyOf(row.OrgId, row.BookingId, row.Id);
                rows.Add(row);

                var cipher = Protector(row.Id).Protect(picture.Bytes);
                try
                {
                    row.Sha256 = Convert.ToHexString(SHA256.HashData(cipher));
                    using var content = new MemoryStream(cipher, writable: false);
                    await storage.PutAsync(StorageBucket.Private, row.StorageKey, content, StoredContentType, ct);
                }
                finally
                {
                    Array.Clear(cipher);
                }

                db.Set<IdentityReviewEvidence>().Add(row);
            }

            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var row in rows)
            {
                var entry = db.Entry(row);
                if (entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;

                try
                {
                    await storage.DeleteAsync(StorageBucket.Private, row.StorageKey, CancellationToken.None);
                }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException)
                {
                    logger.LogWarning("Review evidence {EvidenceId}: its object could not be deleted after a failure ({ErrorType}); the purge deletes it", row.Id, cleanup.GetType().Name);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// The crop of the portrait of the document front, as a PNG whose longest side is at most <paramref name="maxSide"/>; null when there is no picture, it cannot be decoded,
    /// no portrait is found, the models are not there or their time is over. The decoded pictures are zeroed before this returns.
    /// </summary>
    private byte[]? PortraitCrop(byte[]? documentFront, int maxSide, Guid sessionId)
    {
        if (documentFront is not { Length: > 0 })
            return null;

        var settings = biometricOptions.Value;
        if (settings.VerificationTimeoutMs <= 0)
            return null;

        RasterImage? document = null;
        RasterImage? rotated = null;
        RasterImage? upright = null;
        RasterImage? crop = null;
        RasterImage? scaled = null;
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.VerificationTimeoutMs));
        try
        {
            document = RasterImage.Decode(documentFront, settings.MaxImagePixels);
            var face = DocumentPortrait.FindOriented(detector, document, settings, budget.Token, out rotated, out var orientation);
            if (face is null)
            {
                logger.LogInformation("Review evidence of session {SessionId}: no portrait found on the document (evidence_no_portrait)", sessionId);
                return null;
            }

            // The face is in the coordinates of the picture it was found in: the document, or a smaller copy turned upright. The crop is cut from the document at full size.
            var source = document;
            var factor = 1.0;
            if (orientation != 1 && rotated is not null)
            {
                upright = document.Oriented(orientation);
                source = upright;
                factor = (double)upright.Width / rotated.Width;
            }

            var marginX = face.Width * PortraitMargin;
            var marginY = face.Height * PortraitMargin;
            var left = Math.Max(0, (int)Math.Floor((face.X - marginX) * factor));
            var top = Math.Max(0, (int)Math.Floor((face.Y - marginY) * factor));
            var right = Math.Min(source.Width, (int)Math.Ceiling((face.X + face.Width + marginX) * factor));
            var bottom = Math.Min(source.Height, (int)Math.Ceiling((face.Y + face.Height + marginY) * factor));
            if (right - left < 1 || bottom - top < 1)
                return null;

            crop = source.Cropped(left, top, right - left, bottom - top);
            scaled = Fit(crop, maxSide);
            return CardPngEncoder.Encode(scaled ?? crop);
        }
        catch (InvalidDataException)
        {
            logger.LogInformation("Review evidence of session {SessionId}: the document cannot be decoded (evidence_document_undecodable)", sessionId);
            return null;
        }
        catch (BiometricUnavailableException)
        {
            logger.LogInformation("Review evidence of session {SessionId}: the models are not available, no crop (evidence_models_unavailable)", sessionId);
            return null;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            logger.LogWarning("Review evidence of session {SessionId}: the portrait was not found in time (evidence_portrait_timeout)", sessionId);
            return null;
        }
        finally
        {
            scaled?.Dispose();
            crop?.Dispose();
            upright?.Dispose();
            rotated?.Dispose();
            document?.Dispose();
        }
    }

    /// <summary>
    /// A frame chosen by the server, as it is kept: the JPEG received without its metadata when it fits <paramref name="maxSide"/> and was sent upright; otherwise a PNG of
    /// the decoded frame (upright), scaled to fit. Null for a frame whose pixels are gone.
    /// </summary>
    private static EvidencePicture? FramePicture(SequenceFrame frame, int maxSide)
    {
        var image = frame.Image;
        if (image.Rgb.Length == 0)
            return null;

        if (Math.Max(image.Width, image.Height) <= maxSide
            && ExifOrientation.Read(frame.Jpeg) == 1
            && JpegMetadata.TryStrip(frame.Jpeg, out var stripped))
        {
            return new EvidencePicture(IdentityEvidenceKind.Frame, JpegContentType, stripped);
        }

        using var scaled = Fit(image, maxSide);
        return new EvidencePicture(IdentityEvidenceKind.Frame, PngContentType, CardPngEncoder.Encode(scaled ?? image));
    }

    /// <summary>A copy of <paramref name="image"/> whose longest side is <paramref name="maxSide"/>, or null when it already fits.</summary>
    private static RasterImage? Fit(RasterImage image, int maxSide)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide)
            return null;

        var scale = (double)maxSide / longest;
        return image.Resized(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
    }

    private IDataProtector Protector(Guid evidenceId) => dataProtection.CreateProtector(ContentPurpose, evidenceId.ToString("N"));

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    /// <summary>One picture to keep, in clear until it is encrypted; zeroed by the caller.</summary>
    private sealed record EvidencePicture(IdentityEvidenceKind Kind, string ContentType, byte[] Bytes);
}

/// <summary>
/// What the closure hands to <see cref="IdentityReviewEvidenceStore.SaveForReviewAsync"/> once a review is committed (E73/F01/US02/T01): the ids of the verification, whether its
/// property keeps no document (the one rule of the flow, ADR-013-R6: true keeps nothing), the picture of the front of the document and the frames of the request, both owned and
/// zeroed by the request, and the frames the server chose for the match.
/// </summary>
public sealed record ReviewEvidenceInput(
    Guid OrgId,
    Guid BookingId,
    Guid CheckInSessionId,
    Guid? GuestId,
    Guid? VerificationLogId,
    bool PropertyKeepsNoDocument,
    byte[]? DocumentFront,
    FrameSequence? Sequence,
    IReadOnlyList<int>? ChosenFrames);

/// <summary>One live evidence of a booking, as the reviewer lists them: ids, the kind and the times, never the picture.</summary>
public sealed record IdentityEvidenceItem(Guid Id, IdentityEvidenceKind Kind, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>One evidence decrypted for its reader: the bytes are the caller's to zero once written to the response.</summary>
public sealed record IdentityEvidenceContent(Guid Id, IdentityEvidenceKind Kind, string ContentType, byte[] Bytes);

/// <summary>The codes of the kinds of an evidence, as the API and the activity log write them.</summary>
public static class IdentityEvidenceKinds
{
    public const string DocumentPortrait = "documentPortrait";
    public const string Frame = "frame";

    /// <summary>The code of <paramref name="kind"/>.</summary>
    public static string Code(IdentityEvidenceKind kind) => kind switch
    {
        IdentityEvidenceKind.DocumentPortrait => DocumentPortrait,
        IdentityEvidenceKind.Frame => Frame,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of evidence"),
    };
}

/// <summary>
/// Deletes evidence kept for a reviewer (E73/F01/US02): the encrypted object in the private bucket, then the row marked deleted with its reason; the row stays as the trace of
/// the deletion. The one place, used by the store (a decision, a new set), the purge (expired, decided, abandoned) and the erasure of a guest. The objects go at once and the
/// rows are only staged: the caller saves. A failure of the storage is thrown; the rows marked before it stay marked in the context, for the caller to save.
/// </summary>
internal static class IdentityReviewEvidenceCleaner
{
    public static async Task<int> DeleteAsync(
        IFileStorage storage,
        IEnumerable<IdentityReviewEvidence> rows,
        string reason,
        DateTime now,
        CancellationToken ct)
    {
        var deleted = 0;
        foreach (var row in rows)
        {
            if (row.DeletedAt is not null)
                continue;

            // A key that is not one (never written by the store) has nothing to delete: the row is marked all the same.
            if (StorageKeys.IsValid(row.StorageKey))
                await storage.DeleteAsync(StorageBucket.Private, row.StorageKey, ct);

            row.DeletedAt = now;
            row.DeletionReason = reason;
            deleted++;
        }

        return deleted;
    }
}
