using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IMessageHubService"/>
/// <remarks>
/// <para><b>The list is one statement.</b> The direct threads (the same rows <see cref="IGuestMessageService.ListForHostAsync"/> lists, with the same
/// rules of openness and of «da rispondere») and the threads of the channels are projected to the same shape of plain columns, concatenated
/// (<c>UNION ALL</c>), filtered, ordered by the last message and paged in the database. Nothing encrypted is in that statement: the preview of the
/// last message is read for the rows of the page only, after it, so the text of an OTA message (encrypted at rest) is decrypted by the entity and
/// never searched. The scope of the caller is applied to each source before the projection (<c>InScope</c>): a restricted scope reaches the
/// threads of its properties and never one without a property.</para>
/// <para><b>Every method names the org of the scope</b> in its queries, on top of the tenant filter of the context, and answers 404 for what is
/// not there or is not within reach: the caller cannot tell the two apart.</para>
/// <para><b>Personal data.</b> The words, the names and the address of an attachment are never logged (only ids and counters), and the address never
/// leaves <see cref="OpenAttachmentAsync"/>: the thread detail asks the database only whether a message has a file, not where it is.</para>
/// </remarks>
public sealed class MessageHubService(
    AppDbContext db,
    IGuestMessageService guestMessages,
    IChannelMappingService channelMapping,
    IOtaAttachmentFetcher attachments,
    ILogger<MessageHubService> logger,
    TimeProvider? timeProvider = null) : IMessageHubService
{
    /// <summary>Stable code of the 404 of a conversation that is not there or not within reach.</summary>
    public const string ThreadNotFoundCode = "thread_not_found";

    /// <summary>Stable code of the 404 of a message that is not there or not within reach (the generic one).</summary>
    public const string MessageNotFoundCode = "message_not_found";

    /// <summary>Stable code of the 404 of a file that is gone, expired, too big, of another type or unreachable.</summary>
    public const string AttachmentUnavailableCode = "attachment_unavailable";

    private const int DirectSource = (int)MessageThreadSource.Direct;
    private const int OtaSource = (int)MessageThreadSource.Ota;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<MessageHubPage> ListAsync(
        HostScope scope,
        MessageHubFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, GuestMessageRules.MaxPageSize);
        var now = Now();

        // A direct thread is open while its check-out day is not more than seven days behind today (Europe/Rome): the same rule as StateOf.
        var openFrom = RomeCalendar.TodayAt(new DateTimeOffset(now)).AddDays(-GuestMessageRules.ClosedAfterCheckOutDays);
        var rows = Filtered(Rows(scope, openFrom), filter);

        var total = await rows.CountAsync(cancellationToken);
        if (total == 0)
            return new MessageHubPage([], 0, page, pageSize);

        var pageRows = await rows
            .OrderByDescending(r => r.LastMessageAt)
            .ThenBy(r => r.KeyId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var lastMessages = await LastMessagesAsync(scope.OrgId, pageRows, cancellationToken);
        var items = pageRows.Select(r => Summary(r, lastMessages.GetValueOrDefault(new MessageThreadKey((MessageThreadSource)r.Source, r.KeyId)), now)).ToList();
        return new MessageHubPage(items, total, page, pageSize);
    }

    public async Task<MessageThreadView> GetThreadAsync(
        HostScope scope,
        MessageThreadKey key,
        bool includeAmounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return key.Source == MessageThreadSource.Ota
            ? await GetOtaThreadAsync(scope, key, includeAmounts, cancellationToken)
            : await GetDirectThreadAsync(scope, key, includeAmounts, cancellationToken);
    }

    public async Task<MessageHubReadResult> MarkReadAsync(
        HostScope scope,
        MessageThreadKey key,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (key.Source == MessageThreadSource.Direct)
        {
            // The booking is within reach, or it is a 404 (the same check as the detail): only then is the existing service asked.
            await DirectBookingAsync(scope, key.Id, cancellationToken);
            var view = await guestMessages.MarkReadAsync(key.Id, userId, cancellationToken);
            return new MessageHubReadResult(key, view.UnreadCount);
        }

        var thread = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId && t.Id == key.Id)
            .InScope(scope)
            .Select(t => new { t.Id, t.OctorateThreadId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ThreadNotFound();

        // The same lock as the sync of the thread: a read of the host and a pass that stores a new message do not undo each other's count.
        var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.OtaMessageThread, $"{scope.OrgId:N}:{thread.OctorateThreadId}"));
        await using (transaction)
        {
            var now = Now();
            var unread = await db.OtaMessages
                .Where(m => m.OrgId == scope.OrgId && m.ThreadId == thread.Id && m.Direction == OtaMessageDirection.Inbound && m.ReadAt == null)
                .ToListAsync(cancellationToken);
            foreach (var message in unread)
                message.ReadAt = now;

            var row = await db.OtaMessageThreads.FirstAsync(t => t.OrgId == scope.OrgId && t.Id == thread.Id, cancellationToken);
            row.UnreadCount = 0;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("OTA thread {ThreadId} marked as read: {Count} messages", thread.Id, unread.Count);
        }

        return new MessageHubReadResult(key, 0);
    }

    public async Task<IReadOnlyList<MessageHubUnreadThread>> ListUnreadAsync(
        HostScope scope,
        bool includeOta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var items = (await guestMessages.ListUnreadForHostAsync(scope, cancellationToken))
            .Select(t => new MessageHubUnreadThread(MessageThreadKey.ForDirect(t.BookingId), t.BookingId, t.UnreadCount))
            .ToList();
        if (!includeOta)
            return items;

        var ota = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId && t.UnreadCount > 0 && !t.IsArchived)
            .InScope(scope)
            .OrderByDescending(t => t.LastMessageAt)
            .ThenBy(t => t.Id)
            .Take(GuestMessageRules.UnreadThreadsMax)
            .Select(t => new { t.Id, t.BookingId, t.UnreadCount })
            .ToListAsync(cancellationToken);
        items.AddRange(ota.Select(t => new MessageHubUnreadThread(MessageThreadKey.ForOta(t.Id), t.BookingId, t.UnreadCount)));
        return items;
    }

    public async Task<OtaAttachmentContent> OpenAttachmentAsync(HostScope scope, Guid messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var message = await db.OtaMessages
            .AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.Id == messageId)
            .Select(m => new { m.ThreadId, m.AttachmentUrl })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw MessageNotFound();

        // The thread of the message is within reach of the caller, or the message does not exist for it.
        var reachable = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId && t.Id == message.ThreadId)
            .InScope(scope)
            .AnyAsync(cancellationToken);
        if (!reachable)
            throw MessageNotFound();

        if (string.IsNullOrEmpty(message.AttachmentUrl))
            throw AttachmentUnavailable();

        var content = await attachments.FetchAsync(message.AttachmentUrl, cancellationToken);
        if (content is null)
        {
            logger.LogInformation("The attachment of message {MessageId} is no longer available", messageId);
            throw AttachmentUnavailable();
        }

        return content;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The list
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The two sources in the same shape. Both sides name the same members in the same order, which is what lets the provider concatenate them
    /// in one statement; the only strings in it are names and codes, never the words of a message.
    /// </summary>
    private IQueryable<HubRow> Rows(HostScope scope, DateTime openFrom)
    {
        var orgId = scope.OrgId;

        var direct = db.GuestThreads
            .AsNoTracking()
            .Where(t => t.OrgId == orgId
                && t.Booking.Source == BookingSource.Direct
                && (t.Booking.Status == BookingStatus.Confirmed
                    || t.Booking.Status == BookingStatus.CheckedIn
                    || t.Booking.Status == BookingStatus.CheckedOut))
            .InScope(scope)
            .Select(t => new HubRow
            {
                Source = DirectSource,
                KeyId = t.BookingId,
                Channel = (int)MessageHubChannel.Direct,
                Kind = (int)MessageHubThreadKind.Reservation,
                PropertyId = t.PropertyId,
                PropertyName = t.Property.Name,
                BookingId = t.BookingId,
                BookingCode = t.Booking.BookingCode,
                GuestName = t.Booking.Guest.FirstName + " " + t.Booking.Guest.LastName,
                CheckInDate = t.Booking.CheckInDate,
                CheckOutDate = t.Booking.CheckOutDate,
                BookingStatus = (int)t.Booking.Status,
                IsArchived = false,
                LastMessageAt = t.LastMessageAt,
                UnreadCount = db.GuestMessages.Count(m => m.ThreadId == t.Id
                    && m.Sender == GuestMessageSender.Guest
                    && (t.HostReadAt == null || m.CreatedAt > t.HostReadAt)),
                NeedsReply = t.LastGuestMessageAt != null
                    && (t.LastHostMessageAt == null || t.LastHostMessageAt < t.LastGuestMessageAt)
                    && t.Booking.CheckOutDate >= openFrom,
            });

        var ota = db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == orgId)
            .InScope(scope)
            .Select(t => new HubRow
            {
                Source = OtaSource,
                KeyId = t.Id,
                Channel = (int)t.ChannelSource,
                Kind = (int)t.Kind,
                PropertyId = t.PropertyId,
                PropertyName = t.Property!.Name,
                BookingId = t.BookingId,
                BookingCode = t.Booking!.BookingCode,
                GuestName = t.GuestDisplayName ?? (t.Booking!.Guest.FirstName + " " + t.Booking!.Guest.LastName),
                CheckInDate = (DateTime?)t.Booking!.CheckInDate,
                CheckOutDate = (DateTime?)t.Booking!.CheckOutDate,
                BookingStatus = (int?)t.Booking!.Status,
                IsArchived = t.IsArchived,
                LastMessageAt = t.LastMessageAt,
                UnreadCount = t.UnreadCount,
                NeedsReply = !t.IsArchived && t.LastMessageDirection == OtaMessageDirection.Inbound,
            });

        return direct.Concat(ota);
    }

    private static IQueryable<HubRow> Filtered(IQueryable<HubRow> rows, MessageHubFilter filter)
    {
        if (filter.NeedsReply is { } needsReply)
            rows = rows.Where(r => r.NeedsReply == needsReply);

        if (filter.PropertyId is { } propertyId)
            rows = rows.Where(r => r.PropertyId == propertyId);

        if (filter.Channel is { } channel)
        {
            var code = (int)channel;
            rows = rows.Where(r => r.Channel == code);
        }

        if (filter.BookingId is { } bookingId)
            rows = rows.Where(r => r.BookingId == bookingId);

        if (filter.Unread is { } unread)
            rows = unread ? rows.Where(r => r.UnreadCount > 0) : rows.Where(r => r.UnreadCount == 0);

        if (filter.Kind is { } kind)
        {
            var code = (int)kind;
            rows = rows.Where(r => r.Kind == code);
        }

        var text = filter.Query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (text.Length > MessageHubFilter.QueryMaxLength)
                text = text[..MessageHubFilter.QueryMaxLength];

            var lower = text.ToLowerInvariant();
            var code = BookingSearchRules.CodeFragment(text);
            if (code is null)
            {
                rows = rows.Where(r => (r.GuestName ?? string.Empty).ToLower().Contains(lower)
                    || (r.PropertyName ?? string.Empty).ToLower().Contains(lower));
            }
            else
            {
                string fragment = code;
                rows = rows.Where(r => (r.GuestName ?? string.Empty).ToLower().Contains(lower)
                    || (r.PropertyName ?? string.Empty).ToLower().Contains(lower)
                    || (r.BookingCode ?? string.Empty).Contains(fragment));
            }
        }

        return rows;
    }

    /// <summary>
    /// The last message of each conversation of the page, for the preview and the author: the id of the newest message first (a correlated
    /// subquery of plain columns), then the messages themselves, so the encrypted text is read through the entity.
    /// </summary>
    private async Task<Dictionary<MessageThreadKey, LastMessage>> LastMessagesAsync(
        Guid orgId,
        IReadOnlyList<HubRow> pageRows,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<MessageThreadKey, LastMessage>();

        var bookingIds = pageRows.Where(r => r.Source == DirectSource).Select(r => r.KeyId).ToList();
        if (bookingIds.Count > 0)
        {
            var lastIds = await db.GuestThreads
                .AsNoTracking()
                .Where(t => t.OrgId == orgId && bookingIds.Contains(t.BookingId))
                .Select(t => db.GuestMessages
                    .Where(m => m.ThreadId == t.Id)
                    .OrderByDescending(m => m.CreatedAt)
                    .ThenByDescending(m => m.Id)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefault())
                .ToListAsync(cancellationToken);
            var ids = lastIds.OfType<Guid>().ToList();
            var messages = await db.GuestMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && ids.Contains(m.Id))
                .Select(m => new { m.Thread.BookingId, m.Sender, m.Text })
                .ToListAsync(cancellationToken);
            foreach (var m in messages)
            {
                result[MessageThreadKey.ForDirect(m.BookingId)] = new LastMessage(
                    m.Sender == GuestMessageSender.Guest ? MessageOrigin.Guest : MessageOrigin.Person,
                    GuestMessageRules.Preview(m.Text));
            }
        }

        var threadIds = pageRows.Where(r => r.Source == OtaSource).Select(r => r.KeyId).ToList();
        if (threadIds.Count > 0)
        {
            var lastIds = await db.OtaMessageThreads
                .AsNoTracking()
                .Where(t => t.OrgId == orgId && threadIds.Contains(t.Id))
                .Select(t => db.OtaMessages
                    .Where(m => m.ThreadId == t.Id)
                    .OrderByDescending(m => m.SentAt)
                    .ThenByDescending(m => m.CreatedAt)
                    .ThenByDescending(m => m.Id)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefault())
                .ToListAsync(cancellationToken);
            var ids = lastIds.OfType<Guid>().ToList();
            var messages = await db.OtaMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && ids.Contains(m.Id))
                .Select(m => new { m.ThreadId, m.Origin, m.Text })
                .ToListAsync(cancellationToken);
            foreach (var m in messages)
            {
                result[MessageThreadKey.ForOta(m.ThreadId)] = new LastMessage(
                    m.Origin,
                    string.IsNullOrWhiteSpace(m.Text) ? null : GuestMessageRules.Preview(m.Text));
            }
        }

        return result;
    }

    private static MessageHubThreadSummary Summary(HubRow r, LastMessage? last, DateTime now)
    {
        var source = (MessageThreadSource)r.Source;
        var key = new MessageThreadKey(source, r.KeyId);
        var guestName = r.GuestName?.Trim();

        return new MessageHubThreadSummary(
            key,
            source,
            (MessageHubChannel)r.Channel,
            (MessageHubThreadKind)r.Kind,
            StateOf(r, now),
            r.PropertyId,
            r.PropertyName,
            r.PropertyId is null,
            r.BookingId,
            r.BookingCode is null ? null : BookingCodes.Format(r.BookingCode),
            string.IsNullOrEmpty(guestName) ? null : guestName,
            r.CheckInDate,
            r.CheckOutDate,
            r.LastMessageAt,
            last?.Origin,
            last?.Preview,
            r.UnreadCount,
            r.NeedsReply);
    }

    /// <summary>A conversation of a channel is archived or not; a direct one is open until the seventh day after the check-out (the rule of <see cref="GuestMessageRules.StateOf"/>).</summary>
    private static MessageHubThreadState StateOf(HubRow row, DateTime now)
    {
        if ((MessageThreadSource)row.Source == MessageThreadSource.Ota)
            return row.IsArchived ? MessageHubThreadState.Archived : MessageHubThreadState.Open;

        if (row.BookingStatus is not { } status || row.CheckOutDate is not { } checkOut)
            return MessageHubThreadState.Closed;

        return GuestMessageRules.StateOf(BookingSource.Direct, (BookingStatus)status, checkOut, now) == GuestThreadState.Open
            ? MessageHubThreadState.Open
            : MessageHubThreadState.Closed;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // A conversation of a channel
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<MessageThreadView> GetOtaThreadAsync(HostScope scope, MessageThreadKey key, bool includeAmounts, CancellationToken cancellationToken)
    {
        var thread = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId && t.Id == key.Id)
            .InScope(scope)
            .Select(t => new
            {
                t.Id,
                t.PropertyId,
                PropertyName = t.Property != null ? t.Property.Name : null,
                t.BookingId,
                t.ChannelSource,
                t.Kind,
                t.GuestDisplayName,
                t.LastMessageDirection,
                t.UnreadCount,
                t.IsArchived,
                t.AnonymizedAt,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ThreadNotFound();

        // The latest messages, one more than the cap to know whether there are earlier ones. Only whether a message has a file is asked of the
        // database, never where it is: the address does not leave the table.
        var latest = await db.OtaMessages
            .AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.ThreadId == thread.Id)
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(GuestMessageRules.TranscriptMaxMessages + 1)
            .Select(m => new
            {
                m.Id,
                m.Direction,
                m.Origin,
                m.RuleCode,
                m.SenderName,
                m.Text,
                m.SentAt,
                m.ReadAt,
                m.DeliveryState,
                HasAttachment = m.AttachmentUrl != null,
                m.AttachmentContentType,
            })
            .ToListAsync(cancellationToken);
        var hasEarlier = latest.Count > GuestMessageRules.TranscriptMaxMessages;
        var messages = latest
            .Take(GuestMessageRules.TranscriptMaxMessages)
            .Reverse()
            .Select(m => new MessageHubMessage(
                m.Id,
                m.Direction,
                m.Origin,
                m.RuleCode,
                string.IsNullOrWhiteSpace(m.SenderName) ? null : m.SenderName,
                m.Text,
                m.SentAt,
                m.Direction == OtaMessageDirection.Inbound ? m.ReadAt : null,
                m.Direction == OtaMessageDirection.Outbound ? m.DeliveryState : null,
                m.HasAttachment ? m.AttachmentContentType : null))
            .ToList();

        var card = await BookingCardAsync(scope, thread.BookingId, thread.PropertyId, thread.GuestDisplayName, includeAmounts, cancellationToken);
        var actions = await OtaActionsAsync(thread.ChannelSource, thread.Kind, thread.PropertyId, cancellationToken);

        return new MessageThreadView(
            key,
            MessageThreadSource.Ota,
            (MessageHubChannel)(int)thread.ChannelSource,
            (MessageHubThreadKind)(int)thread.Kind,
            thread.IsArchived ? MessageHubThreadState.Archived : MessageHubThreadState.Open,
            thread.PropertyId,
            thread.PropertyName,
            thread.PropertyId is null,
            thread.GuestDisplayName ?? card?.GuestName,
            thread.UnreadCount,
            !thread.IsArchived && thread.LastMessageDirection == OtaMessageDirection.Inbound,
            thread.AnonymizedAt is not null,
            card?.Card,
            card?.GuestId,
            actions,
            messages,
            hasEarlier);
    }

    /// <summary>
    /// What the host may do in a conversation of a channel (OCT-04): nothing while it has no property, or while its property is in a row of the
    /// mapping that nobody decided; otherwise what <see cref="OtaChannelCapabilities"/> says for its channel and kind.
    /// </summary>
    private async Task<MessageThreadActions> OtaActionsAsync(OtaChannel channel, OtaThreadKind kind, Guid? propertyId, CancellationToken cancellationToken)
    {
        if (propertyId is not { } id || await channelMapping.IsWriteBlockedAsync(id, cancellationToken))
            return MessageThreadActions.Blocked(MessageThreadActions.MappingNotConfirmedReason);

        var capability = OtaChannelCapabilities.For(channel, kind);
        return new MessageThreadActions(
            capability.CanReply,
            capability.ContactDetailsAllowed,
            capability.AttachmentsConfirmed,
            capability.AutoReplyConfirmed,
            null);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The conversation with the guest of a direct booking
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<MessageThreadView> GetDirectThreadAsync(HostScope scope, MessageThreadKey key, bool includeAmounts, CancellationToken cancellationToken)
    {
        var booking = await DirectBookingAsync(scope, key.Id, cancellationToken);
        var view = await guestMessages.GetForHostAsync(key.Id, cancellationToken);
        if (view.State == GuestThreadState.Unavailable)
            throw ThreadNotFound();

        var card = await BookingCardAsync(scope, key.Id, booking.PropertyId, null, includeAmounts, cancellationToken);
        var open = view.State == GuestThreadState.Open;
        var actions = open
            ? new MessageThreadActions(true, true, false, true, null)
            : MessageThreadActions.Blocked(MessageThreadActions.ThreadClosedReason);
        var messages = view.Messages
            .Select(m => new MessageHubMessage(
                m.Id,
                m.Sender == GuestMessageSender.Guest ? OtaMessageDirection.Inbound : OtaMessageDirection.Outbound,
                m.Sender == GuestMessageSender.Guest ? MessageOrigin.Guest : MessageOrigin.Person,
                null,
                null,
                m.Text,
                m.CreatedAt,
                null,
                null,
                null))
            .ToList();

        return new MessageThreadView(
            key,
            MessageThreadSource.Direct,
            MessageHubChannel.Direct,
            MessageHubThreadKind.Reservation,
            open ? MessageHubThreadState.Open : MessageHubThreadState.Closed,
            booking.PropertyId,
            booking.PropertyName,
            false,
            card?.GuestName,
            view.UnreadCount,
            view.NeedsReply,
            false,
            card?.Card,
            card?.GuestId,
            actions,
            messages,
            false);
    }

    /// <summary>The direct booking of a key within the reach of the scope, or the 404 of a conversation that is not there.</summary>
    private async Task<DirectBooking> DirectBookingAsync(HostScope scope, Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId)
            .InScope(scope)
            .Select(b => new DirectBooking(b.PropertyId, b.Property.Name))
            .FirstOrDefaultAsync(cancellationToken);
        return booking ?? throw ThreadNotFound();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The booking beside the conversation
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The booking card and the guest of the booking. A booking of another property than the thread's is not shown (a thread is never shown under a
    /// property it was not mapped to, and a stray link must not show a stay at a house the caller may not reach); one of another org is not found.
    /// The money is read only for a caller who may read payments.
    /// </summary>
    private async Task<BookingCardResult?> BookingCardAsync(
        HostScope scope,
        Guid? bookingId,
        Guid? threadPropertyId,
        string? threadGuestName,
        bool includeAmounts,
        CancellationToken cancellationToken)
    {
        if (bookingId is not { } id)
            return null;

        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == id)
            .Select(b => new
            {
                b.Id,
                b.BookingCode,
                b.GuestId,
                GuestFirstName = b.Guest.FirstName,
                GuestLastName = b.Guest.LastName,
                b.PropertyId,
                b.Property.Currency,
                b.CheckInDate,
                b.CheckOutDate,
                b.NumberOfGuests,
                b.NumberOfAdults,
                b.NumberOfChildren,
                b.Status,
                b.Source,
                b.ChannelLabel,
                b.OctorateReservationId,
                b.TotalPrice,
                b.OtaCommission,
                b.OtaNetAmount,
                b.OtaPaidAmount,
                b.OtaBalanceAmount,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null || (threadPropertyId is { } propertyId && booking.PropertyId != propertyId))
            return null;

        MessageBookingAmounts? amounts = null;
        if (includeAmounts)
        {
            // Whose money the booking has is the one rule of NF-77 (BookingAmountSourceRules): a booking of a channel, or a reservation of the booking
            // engine of the agency that the import made, has the figures of the channel manager; a booking CasaZen made and sent to Octorate has a
            // reservation there too (OctorateReservationId), but its money is CasaZen's, and only the row of the mirror tells the two apart.
            var facts = new Booking { Source = booking.Source, OctorateReservationId = booking.OctorateReservationId };
            var pushedByCasaZen = !string.IsNullOrWhiteSpace(booking.OctorateReservationId)
                && await db.OctorateBookingSyncs
                    .AsNoTracking()
                    .AnyAsync(s => s.OrgId == scope.OrgId && s.BookingId == id, cancellationToken);
            if (BookingAmountSourceRules.HoldsChannelManagerMoney(facts, pushedByCasaZen))
            {
                // A booking of a channel: the four figures the channel manager gave, as stored (null stays null) and the total when it is set.
                amounts = new MessageBookingAmounts(
                    booking.TotalPrice > 0m ? booking.TotalPrice : null,
                    booking.OtaPaidAmount,
                    booking.OtaCommission,
                    booking.OtaNetAmount,
                    booking.OtaBalanceAmount);
            }
            else
            {
                var payments = await db.Payments
                    .AsNoTracking()
                    .Where(p => p.OrgId == scope.OrgId && p.BookingId == id)
                    .Select(p => new BookingPaymentFact(p.Status, p.Amount, p.RefundedAmount))
                    .ToListAsync(cancellationToken);
                amounts = new MessageBookingAmounts(booking.TotalPrice, BookingListRules.PaidAmount(payments), null, null, null);
            }
        }

        var name = $"{booking.GuestFirstName} {booking.GuestLastName}".Trim();
        var card = new MessageBookingCard(
            booking.Id,
            BookingCodes.Format(booking.BookingCode),
            name.Length > 0 ? name : threadGuestName ?? string.Empty,
            booking.CheckInDate,
            booking.CheckOutDate,
            booking.NumberOfGuests,
            booking.NumberOfAdults,
            booking.NumberOfChildren,
            booking.Status,
            booking.Source,
            booking.ChannelLabel,
            StayCurrency.Normalize(booking.Currency),
            amounts);
        return new BookingCardResult(card, booking.GuestId, name.Length > 0 ? name : null);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static NotFoundException ThreadNotFound() =>
        new("The message thread was not found or is not within reach") { Code = ThreadNotFoundCode, MessageKey = "MessageThreadNotFound" };

    private static NotFoundException MessageNotFound() =>
        new("The message was not found or is not within reach") { Code = MessageNotFoundCode, MessageKey = "MessageNotFound" };

    private static NotFoundException AttachmentUnavailable() =>
        new("The attachment is no longer available") { Code = AttachmentUnavailableCode, MessageKey = "AttachmentUnavailable" };

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private sealed record DirectBooking(Guid PropertyId, string PropertyName);

    private sealed record BookingCardResult(MessageBookingCard Card, Guid GuestId, string? GuestName);

    private sealed record LastMessage(MessageOrigin Origin, string? Preview);

    /// <summary>
    /// A row of the list in the shape both sources are projected to. Plain columns and codes only; the numbers are those of
    /// <see cref="MessageThreadSource"/>, <see cref="MessageHubChannel"/>, <see cref="MessageHubThreadKind"/> and <see cref="BookingStatus"/>.
    /// </summary>
    private sealed class HubRow
    {
        public int Source { get; set; }

        /// <summary>The id of the booking for a direct conversation, the id of the thread for a conversation of a channel (the id of the key).</summary>
        public Guid KeyId { get; set; }

        public int Channel { get; set; }

        public int Kind { get; set; }

        public Guid? PropertyId { get; set; }

        public string? PropertyName { get; set; }

        public Guid? BookingId { get; set; }

        public string? BookingCode { get; set; }

        public string? GuestName { get; set; }

        public DateTime? CheckInDate { get; set; }

        public DateTime? CheckOutDate { get; set; }

        public int? BookingStatus { get; set; }

        public bool IsArchived { get; set; }

        public DateTime LastMessageAt { get; set; }

        public int UnreadCount { get; set; }

        public bool NeedsReply { get; set; }
    }
}
