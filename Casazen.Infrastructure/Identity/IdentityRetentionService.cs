using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The retention and the verifiable deletion of what the self-hosted identity verification keeps (E73/F01/US02/T03, NF-195, decision D9), run every hour by
/// <c>IdentityEvidencePurgeJob</c> while <c>Features:CheckInIdentitySelfHosted</c> or <c>Features:CheckInIdentityReviewEvidence</c> is on (read again at every run). Every
/// category is applied organization by organization, each query naming the organization (a job has no tenant), in batches of <see cref="BatchSize"/> rows:
/// <list type="number">
/// <item><b>evidence past its time</b> (<c>ExpiresAt</c> reached): the object goes, the row is kept marked deleted, reason <c>expired</c>; and, as a safety sweep, the evidence of
/// a booking decided after it was kept (an operator's approval or rejection, or an approval) goes with reason <c>decided</c>, whatever its expiry;</item>
/// <item><b>abandoned sessions</b>: a verification opened more than <c>IdentityVerification:Retention:AbandonedAfterHours</c> ago and never closed, or whose session expired, is
/// closed with the outcome code <c>abandoned</c>, its presence challenge cleared, its evidence deleted (reason <c>abandoned</c>), under the lock of the row of the session as the
/// flow takes it (a request in the middle of an attempt finishes first, and a session it closed is left alone); and the kept pictures of the document of a booking that no session
/// of its own is still using (<see cref="OverdueDocumentImages"/>) are deleted, objects and rows, with the deleter of the erasure and of the receipt (<c>DocumentImageCleaner</c>);</item>
/// <item><b>the checks of the document</b> kept on a session (<c>DocumentCheckJson</c>: flags, hashes, the proofs of the number) are cleared
/// <c>IdentityVerification:Retention:DocumentCheckDays</c> days after the session expired, or after its verification was closed once the session cannot be used any more;</item>
/// <item><b>the verification logs</b> of the bookings whose check-out is older than <c>IdentityVerification:Retention:VerificationLogMonths</c> months are deleted (the readers of
/// the outcome read only the last row of the stays in progress).</item>
/// </list>
/// Idempotent: what was done is not selected again, a second run finds nothing. A failure of one organization does not stop the others; the run is then reported failed
/// (Hangfire runs it again). What the last run did is kept for the status of the platform administrators (<see cref="IdentityRetentionRunLog"/>).
/// <para><b>Privacy</b>: the log has counts, ids and the type of an exception; never a picture, a key with a name, a value of the document or a name.</para>
/// </summary>
public sealed class IdentityRetentionService(
    AppDbContext db,
    IFileStorage storage,
    IFeatureFlags featureFlags,
    IOptions<IdentityVerificationOptions> identityOptions,
    IdentityRetentionRunLog runLog,
    TimeProvider clock,
    ILogger<IdentityRetentionService> logger)
{
    /// <summary>The rows one unit of work takes: the size of a batch, not a rule of the retention.</summary>
    internal const int BatchSize = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly GuestCheckInSessionStatus[] OpenStatuses = [GuestCheckInSessionStatus.Inviato, GuestCheckInSessionStatus.InCompilazione];

    /// <summary>The purge runs: one of the two flags of the self-hosted verification that keep something is on.</summary>
    public bool PurgeOn =>
        featureFlags.IsEnabled(FeatureFlags.CheckInIdentitySelfHosted) || featureFlags.IsEnabled(FeatureFlags.CheckInIdentityReviewEvidence);

    /// <summary>
    /// One run of the retention at <paramref name="utcNow"/>: the counts of what it deleted, per category. Nothing while both flags are off. With a section
    /// <c>IdentityVerification</c> that is not valid only the evidence is purged (its times are on its rows); the rest waits for a valid section, with a warning.
    /// </summary>
    public async Task<IdentityRetentionCounts> PurgeAsync(DateTime utcNow, CancellationToken ct)
    {
        var now = AsUtc(utcNow);
        if (!PurgeOn)
        {
            logger.LogInformation("Identity retention: the self-hosted verification and its evidence are off, nothing to purge");
            return IdentityRetentionCounts.None;
        }

        var options = identityOptions.Value;
        var periods = options.Validate().Count == 0 ? options.Retention : null;
        if (periods is null)
            logger.LogWarning("Identity retention: the section IdentityVerification is not valid; only the evidence is purged (retention_config_invalid)");

        var total = IdentityRetentionCounts.None;
        var failed = 0;
        foreach (var orgId in await OrgsWithWorkAsync(now, periods, ct))
        {
            try
            {
                total += await PurgeOrgAsync(orgId, now, periods, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The type only: a message could carry a key or a value. The other organizations go on; the run is reported failed below.
                failed++;
                db.ChangeTracker.Clear();
                logger.LogError("Identity retention of org {OrgId} failed ({ErrorType}); the next run repeats it", orgId, ex.GetType().Name);
            }
        }

        runLog.Record(new IdentityRetentionRun(now, total, failed));
        logger.LogInformation(
            "Identity retention: {ExpiredEvidence} expired and {DecidedEvidence} decided evidence pictures, {AbandonedSessions} abandoned sessions with {AbandonedEvidence} pictures, " +
            "{DocumentImages} kept document pictures, {DocumentChecks} document checks, {Logs} verification logs; {Failed} organizations failed",
            total.ExpiredEvidence, total.DecidedEvidence, total.AbandonedSessions, total.AbandonedEvidence, total.AbandonedDocumentImages, total.DocumentChecksCleared,
            total.VerificationLogsDeleted, failed);

        if (failed > 0)
            throw new IdentityRetentionFailedException(failed);

        return total;
    }

    /// <summary>
    /// What a platform administrator checks to see that nothing overdue remains (AC-NF195-4): the evidence past its time and not deleted, the kept pictures of the document that no
    /// session is still using (both across every organization), and what the last run of this process did (null after a start, until the first run).
    /// </summary>
    public async Task<IdentityRetentionStatus> StatusAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var options = identityOptions.Value;
        var abandonedAfterHours = options.Validate().Count == 0 ? options.Retention.AbandonedAfterHours : new IdentityRetentionOptions().AbandonedAfterHours;

        var overdueEvidence = await db.IdentityReviewEvidences
            .IgnoreQueryFilters()
            .CountAsync(evidence => evidence.DeletedAt == null && evidence.ExpiresAt <= now, ct);
        var overdueImages = await OverdueDocumentImages(null, now, abandonedAfterHours).CountAsync(ct);
        return new IdentityRetentionStatus(overdueEvidence, overdueImages, runLog.Last);
    }

    /// <summary>
    /// The live kept pictures of a document (<see cref="DocumentImage"/>, not marked deleted) of a booking that no check-in session of its own is still using, of
    /// <paramref name="orgId"/> or of every organization: a session in use is open (sent or being filled in), not expired, its verification neither closed nor approved, and
    /// not opened, or opened less than <paramref name="abandonedAfterHours"/> hours ago. Every way of the flow to a person and every approval deletes them already: what is left is
    /// the picture of an abandoned verification, or of a deletion that failed.
    /// </summary>
    internal IQueryable<DocumentImage> OverdueDocumentImages(Guid? orgId, DateTime now, int abandonedAfterHours)
    {
        var abandonedBefore = now.AddHours(-abandonedAfterHours);
        var inUse = db.GuestCheckInSessions
            .Where(session => OpenStatuses.Contains(session.Status)
                              && session.ExpiresAt > now
                              && session.IdentityClosedAt == null
                              && session.BiometricOutcome != BiometricOutcome.Accept
                              && (session.IdentityOpenedAt == null || session.IdentityOpenedAt > abandonedBefore));
        var images = db.DocumentImages.IgnoreQueryFilters().Where(image => image.DeletedAt == null);
        if (orgId is { } org)
            images = images.Where(image => image.OrgId == org);

        return images.Where(image => !inUse.Any(session => session.OrgId == image.OrgId && session.BookingId == image.BookingId));
    }

    /// <summary>The organizations that have something for a category; the same conditions as the categories, so an organization with nothing is never visited.</summary>
    private async Task<List<Guid>> OrgsWithWorkAsync(DateTime now, IdentityRetentionOptions? periods, CancellationToken ct)
    {
        var orgs = new HashSet<Guid>();
        orgs.UnionWith(await db.IdentityReviewEvidences
            .IgnoreQueryFilters()
            .Where(evidence => evidence.DeletedAt == null)
            .Select(evidence => evidence.OrgId)
            .Distinct()
            .ToListAsync(ct));

        if (periods is not null)
        {
            var abandonedBefore = now.AddHours(-periods.AbandonedAfterHours);
            orgs.UnionWith(await AbandonedSessions(null, now, abandonedBefore).Select(session => session.OrgId).Distinct().ToListAsync(ct));
            orgs.UnionWith(await OverdueDocumentImages(null, now, periods.AbandonedAfterHours).Select(image => image.OrgId).Distinct().ToListAsync(ct));
            orgs.UnionWith(await ExpiredDocumentChecks(null, now, now.AddDays(-periods.DocumentCheckDays)).Select(session => session.OrgId).Distinct().ToListAsync(ct));
            orgs.UnionWith(await OldVerificationLogs(null, LogCutoff(now, periods.VerificationLogMonths)).Select(log => log.OrgId).Distinct().ToListAsync(ct));
        }

        return [.. orgs.Order()];
    }

    private async Task<IdentityRetentionCounts> PurgeOrgAsync(Guid orgId, DateTime now, IdentityRetentionOptions? periods, CancellationToken ct)
    {
        var expired = await DeleteEvidenceAsync(
            () => LiveEvidence(orgId).Where(evidence => evidence.ExpiresAt <= now), IdentityEvidenceDeletionReasons.Expired, now, ct);
        var decided = await DeleteEvidenceAsync(() => DecidedEvidence(orgId), IdentityEvidenceDeletionReasons.Decided, now, ct);
        if (periods is null)
            return IdentityRetentionCounts.None with { ExpiredEvidence = expired, DecidedEvidence = decided };

        var (sessions, sessionEvidence) = await CloseAbandonedSessionsAsync(orgId, now, now.AddHours(-periods.AbandonedAfterHours), ct);
        var images = await DeleteOverdueDocumentImagesAsync(orgId, now, periods.AbandonedAfterHours, ct);
        var checks = await ClearDocumentChecksAsync(orgId, now, now.AddDays(-periods.DocumentCheckDays), ct);
        var logs = await DeleteVerificationLogsAsync(orgId, LogCutoff(now, periods.VerificationLogMonths), ct);
        return new IdentityRetentionCounts(expired, decided, sessions, sessionEvidence, images, checks, logs);
    }

    private IQueryable<IdentityReviewEvidence> LiveEvidence(Guid orgId) =>
        db.IdentityReviewEvidences.IgnoreQueryFilters().Where(evidence => evidence.OrgId == orgId && evidence.DeletedAt == null);

    /// <summary>The live evidence of a booking decided after it was kept: an approval (of the policy or of an operator) or an operator's rejection.</summary>
    private IQueryable<IdentityReviewEvidence> DecidedEvidence(Guid orgId) =>
        LiveEvidence(orgId).Where(evidence => db.BiometricVerificationLogs.Any(log =>
            log.OrgId == orgId
            && log.BookingId == evidence.BookingId
            && log.CreatedAt >= evidence.CreatedAt
            && (log.Outcome == BiometricOutcome.Accept || (log.Method == VerificationMethod.Operator && log.Outcome == BiometricOutcome.Reject))));

    /// <summary>
    /// Deletes, batch after batch, the evidence <paramref name="query"/> selects: the objects, then the rows marked with <paramref name="reason"/>. What was marked before a failure
    /// of the storage is saved before the failure goes on.
    /// </summary>
    private async Task<int> DeleteEvidenceAsync(Func<IQueryable<IdentityReviewEvidence>> query, string reason, DateTime now, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await query().OrderBy(evidence => evidence.ExpiresAt).ThenBy(evidence => evidence.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var deleted = 0;
            try
            {
                deleted = await IdentityReviewEvidenceCleaner.DeleteAsync(storage, batch, reason, now, ct);
            }
            finally
            {
                if (batch.Any(evidence => evidence.DeletedAt is not null))
                    await db.SaveChangesAsync(CancellationToken.None);

                db.ChangeTracker.Clear();
            }

            total += deleted;
            if (deleted == 0 || batch.Count < BatchSize)
                break;
        }

        return total;
    }

    private IQueryable<GuestCheckInSession> AbandonedSessions(Guid? orgId, DateTime now, DateTime abandonedBefore)
    {
        var sessions = db.GuestCheckInSessions
            .IgnoreQueryFilters()
            .Where(session => session.IdentityOpenedAt != null
                              && session.IdentityClosedAt == null
                              && (session.IdentityOpenedAt <= abandonedBefore || session.ExpiresAt <= now));
        return orgId is { } org ? sessions.Where(session => session.OrgId == org) : sessions;
    }

    /// <summary>Closes the abandoned sessions of the organization, batch after batch, and deletes the evidence linked to them: the sessions closed and the pictures deleted.</summary>
    private async Task<(int Sessions, int Evidence)> CloseAbandonedSessionsAsync(Guid orgId, DateTime now, DateTime abandonedBefore, CancellationToken ct)
    {
        int sessions = 0, evidence = 0;
        while (true)
        {
            var batch = await AbandonedSessions(orgId, now, abandonedBefore)
                .OrderBy(session => session.IdentityOpenedAt)
                .ThenBy(session => session.Id)
                .Select(session => session.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var closed = 0;
            foreach (var sessionId in batch)
            {
                if (!await CloseAbandonedAsync(orgId, sessionId, now, abandonedBefore, ct))
                    continue;

                closed++;
                evidence += await DeleteEvidenceAsync(
                    () => LiveEvidence(orgId).Where(row => row.CheckInSessionId == sessionId), IdentityEvidenceDeletionReasons.Abandoned, now, ct);
            }

            sessions += closed;
            if (closed == 0 || batch.Count < BatchSize)
                break;
        }

        return (sessions, evidence);
    }

    /// <summary>
    /// Closes one abandoned session under the lock of its row (<c>SELECT … FOR UPDATE</c> on PostgreSQL, as the flow takes it), read again under the lock: a session that a request
    /// closed meanwhile, or that is not abandoned any more, is left as it is. The presence challenge (and the pose of the legacy selfie) is cleared.
    /// </summary>
    private async Task<bool> CloseAbandonedAsync(Guid orgId, Guid sessionId, DateTime now, DateTime abandonedBefore, CancellationToken ct)
    {
        var postgres = db.Database.IsNpgsql();
        await using var transaction = postgres ? await db.Database.BeginTransactionAsync(ct) : null;
        if (postgres)
            await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "GuestCheckInSessions" WHERE "Id" = {sessionId} FOR UPDATE""").ToListAsync(ct);

        var session = await db.GuestCheckInSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(row => row.Id == sessionId && row.OrgId == orgId, ct);
        if (session is null
            || session.IdentityOpenedAt is null
            || session.IdentityClosedAt is not null
            || (session.IdentityOpenedAt > abandonedBefore && session.ExpiresAt > now))
        {
            db.ChangeTracker.Clear();
            return false;
        }

        if (ReadCheck(session.DocumentCheckJson) is { } check && (check.Presence is not null || check.Pose is not null))
            session.DocumentCheckJson = JsonSerializer.Serialize(check with { Presence = null, Pose = null }, Json);

        session.IdentityClosedAt = now;
        session.IdentityOutcomeCode = IdentityOutcomeCodes.Abandoned;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        db.ChangeTracker.Clear();
        logger.LogInformation("Identity verification of session {SessionId} closed as abandoned", sessionId);
        return true;
    }

    /// <summary>Deletes the overdue kept pictures of the documents of the organization (objects and rows), batch after batch: the rows deleted.</summary>
    private async Task<int> DeleteOverdueDocumentImagesAsync(Guid orgId, DateTime now, int abandonedAfterHours, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await OverdueDocumentImages(orgId, now, abandonedAfterHours)
                .OrderBy(image => image.UploadedAt)
                .ThenBy(image => image.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            await DocumentImageCleaner.DeleteAsync(db, storage, logger, batch, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// The sessions whose checks of the document are past their time: expired before <paramref name="before"/>, or closed before it and not usable any more (expired, or replaced by
    /// a new link). A session still open keeps them: the final submission compares the number typed with the proof of the one read.
    /// </summary>
    private IQueryable<GuestCheckInSession> ExpiredDocumentChecks(Guid? orgId, DateTime now, DateTime before)
    {
        var sessions = db.GuestCheckInSessions
            .IgnoreQueryFilters()
            .Where(session => session.DocumentCheckJson != null
                              && (session.ExpiresAt <= before
                                  || (session.IdentityClosedAt != null
                                      && session.IdentityClosedAt <= before
                                      && (session.ExpiresAt <= now || session.Status == GuestCheckInSessionStatus.Scaduto))));
        return orgId is { } org ? sessions.Where(session => session.OrgId == org) : sessions;
    }

    private async Task<int> ClearDocumentChecksAsync(Guid orgId, DateTime now, DateTime before, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await ExpiredDocumentChecks(orgId, now, before).OrderBy(session => session.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
                break;

            foreach (var session in batch)
            {
                session.DocumentCheckJson = null;
                session.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize)
                break;
        }

        return total;
    }

    private IQueryable<BiometricVerificationLog> OldVerificationLogs(Guid? orgId, DateTime checkOutBefore)
    {
        var logs = db.BiometricVerificationLogs
            .IgnoreQueryFilters()
            .Where(log => db.Bookings.Any(booking => booking.Id == log.BookingId && booking.OrgId == log.OrgId && booking.CheckOutDate < checkOutBefore));
        return orgId is { } org ? logs.Where(log => log.OrgId == org) : logs;
    }

    private async Task<int> DeleteVerificationLogsAsync(Guid orgId, DateTime checkOutBefore, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await OldVerificationLogs(orgId, checkOutBefore).OrderBy(log => log.CreatedAt).ThenBy(log => log.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
                break;

            db.BiometricVerificationLogs.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize)
                break;
        }

        return total;
    }

    /// <summary>The first day whose check-outs keep their logs: today in Europe/Rome, <paramref name="months"/> months ago (midnight UTC of that date).</summary>
    private static DateTime LogCutoff(DateTime now, int months) => RomeCalendar.TodayAt(new DateTimeOffset(now, TimeSpan.Zero)).AddMonths(-months);

    private static StoredDocumentCheck? ReadCheck(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StoredDocumentCheck>(json, Json);
        }
        catch (JsonException)
        {
            // A value that is not one of ours: nothing to clear in it; its time comes with DocumentCheckDays.
            return null;
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>
/// What one run of the identity retention deleted (E73/F01/US02/T03), per category: the evidence past its time, the evidence of a booking decided, the abandoned sessions
/// closed and their evidence, the kept pictures of the documents nobody uses any more, the checks of the document cleared and the verification logs deleted. Counts only.
/// </summary>
public sealed record IdentityRetentionCounts(
    int ExpiredEvidence,
    int DecidedEvidence,
    int AbandonedSessions,
    int AbandonedEvidence,
    int AbandonedDocumentImages,
    int DocumentChecksCleared,
    int VerificationLogsDeleted)
{
    public static IdentityRetentionCounts None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public static IdentityRetentionCounts operator +(IdentityRetentionCounts left, IdentityRetentionCounts right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new IdentityRetentionCounts(
            left.ExpiredEvidence + right.ExpiredEvidence,
            left.DecidedEvidence + right.DecidedEvidence,
            left.AbandonedSessions + right.AbandonedSessions,
            left.AbandonedEvidence + right.AbandonedEvidence,
            left.AbandonedDocumentImages + right.AbandonedDocumentImages,
            left.DocumentChecksCleared + right.DocumentChecksCleared,
            left.VerificationLogsDeleted + right.VerificationLogsDeleted);
    }
}

/// <summary>One run of the identity retention: when it ran (UTC), what it deleted and how many organizations failed.</summary>
public sealed record IdentityRetentionRun(DateTime RanAt, IdentityRetentionCounts Counts, int FailedOrgs);

/// <summary>What the status of the platform administrators shows of the retention: what is overdue now and the last run of this process (null until the first one).</summary>
public sealed record IdentityRetentionStatus(int OverdueEvidence, int OverdueDocumentImages, IdentityRetentionRun? LastRun);

/// <summary>
/// The last run of the identity retention in this process (E73/F01/US02/T03): the job and the API share the process (Hangfire runs in the web service), so the status of the
/// platform administrators reads it here; after a start it is empty until the hourly run. The overdue counts, read from the database, are the proof; this is the context.
/// </summary>
public sealed class IdentityRetentionRunLog
{
    private IdentityRetentionRun? _last;

    public IdentityRetentionRun? Last => Volatile.Read(ref _last);

    public void Record(IdentityRetentionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Volatile.Write(ref _last, run);
    }
}

/// <summary>A run of the identity retention that could not finish for some organizations: Hangfire runs it again. The message has a count only.</summary>
public sealed class IdentityRetentionFailedException(int failedOrgs)
    : InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The identity retention failed for {failedOrgs} organization(s); see the log of the run."))
{
    public int FailedOrgs { get; } = failedOrgs;
}
