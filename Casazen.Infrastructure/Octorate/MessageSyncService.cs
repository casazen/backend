using System.Globalization;
using System.Runtime.ExceptionServices;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IMessageSyncService"/> (E05/F01/US01/T02, ADR-032, ADR-023, ADR-022, NF-62, NF-64): the copy of the OTA conversations of an
/// organization, read from Octorate through <see cref="IOctorateApiClient"/> and kept up to date.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant.</b> The org of a call is the argument (or the org of the property), opened with <see cref="IOctorateOrgScope.Use"/> for the
/// calls to Octorate, which take no org; every query names the org and ignores the tenant filter, because a job and a handler run with none.
/// The listings are the ones of the organization's mapping (<see cref="IChannelMappingService.ListAsync"/>): an accommodation that is not one of
/// them is never read, whatever a webhook said. A thread gets a property only from a <i>confirmed</i> mapping of its listing and loses it when
/// the mapping stops being confirmed (OCT-04); until then it is stored with no property (unassigned), counted, and never guessed.</item>
/// <item><b>Once.</b> A thread is applied under an advisory lock of (org, Octorate thread) in one transaction: the thread by
/// (org, Octorate thread id), a message by (org, Octorate message id), so the same message delivered by the webhook, the 10-minute pass and the
/// deep pass at once is one row and one event. A message already stored is not written again: only its delivery state (never back from
/// sent), its read time and its attachment address, when they were missing, are completed.</item>
/// <item><b>Provenance.</b> An inbound message is <see cref="MessageOrigin.Guest"/>; an outbound one written outside CasaZen is
/// <see cref="MessageOrigin.Person"/>. An outbound row CasaZen wrote (E05/F02/US02) is matched by its Octorate id; one left in
/// <see cref="OtaMessageDeliveryState.ManualVerify"/> without an id is matched by (thread, same text, sent within ten minutes) and completed
/// with the id and the state Octorate reports, never duplicated (OCT-10). Internal notes of the team (processor <c>NOTE</c>) are not part of the
/// conversation and are not copied.</item>
/// <item><b>Events (ADR-022-R1).</b> In the same unit of work as the data: <c>GuestMessageReceived</c> for each new inbound message sent within the
/// last 48 hours (the import of a history must not notify, nor start a routine, for conversations that ended long ago), key
/// <c>ota-msg:{message id}</c>; <c>OtaThreadSynced</c> for each synced thread with the flag of Octorate's assistant, key
/// <c>ota-thread:{thread}:{last message}:{flag}</c> (the flag is in the key so a change of it, with no new message, is a new event). A key already
/// taken is never recorded again.</item>
/// <item><b>Failures.</b> A listing whose pass fails keeps the code of the failure (<see cref="OtaMessageSyncState.LastError"/>); the exception
/// then reaches the caller, so an event is delivered again. The license without the <c>chat</c> module is the state
/// <see cref="OtaMessageSyncErrorCodes.ChatNotLicensed"/>: it does not stop the other listings, and the listing is probed again after an hour. A
/// rate limit, an organization that is not connected and the wrong environment end the run with nothing recorded against a listing.</item>
/// <item><b>Rate limit.</b> The calls leave <see cref="RateReserve"/> of the org's bucket to the people who use CasaZen; a run of a job waits for the
/// bucket at most <see cref="MaxRateWait"/> and then ends with <see cref="OctorateRateLimitException"/>, the checkpoints saved. The handler of a
/// notification runs in the outbox dispatcher, which serves every org within a budget of seconds, so it never waits (<see cref="MessageSyncPacing"/>):
/// with the bucket down to the reserve it ends at once with the same exception, and the dispatcher delivers the event again.</item>
/// <item><b>Erased guests.</b> A thread anonymized with its guest (<see cref="OtaMessageThread.AnonymizedAt"/>) is read for its structure only:
/// no name, no text and no attachment of it comes back. The erasure only finds the threads that are there and linked to the booking of the guest when
/// it runs, so the sync looks at the guest of the booking of every thread it applies: a thread copied for the first time, or linked to its booking,
/// after the guest was erased or anonymized (<c>Guest.DataAnonymizedDate</c>, or deleted) is marked at once and what it holds goes.</item>
/// <item><b>Privacy.</b> The logs carry ids, codes and counts, never a text, a name or an address.</item>
/// </list>
/// </remarks>
public sealed class MessageSyncService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IChannelMappingService mappings,
    IDomainEventRecorder recorder,
    IFeatureFlags featureFlags,
    OctorateRateLimiter rateLimiter,
    MessageSyncPacing pacing,
    TimeProvider timeProvider,
    ILogger<MessageSyncService> logger) : IMessageSyncService
{
    /// <summary>A read of the messages of a thread starts this much before the newest message that is stored (clock and ordering slack).</summary>
    public static readonly TimeSpan ThreadOverlap = TimeSpan.FromMinutes(5);

    /// <summary>An outbound row left in manual verification is matched with a message of Octorate sent within this time of it.</summary>
    public static readonly TimeSpan ManualVerifyWindow = TimeSpan.FromMinutes(10);

    /// <summary>A new inbound message older than this is stored and raises no event (the import of a history).</summary>
    public static readonly TimeSpan EventFreshness = TimeSpan.FromHours(48);

    /// <summary>The smallest overlap of a deep pass: the retry window of the webhook of Octorate (OCT-09).</summary>
    public static readonly TimeSpan MinDeepOverlap = TimeSpan.FromDays(2);

    /// <summary>The longest a run waits for the bucket of an org to refill.</summary>
    public static readonly TimeSpan MaxRateWait = TimeSpan.FromMinutes(2);

    /// <summary>A listing whose chat is not in its license is asked again after this long.</summary>
    public static readonly TimeSpan LicenseProbeInterval = TimeSpan.FromHours(1);

    /// <summary>The recent pass reads the messages of the threads it names from this far back (the 24 hours it asks for, and an hour of slack).</summary>
    public static readonly TimeSpan RecentFloor = TimeSpan.FromHours(25);

    /// <summary>Calls of the bucket of the org that the sync leaves to the people who use CasaZen.</summary>
    public const int RateReserve = 5;

    /// <summary>The pages of the thread list a deep pass reads at most, for one listing.</summary>
    internal const int MaxDeepPages = 100;

    private const string ThreadKeyPrefix = "ota:";

    public async Task<SyncResult> ImportPropertyAsync(Guid propertyId, CancellationToken ct, TimeSpan? budget = null)
    {
        if (!IsEnabled())
            return SyncResult.Empty;

        // The soft-delete filter stays: a property that was deleted is not imported.
        var orgId = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is not { } org)
        {
            logger.LogInformation("Octorate chat import of property {PropertyId}: the property is not there, nothing read", propertyId);
            return SyncResult.Empty;
        }

        var scope = await LoadOrgAsync(org, ct);
        var listing = scope.Listings.Values.FirstOrDefault(l => l.PropertyId == propertyId);
        if (listing is null)
        {
            logger.LogInformation(
                "Octorate chat import of property {PropertyId} (org {OrgId}): no confirmed mapping, nothing read", propertyId, org);
            return SyncResult.Empty;
        }

        using (orgScope.Use(org))
            return await ImportListingAsync(scope, listing, budget, ct);
    }

    public async Task<SyncResult> SyncThreadAsync(
        Guid orgId,
        string accommodationId,
        string octorateThreadId,
        DateTime? createdAfter,
        CancellationToken ct)
    {
        var accommodation = OctorateApiClient.CheckedAccommodationId(accommodationId);
        var threadId = OctorateApiClient.CheckedThreadId(octorateThreadId);
        if (!IsEnabled())
            return SyncResult.Empty;

        var org = await LoadOrgAsync(orgId, ct);
        if (!org.Listings.TryGetValue(accommodation, out var listing))
        {
            logger.LogInformation(
                "Octorate chat sync of org {OrgId}: accommodation {AccommodationId} is not a listing of the organization, nothing read",
                orgId,
                accommodation);
            return SyncResult.Empty;
        }

        using (orgScope.Use(orgId))
        {
            try
            {
                return await SyncOneThreadAsync(org, listing, threadId, createdAfter, ct);
            }
            catch (OctorateException ex) when (IsListingFailure(ex))
            {
                await RecordErrorAsync(orgId, listing, ex.Code, ct);
                throw;
            }
        }
    }

    public async Task<SyncResult> ReconcileRecentAsync(Guid orgId, CancellationToken ct)
    {
        if (!IsEnabled())
            return SyncResult.Empty;

        var org = await LoadOrgAsync(orgId, ct);

        // Local and cheap, and first: a failure of Octorate later in the pass must not leave the threads whose booking arrived unlinked.
        await LinkUnlinkedThreadsAsync(orgId, ct);

        SyncResult result;
        using (orgScope.Use(orgId))
        {
            result = await ForEachListingAsync(
                org,
                async listing =>
                {
                    await WaitForCapacityAsync(orgId, ct);
                    var recent = await client.GetRecentChatMessagesAsync(listing.AccommodationId, ct);

                    var total = SyncResult.Empty;
                    foreach (var threadId in ThreadIdsOf(recent))
                        total = total.Plus(await SyncOneThreadAsync(org, listing, threadId, Now() - RecentFloor, ct, createdAfterIsFloor: true));

                    return total;
                },
                listing => CompletePassAsync(orgId, listing, state => state.LastRecentPassAt = Now(), ct),
                ct);
        }

        LogPass("recent", orgId, result);
        return result;
    }

    public async Task<SyncResult> ReconcileDeepAsync(Guid orgId, TimeSpan overlap, CancellationToken ct)
    {
        if (!IsEnabled())
            return SyncResult.Empty;

        // The webhook is retried for two days (OCT-09): a window narrower than that would lose a notification that arrived late.
        var window = overlap < MinDeepOverlap ? MinDeepOverlap : overlap;
        var org = await LoadOrgAsync(orgId, ct);
        var started = Now();

        SyncResult result;
        using (orgScope.Use(orgId))
        {
            result = await ForEachListingAsync(
                org,
                async listing =>
                {
                    var state = await ReadStateAsync(orgId, listing.AccommodationId, ct);
                    var since = (state?.LastDeepPassAt ?? started) - window;
                    return await ReadActiveThreadsAsync(org, listing, since, ct);
                },
                listing => CompletePassAsync(orgId, listing, state => state.LastDeepPassAt = started, ct),
                ct);
        }

        LogPass("deep", orgId, result);
        return result;
    }

    // ─── The import ──────────────────────────────────────────────────────────────

    private async Task<SyncResult> ImportListingAsync(SyncOrg org, SyncListing listing, TimeSpan? budget, CancellationToken ct)
    {
        var started = timeProvider.GetTimestamp();
        var state = await ReadStateAsync(org.OrgId, listing.AccommodationId, ct);
        var offset = OffsetOf(state?.ImportCursor);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = SyncResult.Empty;
        var complete = false;

        try
        {
            while (!OutOfBudget(started, budget))
            {
                ct.ThrowIfCancellationRequested();
                await WaitForCapacityAsync(org.OrgId, ct);

                var page = await client.GetChatThreadsAsync(listing.AccommodationId, offset, ct);
                if (page.ItemCount == 0)
                {
                    complete = true;
                    break;
                }

                var fresh = page.Threads.Where(t => seen.Add(t.Id)).ToList();
                if (page.Threads.Count > 0 && fresh.Count == 0)
                {
                    // A page that repeats what was read ignores the offset: there is nothing more to get from the list.
                    logger.LogWarning(
                        "Octorate chat import of org {OrgId}, listing {AccommodationId}: the page at {Offset} repeats the threads already read, the list ends here",
                        org.OrgId,
                        listing.AccommodationId,
                        offset);
                    complete = true;
                    break;
                }

                var stopped = false;
                foreach (var thread in fresh)
                {
                    if (OutOfBudget(started, budget))
                    {
                        stopped = true;
                        break;
                    }

                    result = result.Plus(await SyncOneThreadAsync(org, listing, thread.Id, null, ct));
                }

                // The position moves only past a page that was read to its end: a run that stops inside one reads it again, which adds nothing.
                if (stopped)
                    break;

                offset += page.ItemCount;
                var cursor = offset.ToString(CultureInfo.InvariantCulture);
                await SaveStateAsync(org.OrgId, listing, s => s.ImportCursor = cursor, ct);
            }
        }
        catch (OctorateException ex) when (IsListingFailure(ex))
        {
            await RecordErrorAsync(org.OrgId, listing, ex.Code, ct);
            throw;
        }

        if (complete)
        {
            await CompletePassAsync(
                org.OrgId,
                listing,
                s =>
                {
                    s.InitialImportCompletedAt = Now();
                    s.ImportCursor = null;
                },
                ct);
        }

        logger.LogInformation(
            "Octorate chat import of org {OrgId}, listing {AccommodationId}: {Threads} threads, {Added} messages added, {Skipped} of other channels, {Unassigned} unassigned, complete {Complete}",
            org.OrgId,
            listing.AccommodationId,
            result.ThreadsSeen,
            result.MessagesAdded,
            result.SkippedChannel,
            result.Unassigned,
            complete);
        return result with { Complete = complete };
    }

    private bool OutOfBudget(long started, TimeSpan? budget) => budget is { } limit && timeProvider.GetElapsedTime(started) >= limit;

    private static int OffsetOf(string? cursor) =>
        int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0 ? offset : 0;

    // ─── The deep pass ───────────────────────────────────────────────────────────

    /// <summary>
    /// The threads of a listing with activity since <paramref name="since"/>, newest first: the list is read page after page until a page holds none
    /// (the list is ordered by recent activity, as the inbox of Octorate is). A thread whose last message is not in the answer is read: not knowing is
    /// not a reason to leave it out.
    /// </summary>
    private async Task<SyncResult> ReadActiveThreadsAsync(SyncOrg org, SyncListing listing, DateTime since, CancellationToken ct)
    {
        var result = SyncResult.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        for (var pageNumber = 0; pageNumber < MaxDeepPages; pageNumber++)
        {
            await WaitForCapacityAsync(org.OrgId, ct);
            var page = await client.GetChatThreadsAsync(listing.AccommodationId, offset, ct);
            if (page.ItemCount == 0)
                break;

            var fresh = page.Threads.Where(t => seen.Add(t.Id)).ToList();
            if (page.Threads.Count > 0 && fresh.Count == 0)
                break;

            var active = fresh.Where(t => t.LastActivityAt is null || t.LastActivityAt >= since).ToList();
            foreach (var thread in active)
                result = result.Plus(await SyncOneThreadAsync(org, listing, thread.Id, since, ct, createdAfterIsFloor: true));

            if (active.Count == 0)
                break;

            offset += page.ItemCount;
        }

        return result;
    }

    // ─── One listing after the other ─────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="pass"/> for each listing of the org, then <paramref name="complete"/>. A listing that fails does not stop the others:
    /// its code is kept on its checkpoints and the first failure is thrown once all have run. A license without the chat module is a state of
    /// the listing, not a failure: it is kept and the pass goes on (and the listing rests for <see cref="LicenseProbeInterval"/>). A rate limit,
    /// an org that is not connected and the wrong environment end the run at once.
    /// </summary>
    private async Task<SyncResult> ForEachListingAsync(
        SyncOrg org,
        Func<SyncListing, Task<SyncResult>> pass,
        Func<SyncListing, Task> complete,
        CancellationToken ct)
    {
        var states = await ReadStatesAsync(org.OrgId, ct);
        var now = Now();
        var result = SyncResult.Empty;
        ExceptionDispatchInfo? failure = null;

        foreach (var listing in org.Listings.Values.OrderBy(l => l.AccommodationId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (states.TryGetValue(listing.AccommodationId, out var state)
                && state.LastError == OtaMessageSyncErrorCodes.ChatNotLicensed
                && state.UpdatedAt > now - LicenseProbeInterval)
            {
                logger.LogDebug(
                    "Octorate chat of org {OrgId}, listing {AccommodationId}: not in the license, asked again later", org.OrgId, listing.AccommodationId);
                continue;
            }

            try
            {
                result = result.Plus(await pass(listing));
                await complete(listing);
            }
            catch (OctorateException ex) when (!IsListingFailure(ex))
            {
                throw;
            }
            catch (OctorateAuthException ex) when (ex.IsChatNotLicensed)
            {
                await RecordErrorAsync(org.OrgId, listing, ex.Code, ct);
                logger.LogInformation(
                    "Octorate chat of org {OrgId}, listing {AccommodationId}: the chat module is not in the license", org.OrgId, listing.AccommodationId);
            }
            catch (OctorateException ex)
            {
                await RecordErrorAsync(org.OrgId, listing, ex.Code, ct);
                logger.LogWarning(
                    "Octorate chat of org {OrgId}, listing {AccommodationId} failed ({Code}): the next run reads it again",
                    org.OrgId,
                    listing.AccommodationId,
                    ex.Code);
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
        return result;
    }

    /// <summary>The errors that say something about a listing. A rate limit, an org without a connection and the wrong environment say something about the org.</summary>
    private static bool IsListingFailure(OctorateException ex) =>
        ex is not (OctorateRateLimitException or OctorateWrongEnvironmentException)
        && ex is not OctorateAuthException { IsNotConnected: true };

    // ─── One thread ──────────────────────────────────────────────────────────────

    private async Task<SyncResult> SyncOneThreadAsync(
        SyncOrg org,
        SyncListing listing,
        string threadId,
        DateTime? createdAfter,
        CancellationToken ct,
        bool createdAfterIsFloor = false)
    {
        await WaitForCapacityAsync(org.OrgId, ct);

        OctorateChatThread remote;
        OtaChannel channel;
        OctorateChatMessage[] messages;
        try
        {
            remote = await client.GetChatThreadAsync(listing.AccommodationId, threadId, ct);

            // The thread must belong to the listing it is read through: never attached to another one.
            if (remote.AccommodationId is { } owner && !string.Equals(owner, listing.AccommodationId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Octorate chat thread {OctorateThreadId} of org {OrgId} belongs to accommodation {Owner}, not to {AccommodationId}: not copied",
                    remote.Id,
                    org.OrgId,
                    owner,
                    listing.AccommodationId);
                return SyncResult.Empty;
            }

            if (await ChannelOfAsync(org, listing, remote, ct) is not { } found)
                return new SyncResult(1, 0, 1, 0);

            channel = found;
            var after = await ReadStartAsync(org.OrgId, remote.Id, createdAfter, createdAfterIsFloor, ct);
            await WaitForCapacityAsync(org.OrgId, ct);
            messages = await client.GetChatMessagesAsync(listing.AccommodationId, remote.Id, after, ct);
        }
        catch (OctorateNotFoundException)
        {
            // Gone between the list (or the notification) and the read: nothing to copy.
            logger.LogInformation("Octorate chat thread {OctorateThreadId} of org {OrgId} is not there any more", threadId, org.OrgId);
            return SyncResult.Empty with { NotFound = 1 };
        }

        var added = await ApplyAsync(org.OrgId, listing, remote, channel, messages, ct);
        logger.LogDebug(
            "Octorate chat thread {OctorateThreadId} of org {OrgId}: {Added} messages added, assigned {Assigned}",
            remote.Id,
            org.OrgId,
            added,
            listing.PropertyId is not null);
        return new SyncResult(1, added, 0, listing.PropertyId is null ? 1 : 0);
    }

    /// <summary>
    /// The channel of a thread: the one of its reservation, else the one of the connection it comes through, else the sign of an Airbnb booking.
    /// <c>null</c> for a thread of a channel with no integrated chat, or whose channel nothing says.
    /// </summary>
    private async Task<OtaChannel?> ChannelOfAsync(SyncOrg org, SyncListing listing, OctorateChatThread remote, CancellationToken ct)
    {
        if (remote.ReservationChannelId is not null || remote.ReservationChannelName is not null)
            return OtaChannelMapper.Of(remote.ReservationChannelId, remote.ReservationChannelName);

        if (remote.ConnectionIds.Count > 0)
        {
            if (!org.Connections.TryGetValue(listing.AccommodationId, out var connections))
            {
                await WaitForCapacityAsync(org.OrgId, ct);
                connections = await client.GetConnectionsAsync(listing.AccommodationId, ct);
                org.Connections[listing.AccommodationId] = connections;
            }

            foreach (var connection in connections.Where(c => c.Id is not null && remote.ConnectionIds.Contains(c.Id, StringComparer.Ordinal)))
            {
                if (OtaChannelMapper.Of(connection.ChannelId, connection.ChannelName) is { } channel)
                    return channel;
            }
        }

        return remote.HasAirbnbBookingDetails ? OtaChannel.Airbnb : null;
    }

    /// <summary>
    /// Where a read of the messages of a thread starts. An instant the caller gives is used as it is; when it is a floor (the reconciliations, which
    /// look back over a window so that a message older than the newest one stored is not missed) the read starts at the floor or at the default
    /// start, whichever is earlier, and a thread that is not stored yet is read whole.
    /// </summary>
    private async Task<DateTime?> ReadStartAsync(Guid orgId, string octorateThreadId, DateTime? createdAfter, bool isFloor, CancellationToken ct)
    {
        if (createdAfter is not null && !isFloor)
            return createdAfter;

        var start = await ReadWindowStartAsync(orgId, octorateThreadId, ct);
        return isFloor && start is { } defaultStart && createdAfter is { } floor && floor < defaultStart ? floor : start;
    }

    /// <summary>
    /// The default start of a read of the messages of a thread: the newest message that is stored, minus <see cref="ThreadOverlap"/>, and not later than the
    /// oldest one still on its way (created, waiting for the channel or to be verified), so its state is read again. <c>null</c> for a thread that is
    /// not stored or has no message: everything is read.
    /// </summary>
    private async Task<DateTime?> ReadWindowStartAsync(Guid orgId, string octorateThreadId, CancellationToken ct)
    {
        var threadId = await db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(t => t.OrgId == orgId && t.OctorateThreadId == octorateThreadId)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (threadId is not { } id)
            return null;

        var messages = db.OtaMessages.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().Where(m => m.OrgId == orgId && m.ThreadId == id);
        var newest = await messages.MaxAsync(m => (DateTime?)m.SentAt, ct);
        if (newest is not { } last)
            return null;

        var oldestOnItsWay = await messages
            .Where(m => m.Direction == OtaMessageDirection.Outbound
                        && (m.DeliveryState == OtaMessageDeliveryState.Created
                            || m.DeliveryState == OtaMessageDeliveryState.WaitingAuthorization
                            || m.DeliveryState == OtaMessageDeliveryState.ManualVerify))
            .MinAsync(m => (DateTime?)m.SentAt, ct);

        return (oldestOnItsWay is { } way && way < last ? way : last) - ThreadOverlap;
    }

    // ─── The unit of work of one thread ──────────────────────────────────────────

    private async Task<int> ApplyAsync(
        Guid orgId,
        SyncListing listing,
        OctorateChatThread remote,
        OtaChannel channel,
        IReadOnlyList<OctorateChatMessage> remoteMessages,
        CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OtaMessageThread, $"{orgId:N}:{remote.Id}"));
        try
        {
            var added = await ApplyUnderLockAsync(orgId, listing, remote, channel, remoteMessages, ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return added;
        }
        finally
        {
            // Whatever happened, nothing of this thread (not even an event that was not saved) is left for the next one.
            db.ChangeTracker.Clear();
        }
    }

    private async Task<int> ApplyUnderLockAsync(
        Guid orgId,
        SyncListing listing,
        OctorateChatThread remote,
        OtaChannel channel,
        IReadOnlyList<OctorateChatMessage> remoteMessages,
        CancellationToken ct)
    {
        var now = Now();
        var thread = await db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(t => t.OrgId == orgId && t.OctorateThreadId == remote.Id, ct);
        var isNew = thread is null;
        if (thread is null)
        {
            thread = new OtaMessageThread
            {
                OrgId = orgId,
                OctorateThreadId = remote.Id,
                LastMessageAt = remote.LastActivityAt ?? now,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // The key is set before the add: it goes through the set so that it is an insert, never an update of a row that is not there.
            db.OtaMessageThreads.Add(thread);
        }

        var previousReservation = thread.OctorateReservationId;
        thread.OctorateAccommodationId = listing.AccommodationId;
        thread.PropertyId = listing.PropertyId;
        thread.ChannelSource = channel;
        thread.DefaultProcessor = Clip(remote.DefaultProcessor, OtaMessageThread.DefaultProcessorMaxLength);
        thread.AiAssistantOn = remote.AiDriven;
        thread.IsArchived = remote.ArchivedAt is not null;

        if (remote.ReservationId is { Length: > 0 and <= OtaMessageThread.OctorateReservationIdMaxLength } reservationId)
        {
            thread.OctorateReservationId = reservationId;
            thread.Kind = OtaThreadKind.Reservation;
            if (await FindBookingAsync(orgId, reservationId, ct) is { } bookingId)
                thread.BookingId = bookingId;
            else if (!string.Equals(previousReservation, reservationId, StringComparison.Ordinal))
                thread.BookingId = null;
        }
        else if (isNew)
        {
            thread.Kind = OtaThreadKind.Request;
        }

        // The guest of the booking may have been erased before this thread was copied or linked to it, when there was nothing of it to erase: the
        // thread is then kept anonymous from its first copy, as if the erasure had met it, and what it holds already goes (ADR-032-R13).
        if (thread.AnonymizedAt is null
            && thread.BookingId is { } linkedBooking
            && (await ErasedGuestBookingsAsync(orgId, [linkedBooking], ct)).Contains(linkedBooking))
        {
            await AnonymizeThreadAsync(thread, now, ct);
            logger.LogInformation("Octorate chat thread {OctorateThreadId} of org {OrgId}: the guest of its booking was erased, the thread is kept anonymous", remote.Id, orgId);
        }

        var anonymized = thread.AnonymizedAt is not null;
        if (!anonymized && Clip(remote.GuestName, OtaMessageThread.GuestDisplayNameMaxLength) is { } guestName)
            thread.GuestDisplayName = guestName;

        var (added, newInbound) = await StoreMessagesAsync(orgId, thread, anonymized, remoteMessages, now, ct);

        Touch(thread, now);
        await db.SaveChangesAsync(ct);

        // What the thread says about itself now, from all of its messages: the unread inbound ones and the newest message.
        var unread = await db.OtaMessages
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .CountAsync(m => m.OrgId == orgId && m.ThreadId == thread.Id && m.Direction == OtaMessageDirection.Inbound && m.ReadAt == null, ct);
        var newest = await db.OtaMessages
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.ThreadId == thread.Id)
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => new { m.SentAt, m.Direction })
            .FirstOrDefaultAsync(ct);
        thread.UnreadCount = unread;
        if (newest is not null)
        {
            thread.LastMessageAt = newest.SentAt;
            thread.LastMessageDirection = newest.Direction;
        }

        foreach (var message in newInbound)
        {
            var key = $"ota-msg:{message.OctorateMessageId}";
            if (!await IsEventKeyTakenAsync(orgId, key, ct))
            {
                recorder.Record(
                    DomainEventType.GuestMessageReceived,
                    message.Id,
                    new GuestMessageReceivedPayload(ThreadKeyOf(thread.Id), GuestMessageReceivedPayload.OtaSource, thread.PropertyId, thread.BookingId, channel.ToString()),
                    key,
                    orgId);
            }
        }

        // The instant to the millisecond, as Octorate gives it: what the database hands back is the same key (it keeps microseconds).
        var lastMessage = thread.LastMessageAt.ToUniversalTime().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var syncedKey = $"ota-thread:{thread.Id:N}:{lastMessage}:{AiMark(thread.AiAssistantOn)}";
        if (!await IsEventKeyTakenAsync(orgId, syncedKey, ct))
        {
            recorder.Record(
                DomainEventType.OtaThreadSynced,
                thread.Id,
                new OtaThreadSyncedPayload(ThreadKeyOf(thread.Id), thread.OctorateThreadId, thread.OctorateAccommodationId, thread.PropertyId, thread.AiAssistantOn),
                syncedKey,
                orgId);
        }

        Touch(thread, now);
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Stores the messages Octorate gave that are not stored yet and completes the ones that are (see the remarks of the class). Returns how many
    /// were added and the new inbound ones that are recent enough to raise an event.
    /// </summary>
    private async Task<(int Added, List<OtaMessage> NewInbound)> StoreMessagesAsync(
        Guid orgId,
        OtaMessageThread thread,
        bool anonymized,
        IReadOnlyList<OctorateChatMessage> remoteMessages,
        DateTime now,
        CancellationToken ct)
    {
        var incoming = remoteMessages
            .Where(m => !string.Equals(m.Processor, OctorateChatReader.NoteProcessor, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        var newInbound = new List<OtaMessage>();
        if (incoming.Count == 0)
            return (0, newInbound);

        var ids = incoming.Select(m => m.Id).ToList();
        var stored = await db.OtaMessages
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(m => m.OrgId == orgId && m.OctorateMessageId != null && ids.Contains(m.OctorateMessageId))
            .ToListAsync(ct);
        var storedById = stored.ToDictionary(m => m.OctorateMessageId!, StringComparer.Ordinal);

        // The rows CasaZen wrote and could not confirm (the answer of Octorate was lost): they have no id yet.
        var pending = new List<OtaMessage>();
        if (db.Entry(thread).State != EntityState.Added)
        {
            pending = await db.OtaMessages
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(m => m.OrgId == orgId
                            && m.ThreadId == thread.Id
                            && m.OctorateMessageId == null
                            && m.Direction == OtaMessageDirection.Outbound
                            && m.DeliveryState == OtaMessageDeliveryState.ManualVerify)
                .ToListAsync(ct);
        }

        var fresh = now - EventFreshness;
        var added = 0;
        foreach (var remote in incoming)
        {
            if (storedById.TryGetValue(remote.Id, out var row))
            {
                if (row.ThreadId == thread.Id)
                    Complete(row, remote, anonymized);

                continue;
            }

            if (remote.Direction == OtaMessageDirection.Outbound && TakeMatching(pending, remote) is { } confirmed)
            {
                confirmed.OctorateMessageId = remote.Id;
                confirmed.DeliveryState = remote.DeliveryState;
                continue;
            }

            var inbound = remote.Direction == OtaMessageDirection.Inbound;
            var message = new OtaMessage
            {
                OrgId = orgId,
                ThreadId = thread.Id,
                OctorateMessageId = remote.Id,
                Direction = remote.Direction,
                SenderName = anonymized
                    ? GuestDataEraser.AnonymizedName
                    : Clip(remote.SenderName, OtaMessage.SenderNameMaxLength) ?? (inbound ? thread.GuestDisplayName : null) ?? string.Empty,
                Text = anonymized ? string.Empty : remote.Text,
                AttachmentUrl = anonymized ? null : remote.AttachmentUrl,
                AttachmentContentType = anonymized || remote.AttachmentUrl is null ? null : remote.AttachmentContentType,
                SentAt = remote.SentAt,
                ReadAt = inbound ? remote.ReadAt : null,
                DeliveryState = remote.DeliveryState,
                Origin = inbound ? MessageOrigin.Guest : MessageOrigin.Person,
                CreatedAt = now,
            };

            db.OtaMessages.Add(message);
            added++;
            if (inbound && remote.SentAt >= fresh)
                newInbound.Add(message);
        }

        return (added, newInbound);
    }

    /// <summary>What a message already stored gets from a later read: the state it reached (never back from sent), the time it was read and an attachment it did not have.</summary>
    private static void Complete(OtaMessage row, OctorateChatMessage remote, bool anonymized)
    {
        if (row.Direction == OtaMessageDirection.Outbound
            && remote.Direction == OtaMessageDirection.Outbound
            && row.DeliveryState != OtaMessageDeliveryState.Sent
            && row.DeliveryState != remote.DeliveryState)
        {
            row.DeliveryState = remote.DeliveryState;
        }

        if (row.Direction == OtaMessageDirection.Inbound && row.ReadAt is null && remote.ReadAt is { } readAt)
            row.ReadAt = readAt;

        if (!anonymized && row.AttachmentUrl is null && remote.AttachmentUrl is not null)
        {
            row.AttachmentUrl = remote.AttachmentUrl;
            row.AttachmentContentType = remote.AttachmentContentType;
        }
    }

    /// <summary>The row left in manual verification that has the same text as <paramref name="remote"/> and was sent within <see cref="ManualVerifyWindow"/> of it.</summary>
    private static OtaMessage? TakeMatching(List<OtaMessage> pending, OctorateChatMessage remote)
    {
        var text = NormalizedText(remote.Text);
        var match = pending.FirstOrDefault(row =>
            string.Equals(NormalizedText(row.Text), text, StringComparison.Ordinal) && (row.SentAt - remote.SentAt).Duration() <= ManualVerifyWindow);
        if (match is not null)
            pending.Remove(match);

        return match;
    }

    private static string NormalizedText(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private async Task<Guid?> FindBookingAsync(Guid orgId, string reservationId, CancellationToken ct) =>
        await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.OctorateReservationId == reservationId)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The threads whose reservation CasaZen did not hold yet are linked to the booking now that it does (a Request that became a booking).</summary>
    private async Task LinkUnlinkedThreadsAsync(Guid orgId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var unlinked = await db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(t => t.OrgId == orgId && t.BookingId == null && t.OctorateReservationId != null)
            .ToListAsync(ct);
        if (unlinked.Count == 0)
            return;

        var reservationIds = unlinked.Select(t => t.OctorateReservationId!).Distinct(StringComparer.Ordinal).ToList();
        var bookings = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.OctorateReservationId != null && reservationIds.Contains(b.OctorateReservationId))
            .Select(b => new { b.Id, b.OctorateReservationId })
            .ToListAsync(ct);
        var bookingByReservation = bookings
            .GroupBy(b => b.OctorateReservationId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        var now = Now();
        var erased = await ErasedGuestBookingsAsync(orgId, [.. bookingByReservation.Values], ct);
        int linked = 0, anonymized = 0;
        foreach (var thread in unlinked)
        {
            if (!bookingByReservation.TryGetValue(thread.OctorateReservationId!, out var bookingId))
                continue;

            thread.BookingId = bookingId;
            thread.Kind = OtaThreadKind.Reservation;
            thread.UpdatedAt = now;
            linked++;

            // The guest of that booking may have been erased before the thread was linked to it, when the erasure could not find the thread: what
            // the thread holds goes now, as the erasure would have done it (ADR-032-R13).
            if (thread.AnonymizedAt is null && erased.Contains(bookingId))
            {
                await AnonymizeThreadAsync(thread, now, ct);
                anonymized++;
            }
        }

        if (linked > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Octorate chat of org {OrgId}: {Threads} threads linked to the booking of their reservation, {Anonymized} of them kept anonymous",
                orgId,
                linked,
                anonymized);
        }

        db.ChangeTracker.Clear();
    }

    /// <summary>The bookings among <paramref name="bookingIds"/> whose guest was erased or anonymized: the guest has no conversation left to copy (ADR-032-R13).</summary>
    private async Task<HashSet<Guid>> ErasedGuestBookingsAsync(Guid orgId, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return [];

        var erased = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && bookingIds.Contains(b.Id) && (b.Guest.DataAnonymizedDate != null || b.Guest.IsDeleted))
            .Select(b => b.Id)
            .ToListAsync(ct);
        return [.. erased];
    }

    /// <summary>
    /// Anonymizes a thread of an erased guest the way the erasure does (<see cref="GuestDataEraser.AnonymizeOtaThread"/>): the thread is marked and what
    /// the messages already stored hold of a person goes. Stages the changes; the unit of work of the caller saves them.
    /// </summary>
    private async Task AnonymizeThreadAsync(OtaMessageThread thread, DateTime now, CancellationToken ct)
    {
        List<OtaMessage> stored = [];
        if (db.Entry(thread).State != EntityState.Added)
        {
            stored = await db.OtaMessages
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(m => m.OrgId == thread.OrgId && m.ThreadId == thread.Id)
                .ToListAsync(ct);
        }

        GuestDataEraser.AnonymizeOtaThread(thread, stored, now);
    }

    /// <summary>Sets <c>UpdatedAt</c> when the thread is new or changed: a read that finds nothing new leaves the row as it was.</summary>
    private void Touch(OtaMessageThread thread, DateTime now)
    {
        db.ChangeTracker.DetectChanges();
        if (db.Entry(thread).State is EntityState.Added or EntityState.Modified)
            thread.UpdatedAt = now;
    }

    /// <summary>The key is in the outbox of the org already, or in the events of this unit of work.</summary>
    private async Task<bool> IsEventKeyTakenAsync(Guid orgId, string key, CancellationToken ct)
    {
        if (db.ChangeTracker.Entries<OutboxEvent>().Any(e => e.State == EntityState.Added && e.Entity.OrgId == orgId && e.Entity.DedupKey == key))
            return true;

        return await db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(e => e.OrgId == orgId && e.DedupKey == key, ct);
    }

    private static string ThreadKeyOf(Guid threadId) => ThreadKeyPrefix + threadId.ToString("D");

    private static string AiMark(bool? aiAssistantOn) => aiAssistantOn switch
    {
        true => "on",
        false => "off",
        null => "unknown",
    };

    private static IEnumerable<string> ThreadIdsOf(IEnumerable<OctorateChatMessage> messages) =>
        messages
            .Select(m => m.ThreadId)
            .OfType<string>()
            .Where(id => id.Length is > 0 and <= OtaMessageThread.OctorateIdMaxLength && id.All(char.IsAsciiDigit))
            .Distinct(StringComparer.Ordinal);

    private static string? Clip(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    // ─── The listings of an org and their checkpoints ────────────────────────────

    /// <summary>
    /// The listings of the org that can be read: the rows of its mapping that are not rejected, each with the connection it was found through, and
    /// the property only when the mapping is confirmed and the property is one of the org's.
    /// </summary>
    private async Task<SyncOrg> LoadOrgAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await mappings.ListAsync(orgId, ct);
        var connections = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId)
            .Select(m => new { m.OctorateAccommodationId, m.ConnectionId })
            .ToListAsync(ct);
        var connectionOf = connections
            .GroupBy(c => c.OctorateAccommodationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ConnectionId, StringComparer.Ordinal);

        var listings = new Dictionary<string, SyncListing>(StringComparer.Ordinal);

        // A confirmed row first: when a listing has two rows, the confirmed one decides.
        foreach (var row in rows.Where(r => r.Status != OctorateMappingStatus.Rejected).OrderBy(r => r.Status == OctorateMappingStatus.Confirmed ? 0 : 1))
        {
            if (listings.ContainsKey(row.OctorateAccommodationId) || !connectionOf.TryGetValue(row.OctorateAccommodationId, out var connectionId))
                continue;

            var propertyId = row.Status == OctorateMappingStatus.Confirmed ? row.Property?.Id : null;
            listings[row.OctorateAccommodationId] = new SyncListing(row.OctorateAccommodationId, connectionId, propertyId);
        }

        return new SyncOrg(orgId, listings);
    }

    private async Task<OtaMessageSyncState?> ReadStateAsync(Guid orgId, string accommodationId, CancellationToken ct) =>
        await db.OtaMessageSyncStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.OctorateAccommodationId == accommodationId, ct);

    private async Task<Dictionary<string, OtaMessageSyncState>> ReadStatesAsync(Guid orgId, CancellationToken ct) =>
        await db.OtaMessageSyncStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => s.OrgId == orgId)
            .ToDictionaryAsync(s => s.OctorateAccommodationId, StringComparer.Ordinal, ct);

    /// <summary>Changes the checkpoints of a listing and saves them, in a unit of work of their own (the context is cleared before and after).</summary>
    private async Task SaveStateAsync(Guid orgId, SyncListing listing, Action<OtaMessageSyncState> change, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var state = await db.OtaMessageSyncStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.OctorateAccommodationId == listing.AccommodationId, ct);
        if (state is null)
        {
            state = new OtaMessageSyncState
            {
                OrgId = orgId,
                OctorateAccommodationId = listing.AccommodationId,
                ConnectionId = listing.ConnectionId,
            };
            db.OtaMessageSyncStates.Add(state);
        }

        state.ConnectionId = listing.ConnectionId;
        state.PropertyId = listing.PropertyId;
        change(state);
        state.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private Task RecordErrorAsync(Guid orgId, SyncListing listing, string code, CancellationToken ct) =>
        SaveStateAsync(orgId, listing, state => state.LastError = Clip(code, OtaMessageSyncState.LastErrorMaxLength), ct);

    /// <summary>A pass over a listing worked: its mark, no error, and the newest message of the listing as the high-water mark.</summary>
    private async Task CompletePassAsync(Guid orgId, SyncListing listing, Action<OtaMessageSyncState> mark, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var newest = await db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(t => t.OrgId == orgId && t.OctorateAccommodationId == listing.AccommodationId)
            .MaxAsync(t => (DateTime?)t.LastMessageAt, ct);

        await SaveStateAsync(
            orgId,
            listing,
            state =>
            {
                mark(state);
                state.LastError = null;
                if (newest is { } at && (state.LastCreatedAfter is null || at > state.LastCreatedAfter))
                    state.LastCreatedAfter = at;
            },
            ct);
    }

    // ─── The clock, the flag, the bucket ─────────────────────────────────────────

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The sync writes only while the connector is on (the flag of the module): off, nothing is read from Octorate and nothing is stored.</summary>
    private bool IsEnabled()
    {
        if (featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return true;

        logger.LogInformation("Octorate chat sync: the connector is off, nothing done");
        return false;
    }

    /// <summary>
    /// Waits, at most <see cref="MaxRateWait"/>, until the bucket of the org has more than <see cref="RateReserve"/> calls; the run ends with
    /// <see cref="OctorateRateLimitException"/> when it did not refill in time (the checkpoints are saved, the next run continues). The handler of a
    /// notification does not wait (<see cref="MessageSyncPacing"/>): with the bucket down to the reserve it ends at once.
    /// </summary>
    private async Task WaitForCapacityAsync(Guid orgId, CancellationToken ct)
    {
        if (!pacing.WaitForCapacity)
        {
            // The handler of a notification runs in the outbox dispatcher, one event after the other for every org within a budget of seconds: it does
            // not wait for the bucket, it ends at once and the dispatcher delivers the event again at its next run (ADR-022-R4).
            if (rateLimiter.AvailableCalls(orgId) <= RateReserve)
                throw new OctorateRateLimitException(null);

            return;
        }

        var waited = TimeSpan.Zero;
        while (rateLimiter.AvailableCalls(orgId) <= RateReserve)
        {
            if (waited >= MaxRateWait)
                throw new OctorateRateLimitException(null);

            await Task.Delay(OctorateRateLimiter.BurstPeriod, timeProvider, ct);
            waited += OctorateRateLimiter.BurstPeriod;
        }
    }

    private void LogPass(string kind, Guid orgId, SyncResult result) =>
        logger.LogInformation(
            "Octorate chat {Pass} pass of org {OrgId}: {Threads} threads, {Added} messages added, {Skipped} of other channels, {Unassigned} unassigned",
            kind,
            orgId,
            result.ThreadsSeen,
            result.MessagesAdded,
            result.SkippedChannel,
            result.Unassigned);

    /// <summary>A listing of the org: its accommodation, the connection it was found through and its property while the mapping is confirmed.</summary>
    private sealed record SyncListing(string AccommodationId, Guid ConnectionId, Guid? PropertyId);

    /// <summary>The listings of an org read once for a run, and the connections of an accommodation read once for it.</summary>
    private sealed class SyncOrg(Guid orgId, IReadOnlyDictionary<string, SyncListing> listings)
    {
        public Guid OrgId { get; } = orgId;

        public IReadOnlyDictionary<string, SyncListing> Listings { get; } = listings;

        public Dictionary<string, OctorateChannelConnection[]> Connections { get; } = new(StringComparer.Ordinal);
    }
}
