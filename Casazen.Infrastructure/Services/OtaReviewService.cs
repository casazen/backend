using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOtaReviewService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The org is the call's.</b> Every query names <see cref="IOctorateOrgScope.OrgId"/> (the org of the request, or the org a job opened with <c>Use</c>), on top of the tenant
/// filter of the context: a review, a booking or a mapping of another org is never read, listed or answered (AC-NF69-2, tenant rule). The reach of a collaborator «Solo alcuni» is
/// applied in SQL (<c>InScope</c>) where a <see cref="HostScope"/> is given; the writes are authorized by the controller on the property of the row, which answers 404 outside the reach.</item>
/// <item><b>A read never invents a review.</b> The row of a booking is made when the channel manager answers (or fails with a code the sync has to remember); a channel with no
/// integration for reviews gets no row, no call and no text, whoever asks. A read that fails stores its code and the time of the next try, and never a text.</item>
/// <item><b>A write is saved before it leaves, and settled after.</b> The text and the key are saved as <see cref="OtaReviewActionState.ManualVerify"/> under the lock of the booking, in
/// their own commit; the call to Octorate is made with no lock and no transaction held; its answer is stored under the lock again. The same key finds the row and sends nothing;
/// a call whose outcome is not known stays <c>ManualVerify</c> and nothing in this class sends it again (OCT-13): the next read of the booking finds the reply on the channel and completes
/// it, or turns it into an error after an hour. A 429 means nothing happened: the row is taken back and the 429 reaches the caller, who may send again with the same key.</item>
/// <item><b>Logs</b> carry ids, states and codes: never a word of a guest or of a host, never the reference of the booking on the channel manager.</item>
/// </list>
/// </remarks>
public sealed partial class OtaReviewService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IChannelMappingService channelMapping,
    IFeatureFlags featureFlags,
    IDomainEventRecorder domainEvents,
    ILogger<OtaReviewService> logger,
    TimeProvider? timeProvider = null,
    IPushNotificationService? push = null) : IOtaReviewService
{
    /// <summary>The most ratings a host's review carries.</summary>
    internal const int MaxHostReviewRatings = 12;

    private const int MaxCategoryLength = 64;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The rows of the mapping of each org, read once for the life of the service (a request, or a run of the job): the listing of a property does not change within it.</summary>
    private readonly Dictionary<Guid, IReadOnlyList<ChannelMappingRow>> _mappings = [];

    // ─── The read ───

    public async Task<OtaReviewSyncResult> SyncBookingAsync(Guid bookingId, CancellationToken ct = default)
    {
        var orgId = orgScope.OrgId;
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new OtaReviewSyncResult(OtaReviewSyncOutcome.ConnectorOff);

        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .Select(b => new BookingFacts(
                b.Id,
                b.PropertyId,
                b.Source,
                b.Status,
                b.NoShowAt != null,
                b.CheckOutDate,
                b.OctorateReservationId,
                b.Guest.DataAnonymizedDate != null || b.Guest.IsDeleted))
            .FirstOrDefaultAsync(ct);
        if (booking is null)
            return new OtaReviewSyncResult(OtaReviewSyncOutcome.BookingNotFound);

        // A channel with no integration for reviews is never read and never gets a row (OCT-12, S6).
        if (OtaChannelMapper.Of(booking.Source) is not { } channel || !OtaReviewCapabilities.For(channel).CanRead)
            return new OtaReviewSyncResult(OtaReviewSyncOutcome.ChannelNotIntegrated);

        // A stay that does not happen (a request, a cancelled one, a no-show) has nothing to review: the one definition of an active stay.
        if (!AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) || booking.NoShow
            || string.IsNullOrWhiteSpace(booking.OctorateReservationId))
        {
            return new OtaReviewSyncResult(OtaReviewSyncOutcome.NotReadable);
        }

        var accommodation = await AccommodationOfAsync(orgId, booking.PropertyId, ct);
        if (accommodation is null)
            return new OtaReviewSyncResult(OtaReviewSyncOutcome.NotReadable);

        var refer = booking.OctorateReservationId.Trim();
        OctorateReview answer;
        try
        {
            using (orgScope.Use(orgId))
                answer = await client.GetReviewAsync(accommodation, refer, ct);
        }
        catch (OctorateRateLimitException)
        {
            // The calls of the org are used up: nothing was read and nothing is stored; the caller stops and the booking is read at the next run.
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OctorateException ex)
        {
            return await StoreFailureAsync(orgId, booking, channel, refer, ex.Code, ct);
        }
        catch (ArgumentException)
        {
            // A reference or an accommodation the client could not take: nothing left CasaZen.
            return await StoreFailureAsync(orgId, booking, channel, refer, OctorateRequestRejectedException.RejectedCode, ct);
        }

        return await StoreAsync(orgId, booking, channel, refer, answer, ct);
    }

    private async Task<OtaReviewSyncResult> StoreAsync(
        Guid orgId,
        BookingFacts booking,
        OtaChannel channel,
        string refer,
        OctorateReview answer,
        CancellationToken ct)
    {
        var now = Now();
        OtaReviewApplyResult applied;
        OtaReview row;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OtaReview, LockKey(orgId, booking.Id))))
        {
            row = await LoadOrCreateAsync(orgId, booking, channel, refer, now, ct);
            applied = OtaReviewMapper.Apply(row, answer, booking.CheckOutDate, booking.GuestErased, now);

            // The first time the guest's review is out: one event for each review, in the same save, ids only (ADR-022-R21). The routines of the host that wait for a review start from it.
            if (applied.NewlyPublished)
            {
                domainEvents.Record(
                    DomainEventType.ReviewReceived,
                    row.Id,
                    new ReviewReceivedPayload(row.Id, booking.Id, booking.PropertyId),
                    ReviewReceivedPayload.OtaDedupKeyOf(row.Id),
                    orgId);
            }

            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        logger.LogInformation(
            "Reviews of booking {BookingId} (org {OrgId}) read: published {Published}, reply {ReplyState}, host review {HostReviewState}, window {Window}",
            booking.Id, orgId, row.GuestReviewPublished, row.HostReplyState, row.HostReviewState, row.HostReviewWindow);

        await TellHostsAsync(row, applied, ct);
        return new OtaReviewSyncResult(OtaReviewSyncOutcome.Synced, null, applied.NewlyPublished);
    }

    private async Task<OtaReviewSyncResult> StoreFailureAsync(
        Guid orgId,
        BookingFacts booking,
        OtaChannel channel,
        string refer,
        string code,
        CancellationToken ct)
    {
        var now = Now();
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OtaReview, LockKey(orgId, booking.Id))))
        {
            var row = await LoadOrCreateAsync(orgId, booking, channel, refer, now, ct);
            row.LastSyncedAt = now;
            row.LastSyncError = Clip(code, OtaReview.ErrorCodeMaxLength);
            row.PollCount++;
            row.NextPollAt = OtaReviewPollSchedule.NextPollAfterFailure(
                now,
                booking.CheckOutDate,
                code,
                unconfirmedAction: row.HostReplyState == OtaReviewActionState.ManualVerify || row.HostReviewState == OtaReviewActionState.ManualVerify);
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        // A code and the ids, never what the channel manager said: the host sees the state on the booking, and the next read tries again.
        logger.LogWarning("Reviews of booking {BookingId} (org {OrgId}) could not be read: {Code}", booking.Id, orgId, code);
        return new OtaReviewSyncResult(OtaReviewSyncOutcome.Failed, code);
    }

    /// <summary>
    /// The row of a review as the database holds it now, tracked, for the code that holds the lock of its booking: a row the context already tracks is read again (the context is
    /// the one of the request, and a service that ran before may have left an older copy of the row in it, which would hide what another writer saved). <c>null</c> for a row of
    /// another org, or one that is gone.
    /// </summary>
    private async Task<OtaReview?> FreshRowAsync(Guid orgId, Guid reviewId, CancellationToken ct)
    {
        var tracked = db.OtaReviews.Local.FirstOrDefault(r => r.Id == reviewId && r.OrgId == orgId);
        if (tracked is not null)
        {
            await db.Entry(tracked).ReloadAsync(ct);
            return db.Entry(tracked).State == EntityState.Detached ? null : tracked;
        }

        return await db.OtaReviews.FirstOrDefaultAsync(r => r.OrgId == orgId && r.Id == reviewId, ct);
    }

    /// <summary>The row of the booking and channel under the lock the caller holds, or a new one added to the context (never added through a collection of a parent).</summary>
    private async Task<OtaReview> LoadOrCreateAsync(Guid orgId, BookingFacts booking, OtaChannel channel, string refer, DateTime now, CancellationToken ct)
    {
        var row = db.OtaReviews.Local.FirstOrDefault(r => r.OrgId == orgId && r.BookingId == booking.Id && r.Channel == channel);
        if (row is not null)
            await db.Entry(row).ReloadAsync(ct);

        row ??= await db.OtaReviews.FirstOrDefaultAsync(r => r.OrgId == orgId && r.BookingId == booking.Id && r.Channel == channel, ct);
        if (row is null || db.Entry(row).State == EntityState.Detached)
        {
            row = new OtaReview
            {
                OrgId = orgId,
                PropertyId = booking.PropertyId,
                BookingId = booking.Id,
                Channel = channel,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OtaReviews.Add(row);
        }

        row.PropertyId = booking.PropertyId;
        row.OctorateRefer = Clip(refer, OtaReview.OctorateReferMaxLength);
        return row;
    }

    // ─── What the host reads ───

    public async Task<BookingReviewsView> ListForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var booking = await ReachableBookingAsync(scope, bookingId, ct);
        var channelCapability = OtaReviewCapabilities.OfSource(booking.Source);
        var channelView = new OtaReviewCapabilitiesView(
            channelCapability.CanRead,
            channelCapability.CanReply,
            channelCapability.CanHostReview,
            channelCapability.Reason);

        var items = new List<BookingReviewItem>();

        // The review of the booking site: the host reads it and answers it once, as it always could.
        var direct = await db.Reviews
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.BookingId == bookingId && r.Status != ReviewStatus.Removed)
            .Select(r => new { r.Id, r.Rating, r.Text, r.PublishedAt, Answered = r.HostReply != null })
            .FirstOrDefaultAsync(ct);
        if (direct is not null)
        {
            items.Add(new BookingReviewItem(
                MessageThreadKey.ForReview(direct.Id),
                MessageThreadSource.Review,
                MessageHubChannel.Direct,
                direct.Rating,
                ReviewRules.Preview(direct.Text),
                direct.PublishedAt,
                direct.Answered ? OtaReviewActionState.Sent : OtaReviewActionState.None,
                DirectCapabilities(direct.Answered)));
        }

        // The review of a channel: only a published one is a review (the row of a booking that was read and has none is the state of the read, not an item).
        var rows = await db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.BookingId == bookingId)
            .ToListAsync(ct);
        foreach (var row in rows.Where(r => r.GuestReviewPublished))
        {
            items.Add(new BookingReviewItem(
                MessageThreadKey.ForOtaReview(row.Id),
                MessageThreadSource.OtaReview,
                (MessageHubChannel)(int)row.Channel,
                row.GuestRating,
                ReviewRules.Preview(row.GuestReviewText),
                row.GuestReviewPublishedAt,
                row.HostReplyState,
                await CapabilitiesOfAsync(row, ct)));
        }

        var latest = rows.OrderByDescending(r => r.LastSyncedAt).FirstOrDefault();
        var canRefreshAt = latest?.LastSyncedAt is { } read && Now() < read + OtaReviewPollSchedule.RefreshInterval
            ? read + OtaReviewPollSchedule.RefreshInterval
            : (DateTime?)null;
        return new BookingReviewsView(
            bookingId,
            ChannelOf(booking.Source),
            channelView,
            items,
            latest?.LastSyncedAt,
            latest?.LastSyncError,
            canRefreshAt);
    }

    public async Task<BookingReviewsView> RefreshAsync(HostScope scope, Guid bookingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var booking = await ReachableBookingAsync(scope, bookingId, ct);

        // Only a booking of a channel the channel manager integrates for reviews is read: the others have nothing to refresh.
        if (booking.Source != BookingSource.Airbnb)
            throw new DomainRuleException(OtaReviewErrorCodes.ChannelNotIntegrated, "OtaReviewChannelNotIntegrated");

        var now = Now();
        var lastRead = await db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.BookingId == bookingId)
            .Select(r => r.LastSyncedAt)
            .OrderByDescending(at => at)
            .FirstOrDefaultAsync(ct);
        if (lastRead is { } read && now < read + OtaReviewPollSchedule.RefreshInterval)
        {
            var wait = (int)Math.Ceiling((read + OtaReviewPollSchedule.RefreshInterval - now).TotalSeconds);
            throw new DomainTooManyRequestsException(OtaReviewErrorCodes.RefreshTooSoon, "OtaReviewRefreshTooSoon")
            {
                Extensions = new Dictionary<string, object?> { ["retryAfterSeconds"] = wait },
            };
        }

        var result = await SyncBookingAsync(bookingId, ct);
        switch (result.Outcome)
        {
            case OtaReviewSyncOutcome.ConnectorOff:
                throw OctorateAuthException.NotConnected();
            case OtaReviewSyncOutcome.NotReadable:
                throw new DomainConflictException(OtaReviewErrorCodes.MappingNotConfirmed, "MessageMappingNotConfirmed");
        }

        return await ListForBookingAsync(scope, bookingId, ct);
    }

    public async Task<OtaReviewView> GetAsync(HostScope scope, Guid reviewId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var found = await db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.Id == reviewId)
            .InScope(scope)
            .Select(r => new { Row = r, PropertyName = r.Property.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw ReviewNotFound();

        return await ViewOfAsync(found.Row, found.PropertyName, ct);
    }

    public Task<Guid?> FindPropertyIdAsync(Guid reviewId, CancellationToken ct = default)
    {
        var orgId = orgScope.OrgId;
        return db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.Id == reviewId)
            .Select(r => (Guid?)r.PropertyId)
            .FirstOrDefaultAsync(ct);
    }

    // ─── What the host sends ───

    public async Task<OtaReviewActionResult> ReplyAsync(Guid reviewId, string? text, string? idempotencyKey, string? userId, CancellationToken ct = default)
    {
        var key = KeyOrThrow(idempotencyKey);
        var reply = ReviewRules.NormalizeText(text);
        if (reply is null || reply.Length > ReviewRules.ReplyMaxLength)
            throw new DomainRuleException(OtaReviewErrorCodes.ReplyInvalid, "ReviewReplyInvalid");

        return await PerformAsync(ActionKind.Reply, reviewId, key, userId, reply, null, ct);
    }

    public async Task<OtaReviewActionResult> SendHostReviewAsync(
        Guid reviewId,
        OtaHostReviewCommand? command,
        string? idempotencyKey,
        string? userId,
        CancellationToken ct = default)
    {
        var key = KeyOrThrow(idempotencyKey);
        var (review, stored) = HostReviewOf(command);
        return await PerformAsync(ActionKind.HostReview, reviewId, key, userId, stored, review, ct);
    }

    private async Task<OtaReviewActionResult> PerformAsync(
        ActionKind kind,
        Guid reviewId,
        string key,
        string? userId,
        string payload,
        OctorateHostReviewCommand? hostReview,
        CancellationToken ct)
    {
        var orgId = orgScope.OrgId;
        var bookingId = await db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.Id == reviewId)
            .Select(r => (Guid?)r.BookingId)
            .FirstOrDefaultAsync(ct)
            ?? throw ReviewNotFound();

        // One: the row, the checks and the save of the request, under the lock of the booking, committed before anything leaves.
        string accommodation;
        string refer;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OtaReview, LockKey(orgId, bookingId))))
        {
            var row = await FreshRowAsync(orgId, reviewId, ct) ?? throw ReviewNotFound();

            // The same key is the same action: what was stored is the answer, and nothing is sent again, whatever the text of this request says.
            if (string.Equals(KeyOf(row, kind), key, StringComparison.Ordinal))
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);

                return await ResultOfAsync(row, kind, replayed: true, ct);
            }

            var capability = OtaReviewCapabilities.For(row.Channel);
            if (kind == ActionKind.Reply ? !capability.CanReply : !capability.CanHostReview)
                throw new DomainRuleException(OtaReviewErrorCodes.ChannelNotSupported, "OtaReviewChannelNotSupported");

            if (kind == ActionKind.Reply && !row.GuestReviewPublished)
                throw new DomainRuleException(OtaReviewErrorCodes.NotPublished, "OtaReviewNotPublished");

            switch (StateOf(row, kind))
            {
                case OtaReviewActionState.Sent:
                    throw new DomainConflictException(
                        kind == ActionKind.Reply ? OtaReviewErrorCodes.ReplyAlreadySent : OtaReviewErrorCodes.HostReviewAlreadySent,
                        kind == ActionKind.Reply ? "OtaReviewReplyAlreadySent" : "OtaReviewHostReviewAlreadySent");
                case OtaReviewActionState.ManualVerify:
                    throw new DomainConflictException(OtaReviewErrorCodes.ActionUnconfirmed, "OtaReviewActionUnconfirmed");
            }

            // The host's own review is written while the window of the channel is open: once it was sent the window is over, and that is «already sent» (the state above), not «closed».
            if (kind == ActionKind.HostReview && row.HostReviewWindow != HostReviewWindow.Open)
                throw new DomainConflictException(OtaReviewErrorCodes.WindowClosed, "OtaReviewWindowClosed");

            // OCT-04: nothing is written to a channel for a property whose listing nobody decided, and nothing is sent when the connector is off.
            accommodation = await AccommodationOfAsync(orgId, row.PropertyId, ct) ?? string.Empty;
            if (accommodation.Length == 0 || await channelMapping.IsWriteBlockedAsync(row.PropertyId, ct))
                throw new DomainConflictException(OtaReviewErrorCodes.MappingNotConfirmed, "MessageMappingNotConfirmed");

            if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
                throw OctorateAuthException.NotConnected();

            // The text and the key first, as an outcome that is not known: a process that dies from here on leaves exactly what it should (it may or may not have left).
            var now = Now();
            Begin(row, kind, payload, key, now);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            refer = row.OctorateRefer;
        }

        // Two: the call, with no lock and no transaction held. Never repeated here (OCT-13).
        OtaReviewActionState outcome;
        string? errorCode = null;
        DomainConflictException? refused = null;
        try
        {
            using (orgScope.Use(orgId))
            {
                if (kind == ActionKind.Reply)
                    await client.SendHostReplyAsync(accommodation, refer, payload, ct);
                else
                    await client.SendHostReviewAsync(accommodation, refer, hostReview!, ct);
            }

            outcome = OtaReviewActionState.Sent;
        }
        catch (OctorateRateLimitException)
        {
            // Nothing happened (the bucket of the org was empty, or Octorate answered 429 after the waits): the attempt is taken back, so that the same key can be sent when the
            // limit is over, and the 429 reaches the caller.
            await SettleAsync(orgId, reviewId, bookingId, kind, key, OtaReviewActionState.None, null, discard: true, CancellationToken.None);
            throw;
        }
        catch (OctorateServerException ex)
        {
            // No answer, a 5xx, or an answer that cannot be read: the action may have left. It is not sent again here (OCT-13).
            logger.LogWarning(
                "Review {ReviewId} (org {OrgId}): the outcome of the {Action} is not known ({Code}); it waits for the next read of the channel",
                reviewId, orgId, kind, ex.Code);
            outcome = OtaReviewActionState.ManualVerify;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller left while the action was on its way: just as unknown, and it stays as it was saved.
            await SettleAsync(orgId, reviewId, bookingId, kind, key, OtaReviewActionState.ManualVerify, null, discard: false, CancellationToken.None);
            throw;
        }
        catch (OctorateReviewActionNotAllowedException ex)
        {
            // Not allowed, or already done: the channel manager says no. The next read of the booking shows which of the two it was.
            logger.LogWarning("Review {ReviewId} (org {OrgId}): the {Action} was refused as not allowed or already done", reviewId, orgId, kind);
            outcome = OtaReviewActionState.Error;
            errorCode = ex.Code;
            refused = new DomainConflictException(OtaReviewErrorCodes.ActionNotAllowed, "OtaReviewActionNotAllowed");
        }
        catch (OctorateException ex)
        {
            // A typed refusal: not taken by the channel manager. The state says so, with the code.
            logger.LogWarning("Review {ReviewId} (org {OrgId}): the {Action} was not taken ({Code}, HTTP {Status})", reviewId, orgId, kind, ex.Code, ex.OctorateStatusCode);
            outcome = OtaReviewActionState.Error;
            errorCode = ex.Code;
        }
        catch (ArgumentException)
        {
            // An id or a rating the client could not take: nothing left CasaZen.
            logger.LogWarning("Review {ReviewId} (org {OrgId}): the {Action} was not sent, the data of the request is not valid", reviewId, orgId, kind);
            outcome = OtaReviewActionState.Error;
            errorCode = OctorateRequestRejectedException.RejectedCode;
        }

        var settled = await SettleAsync(orgId, reviewId, bookingId, kind, key, outcome, errorCode, discard: false, CancellationToken.None);
        logger.LogInformation("Review {ReviewId} (org {OrgId}): the {Action} is {State}", reviewId, orgId, kind, outcome);

        if (refused is not null)
            throw refused;

        return await ResultOfAsync(settled, kind, replayed: false, ct);
    }

    /// <summary>
    /// Stores what came of the call under the lock of the booking: the row is read again, and only the attempt that this key opened is settled (a row another key took over is
    /// left as it is). <paramref name="discard"/> takes the attempt back (a 429): no text, no key, as if nothing was asked.
    /// </summary>
    private async Task<OtaReview> SettleAsync(
        Guid orgId,
        Guid reviewId,
        Guid bookingId,
        ActionKind kind,
        string key,
        OtaReviewActionState outcome,
        string? errorCode,
        bool discard,
        CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OtaReview, LockKey(orgId, bookingId)));
        var row = await FreshRowAsync(orgId, reviewId, ct) ?? throw ReviewNotFound();
        if (string.Equals(KeyOf(row, kind), key, StringComparison.Ordinal))
        {
            var now = Now();
            if (discard)
                Revert(row, kind);
            else
                Settle(row, kind, outcome, errorCode, now);

            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return row;
    }

    private async Task<OtaReviewActionResult> ResultOfAsync(OtaReview row, ActionKind kind, bool replayed, CancellationToken ct)
    {
        var name = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == row.OrgId && p.Id == row.PropertyId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);
        var view = await ViewOfAsync(row, name, ct);
        return new OtaReviewActionResult(
            view,
            StateOf(row, kind),
            replayed,
            kind == ActionKind.Reply ? row.HostReplyError : row.HostReviewError);
    }

    // ─── The view of a row ───

    private async Task<OtaReviewView> ViewOfAsync(OtaReview row, string? propertyName, CancellationToken ct)
    {
        var hostReview = ReadHostReview(row);
        return new OtaReviewView(
            row.Id,
            MessageThreadKey.ForOtaReview(row.Id),
            row.PropertyId,
            propertyName,
            row.BookingId,
            row.Channel,
            row.GuestReviewPublished,
            row.GuestReviewPublishedAt,
            row.GuestRating,
            OtaReviewMapper.ReadRatingsJson(row.GuestCategoryRatingsJson),
            row.GuestReviewPublished ? row.GuestReviewText : null,
            new ReviewReplyView(row.HostReplyState, row.HostReplyText, row.HostReplySentAt, row.HostReplyError),
            new OtaHostReviewView(
                row.HostReviewWindow,
                row.HostReviewState,
                hostReview?.Ratings ?? [],
                hostReview?.Text,
                hostReview?.PrivateText,
                row.HostReviewSentAt,
                row.HostReviewError),
            row.LastSyncedAt,
            row.LastSyncError,
            await CapabilitiesOfAsync(row, ct));
    }

    /// <summary>
    /// What a review allows its host now: the table of its channel, narrowed by the connector and the listing of its property (nothing is written to a channel for a property whose listing
    /// nobody decided, OCT-04) and by what the review is (published, not answered yet, the window open, the last attempt not waiting for a check).
    /// </summary>
    private async Task<OtaReviewCapabilitiesView> CapabilitiesOfAsync(OtaReview row, CancellationToken ct)
    {
        var channel = OtaReviewCapabilities.For(row.Channel);
        if (!channel.CanRead)
            return new OtaReviewCapabilitiesView(false, false, false, channel.Reason);

        string? reason = null;
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            reason = OctorateAuthException.NotConnectedCode;
        else if (await AccommodationOfAsync(row.OrgId, row.PropertyId, ct) is null || await channelMapping.IsWriteBlockedAsync(row.PropertyId, ct))
            reason = OtaReviewErrorCodes.MappingNotConfirmed;

        string? replyReason = reason;
        if (replyReason is null)
        {
            replyReason = !row.GuestReviewPublished
                ? OtaReviewErrorCodes.NotPublished
                : row.HostReplyState switch
                {
                    OtaReviewActionState.Sent => OtaReviewErrorCodes.ReplyAlreadySent,
                    OtaReviewActionState.ManualVerify => OtaReviewErrorCodes.ActionUnconfirmed,
                    _ => null,
                };
        }

        string? hostReviewReason = reason;
        if (hostReviewReason is null)
        {
            hostReviewReason = row.HostReviewState switch
            {
                OtaReviewActionState.Sent => OtaReviewErrorCodes.HostReviewAlreadySent,
                OtaReviewActionState.ManualVerify => OtaReviewErrorCodes.ActionUnconfirmed,
                _ => row.HostReviewWindow == HostReviewWindow.Open ? null : OtaReviewErrorCodes.WindowClosed,
            };
        }

        return new OtaReviewCapabilitiesView(
            true,
            channel.CanReply && replyReason is null,
            channel.CanHostReview && hostReviewReason is null,
            reason,
            reason is null ? replyReason : null,
            reason is null ? hostReviewReason : null);
    }

    /// <summary>The reply of a review of the booking site: allowed once, until the host answers; no host's review exists for it.</summary>
    internal static OtaReviewCapabilitiesView DirectCapabilities(bool answered) =>
        new(true, !answered, false, null, answered ? "review_reply_already_sent" : null, "host_review_not_supported");

    // ─── Small things ───

    private async Task<BookingFacts> ReachableBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct) =>
        await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId)
            .InScope(scope)
            .Select(b => new BookingFacts(
                b.Id,
                b.PropertyId,
                b.Source,
                b.Status,
                b.NoShowAt != null,
                b.CheckOutDate,
                b.OctorateReservationId,
                b.Guest.DataAnonymizedDate != null || b.Guest.IsDeleted))
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = OtaReviewErrorCodes.BookingNotFound, MessageKey = "BookingNotFound" };

    /// <summary>The accommodation of the confirmed listing of a property; <c>null</c> while the property has none.</summary>
    private async Task<string?> AccommodationOfAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        if (!_mappings.TryGetValue(orgId, out var rows))
        {
            rows = await channelMapping.ListAsync(orgId, ct);
            _mappings[orgId] = rows;
        }

        return rows
            .FirstOrDefault(r => r.Status == OctorateMappingStatus.Confirmed && r.Property?.Id == propertyId)
            ?.OctorateAccommodationId;
    }

    private static MessageHubChannel ChannelOf(BookingSource source) =>
        source == BookingSource.Direct
            ? MessageHubChannel.Direct
            : OtaChannelMapper.Of(source) is { } channel ? (MessageHubChannel)(int)channel : MessageHubChannel.Direct;

    private static string KeyOrThrow(string? idempotencyKey) =>
        GuestMessageRules.NormalizeIdempotencyKey(idempotencyKey)
        ?? throw new DomainRuleException(OtaReviewErrorCodes.IdempotencyKeyInvalid, "IdempotencyKeyInvalid");

    private static string LockKey(Guid orgId, Guid bookingId) => $"{orgId:N}:{bookingId:N}";

    private static NotFoundException ReviewNotFound() =>
        new("The review was not found or is not within reach") { Code = OtaReviewErrorCodes.NotFound, MessageKey = "OtaReviewNotFound" };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private DateTime Now()
    {
        var utc = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    // ─── The two actions, one after the other ───

    private enum ActionKind
    {
        Reply,
        HostReview,
    }

    private static OtaReviewActionState StateOf(OtaReview row, ActionKind kind) => kind == ActionKind.Reply ? row.HostReplyState : row.HostReviewState;

    private static string? KeyOf(OtaReview row, ActionKind kind) => kind == ActionKind.Reply ? row.HostReplyIdempotencyKey : row.HostReviewIdempotencyKey;

    /// <summary>The attempt, saved before it leaves: its words and its key, as an outcome that is not known.</summary>
    private static void Begin(OtaReview row, ActionKind kind, string payload, string key, DateTime now)
    {
        if (kind == ActionKind.Reply)
        {
            row.HostReplyText = payload;
            row.HostReplyIdempotencyKey = key;
            row.HostReplyState = OtaReviewActionState.ManualVerify;
            row.HostReplyRequestedAt = now;
            row.HostReplySentAt = null;
            row.HostReplyError = null;
        }
        else
        {
            row.HostReviewJson = payload;
            row.HostReviewIdempotencyKey = key;
            row.HostReviewState = OtaReviewActionState.ManualVerify;
            row.HostReviewRequestedAt = now;
            row.HostReviewSentAt = null;
            row.HostReviewError = null;
        }

        // The next read of the booking comes at the next run, whatever the cadence said: it is how the attempt ends.
        row.NextPollAt = now;
        row.UpdatedAt = now;
    }

    private static void Settle(OtaReview row, ActionKind kind, OtaReviewActionState outcome, string? errorCode, DateTime now)
    {
        var code = errorCode is null ? null : Clip(errorCode, OtaReview.ErrorCodeMaxLength);
        if (kind == ActionKind.Reply)
        {
            row.HostReplyState = outcome;
            row.HostReplySentAt = outcome == OtaReviewActionState.Sent ? now : null;
            row.HostReplyError = outcome == OtaReviewActionState.Error ? code : null;
        }
        else
        {
            row.HostReviewState = outcome;
            row.HostReviewSentAt = outcome == OtaReviewActionState.Sent ? now : null;
            row.HostReviewError = outcome == OtaReviewActionState.Error ? code : null;
            if (outcome == OtaReviewActionState.Sent)
                row.HostReviewWindow = HostReviewWindow.Sent;
        }

        // A refusal as «not allowed» is read again at the next run, which shows whether it was already done; an unknown outcome too. A settled one follows its cadence.
        row.NextPollAt = outcome is OtaReviewActionState.Error or OtaReviewActionState.ManualVerify
            ? now
            : row.NextPollAt;
    }

    /// <summary>Takes the attempt back: no text, no key, no state, as if nothing was asked (the call did not happen).</summary>
    private static void Revert(OtaReview row, ActionKind kind)
    {
        if (kind == ActionKind.Reply)
        {
            row.HostReplyText = null;
            row.HostReplyIdempotencyKey = null;
            row.HostReplyState = OtaReviewActionState.None;
            row.HostReplyRequestedAt = null;
            row.HostReplyError = null;
        }
        else
        {
            row.HostReviewJson = null;
            row.HostReviewIdempotencyKey = null;
            row.HostReviewState = OtaReviewActionState.None;
            row.HostReviewRequestedAt = null;
            row.HostReviewError = null;
        }
    }

    // ─── The host's review ───

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryPattern();

    /// <summary>
    /// The host's review as it goes to the channel manager and as it is stored (the ratings, the public text, the private text). At least one rating and the public text; 422
    /// <c>ota_review_host_review_invalid</c> for anything else: no rating, a value that is not a whole number from 1 to 5, a category twice or with a name that is not one, a missing
    /// or too long text. A list is never «all»: an empty list of ratings is refused, not sent as «no rating».
    /// </summary>
    internal static (OctorateHostReviewCommand Review, string Stored) HostReviewOf(OtaHostReviewCommand? command)
    {
        var invalid = new DomainRuleException(OtaReviewErrorCodes.HostReviewInvalid, "OtaReviewHostReviewInvalid");
        if (command is null || command.Ratings is null || command.Ratings.Count is 0 or > MaxHostReviewRatings)
            throw invalid;

        var ratings = new List<OctorateReviewRating>();
        foreach (var rating in command.Ratings)
        {
            var category = rating?.Category?.Trim().ToLowerInvariant();
            if (rating is null
                || category is null
                || category.Length > MaxCategoryLength
                || !CategoryPattern().IsMatch(category)
                || rating.Value is < 1m or > 5m
                || rating.Value != decimal.Truncate(rating.Value)
                || ratings.Any(r => r.Category == category))
            {
                throw invalid;
            }

            ratings.Add(new OctorateReviewRating(category, rating.Value));
        }

        var text = ReviewRules.NormalizeText(command.Text);
        var privateText = ReviewRules.NormalizeText(command.PrivateText);
        if (text is null || text.Length > ReviewRules.TextMaxLength || privateText is { Length: > ReviewRules.TextMaxLength })
            throw invalid;

        var stored = JsonSerializer.Serialize(
            new StoredHostReview(
                [.. ratings.Select(r => new ReviewCategoryRating(r.Category, r.Value))],
                text,
                privateText),
            JsonOptions);
        return (new OctorateHostReviewCommand(ratings, text, privateText), stored);
    }

    private static StoredHostReview? ReadHostReview(OtaReview row)
    {
        if (string.IsNullOrWhiteSpace(row.HostReviewJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StoredHostReview>(row.HostReviewJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a row keeps of the host's review (encrypted in the column).</summary>
    private sealed record StoredHostReview(IReadOnlyList<ReviewCategoryRating> Ratings, string? Text, string? PrivateText);

    // ─── The hosts are told ───

    /// <summary>
    /// The hosts of the booking learn, live, what the read found that needs them (best effort, after the save: whatever goes wrong here is logged and never undoes the read): a
    /// review that appeared, a reply or a host's review that did not reach the channel.
    /// </summary>
    private async Task TellHostsAsync(OtaReview row, OtaReviewApplyResult applied, CancellationToken ct)
    {
        if (push is null || !(applied.NewlyPublished || applied.ReplyFailed || applied.HostReviewFailed))
            return;

        try
        {
            var facts = await db.Bookings
                .AsNoTracking()
                .Where(b => b.OrgId == row.OrgId && b.Id == row.BookingId)
                .Select(b => new { PropertyName = b.Property.Name, Language = b.Org.DefaultLanguage })
                .FirstOrDefaultAsync(ct);
            if (facts is null)
                return;

            var culture = string.Equals(facts.Language, "en", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("en")
                : EmailTemplates.DefaultCulture;
            if (applied.NewlyPublished)
                OtaReviewLiveRefresh.TellReceived(push, row, facts.PropertyName, culture);

            if (applied.ReplyFailed)
                OtaReviewLiveRefresh.TellFailed(push, row, row.HostReplyRequestedAt, "reply", facts.PropertyName, culture);

            if (applied.HostReviewFailed)
                OtaReviewLiveRefresh.TellFailed(push, row, row.HostReviewRequestedAt, "host-review", facts.PropertyName, culture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The hosts of review {ReviewId} (org {OrgId}) could not be told ({Error})", row.Id, row.OrgId, ex.GetType().Name);
        }
    }

    private sealed record BookingFacts(
        Guid Id,
        Guid PropertyId,
        BookingSource Source,
        BookingStatus Status,
        bool NoShow,
        DateTime CheckOutDate,
        string? OctorateReservationId,
        bool GuestErased);
}
