using System.Net.Mail;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IMessageReplyService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One path for the channel.</b> The checks come in the order of the brief: the conversation within the reach of the scope (404), a guest who
/// was not erased (409 <c>thread_closed</c>), a property whose mapping is decided (409 <c>mapping_not_confirmed</c>, OCT-04), the connector on, the key and the
/// rules of the channel (422), and only then the message with the same key (<c>Replayed</c>). The row of the message is written <b>before</b> the call and
/// committed, under the advisory lock of the conversation that the copy of the conversation takes too (<see cref="PostgresAdvisoryLocks.Scope.OtaMessageThread"/>),
/// so two sends with one key write one row; the call to Octorate is made with no lock and no transaction held; the outcome is then stored under the lock again.</item>
/// <item><b>Four ends of a send, none of them a retry.</b> The channel manager accepted it: its id and its state are stored, the thread counts as answered
/// and its inbound messages as read. It refused it with a typed error (a refused token, a chat module not in the license, a request it rejects): the row
/// is <see cref="OtaMessageDeliveryState.Error"/> and the code is in the answer. It did not answer, or answered something that cannot be read: the row is
/// <see cref="OtaMessageDeliveryState.ManualVerify"/>, and nothing in this class sends it again (OCT-10, OCT-13): the copy of the conversation matches it
/// with the message that arrived. It answered 429, or CasaZen's own bucket of calls was empty: nothing happened, so the row is taken away again and the
/// 429 reaches the caller, who may send again with the same key.</item>
/// <item><b>A row that was written and never sent.</b> If the process stopped between the row and the call, the row stays in
/// <see cref="OtaMessageDeliveryState.Created"/> with no id. A second send with its key, after <see cref="StaleSendWindow"/>, finds it and turns it into
/// <see cref="OtaMessageDeliveryState.ManualVerify"/>: it may or may not have left, and it is never shown as sent.</item>
/// <item><b>The e-mail to a guest with no conversation</b> is not a conversation of the hub and has no row: the key is remembered in memory for an hour
/// (<see cref="EmailKeyMemory"/>, per replica, like the limits of the AI), so a double click or a retry sends one e-mail. The contact is read only after the
/// checks of the booking, once, and its address goes to the queue of the e-mails and nowhere else.</item>
/// <item><b>Logs</b> carry ids, states and codes: never a word of a message, a name, an address or a number.</item>
/// </list>
/// </remarks>
public sealed class MessageReplyService(
    AppDbContext db,
    IGuestMessageService guestMessages,
    IChannelMappingService channelMapping,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateBookingTargets targets,
    IFeatureFlags featureFlags,
    IEmailQueue emailQueue,
    IActivityLog activityLog,
    IMemoryCache cache,
    ILogger<MessageReplyService> logger,
    TimeProvider? timeProvider = null) : IMessageReplyService
{
    /// <summary>Stable code of the 409 of a conversation whose listing has no decided mapping.</summary>
    public const string MappingNotConfirmedCode = "mapping_not_confirmed";

    /// <summary>Stable code of the 409 of a read of the contact that the channel manager does not release.</summary>
    public const string ContactInfoUnavailableCode = "contact_info_unavailable";

    /// <summary>Stable code of the 409 of a booking with no way to write to its guest.</summary>
    public const string NoContactChannelCode = "no_contact_channel";

    /// <summary>The code of a message that could not be handed to the queue of the e-mails (the state is <see cref="OtaMessageDeliveryState.Error"/>).</summary>
    public const string EmailNotQueuedCode = "email_not_queued";

    /// <summary>The code of a message the channel manager accepted and then reports as failed (<c>ERROR</c> in its own answer).</summary>
    public const string ChannelReportedErrorCode = "octorate_message_error";

    /// <summary>A row in <see cref="OtaMessageDeliveryState.Created"/> with no id older than this was never sent by a call that is still running.</summary>
    public static readonly TimeSpan StaleSendWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long the key of an e-mail to a guest is remembered.</summary>
    public static readonly TimeSpan EmailKeyMemory = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<MessageReplyResult> ReplyAsync(
        HostScope scope,
        MessageThreadKey key,
        string? text,
        string? idempotencyKey,
        string? userId,
        MessageOrigin origin,
        string? ruleCode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CheckAuthor(origin, ruleCode);

        return key.Source == MessageThreadSource.Direct
            ? await ReplyDirectAsync(scope, key, text, idempotencyKey, userId, ct)
            : await ReplyOtaAsync(scope, key, text, idempotencyKey, userId, origin, ruleCode, ct);
    }

    public async Task<MessageContactOptions> ChooseChannelAsync(HostScope scope, Guid bookingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return (await ChooseAsync(scope, bookingId, ct)).Options;
    }

    public async Task<MessageReplyResult> StartConversationAsync(
        HostScope scope,
        Guid bookingId,
        string? text,
        string? idempotencyKey,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // What does not depend on the booking is refused before the channel manager is asked anything (the options of an e-mail read the reservation).
        if (GuestMessageRules.NormalizeText(text) is not { Length: <= GuestMessageRules.TextMaxLength })
            throw new DomainRuleException(MessageChannelRules.TextInvalidCode, "GuestMessageTextInvalid");

        if (GuestMessageRules.NormalizeIdempotencyKey(idempotencyKey) is null)
            throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");

        var choice = await ChooseAsync(scope, bookingId, ct);

        switch (choice.Options.Channel)
        {
            case MessageContactChannel.Ota or MessageContactChannel.Direct when choice.Options.ThreadKey is { } threadKey:
                return await ReplyAsync(scope, threadKey, text, idempotencyKey, userId, MessageOrigin.Person, null, ct);
            case MessageContactChannel.Email:
                return await SendByEmailAsync(scope, choice, text, idempotencyKey, userId, ct);
            default:
                throw NoChannel(choice.Options.Reason);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // A conversation with the guest of a direct booking
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<MessageReplyResult> ReplyDirectAsync(
        HostScope scope,
        MessageThreadKey key,
        string? text,
        string? idempotencyKey,
        string? userId,
        CancellationToken ct)
    {
        // The booking is within the reach of the scope, or the conversation is not there: only then is the existing service asked, unchanged.
        var reachable = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == key.Id)
            .InScope(scope)
            .AnyAsync(ct);
        if (!reachable)
            throw ThreadNotFound();

        var sent = await guestMessages.ReplyAsHostAsync(key.Id, text, idempotencyKey, userId, ct);
        var message = new MessageHubMessage(
            sent.Message.Id,
            OtaMessageDirection.Outbound,
            MessageOrigin.Person,
            null,
            null,
            sent.Message.Text,
            sent.Message.CreatedAt,
            null,
            null,
            null);
        return new MessageReplyResult(MessageContactChannel.Direct, key, message, OtaMessageDeliveryState.Sent, sent.Replayed, null);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // A conversation of a channel
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<MessageReplyResult> ReplyOtaAsync(
        HostScope scope,
        MessageThreadKey key,
        string? text,
        string? idempotencyKey,
        string? userId,
        MessageOrigin origin,
        string? ruleCode,
        CancellationToken ct)
    {
        // The conversation as the caller sees it: the org of the scope, and the properties it reaches (a conversation with no property is only in the
        // scope of whoever reaches every property). Anything else is a 404.
        var thread = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId && t.Id == key.Id)
            .InScope(scope)
            .Select(t => new ThreadFacts(
                t.Id, t.OrgId, t.PropertyId, t.OctorateThreadId, t.OctorateAccommodationId, t.ChannelSource, t.Kind, t.AnonymizedAt != null))
            .FirstOrDefaultAsync(ct)
            ?? throw ThreadNotFound();

        // The guest of an erased conversation is gone: nothing is written to it.
        if (thread.Anonymized)
            throw ThreadClosed();

        // OCT-04: nothing is written to a channel for a property whose listing was not decided, and nothing is sent when the connector is off.
        if (thread.PropertyId is not { } propertyId || await channelMapping.IsWriteBlockedAsync(propertyId, ct))
            throw MappingNotConfirmed();

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            throw OctorateAuthException.NotConnected();

        var clean = GuestMessageRules.NormalizeText(text);
        if (MessageChannelRules.CheckOutgoing(thread.Channel, thread.Kind, clean) is { } violation)
        {
            throw new DomainRuleException(
                violation,
                violation == MessageChannelRules.ContactDetailsCode ? "MessageChannelRuleViolation" : "GuestMessageTextInvalid");
        }

        var idempotency = GuestMessageRules.NormalizeIdempotencyKey(idempotencyKey)
            ?? throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");

        // The row is written before the call, under the lock of the conversation; a second send with the same key finds it and sends nothing.
        var (row, replayed) = await WriteBeforeSendingAsync(thread, clean!, idempotency, userId, origin, ruleCode, ct);
        if (replayed)
            return ResultOf(key, row, replayed: true, errorCode: null);

        OctorateChatSendResult sent;
        try
        {
            using (orgScope.Use(thread.OrgId))
                sent = await client.SendChatMessageAsync(thread.OctorateAccommodationId, thread.OctorateThreadId, clean!, ct);
        }
        catch (OctorateServerException ex)
        {
            // No answer, a 5xx, or an answer that cannot be read: the message may have left. It is not sent again here (OCT-10, OCT-13).
            logger.LogWarning(
                "OTA message {MessageId} of thread {ThreadId} (org {OrgId}): the outcome of the send is not known ({Code}); it waits for a person",
                row.Id, thread.Id, thread.OrgId, ex.Code);
            await StoreOutcomeAsync(thread, row, null, OtaMessageDeliveryState.ManualVerify, CancellationToken.None);
            return ResultOf(key, row, replayed: false, errorCode: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller left while the message was on its way: just as unknown.
            await StoreOutcomeAsync(thread, row, null, OtaMessageDeliveryState.ManualVerify, CancellationToken.None);
            throw;
        }
        catch (OctorateRateLimitException)
        {
            // Nothing happened (the bucket of the org was empty, or Octorate answered 429 after the waits): the row is taken away again, so that the
            // same key can be sent when the limit is over, and the 429 reaches the caller.
            await DiscardAsync(row);
            throw;
        }
        catch (OctorateException ex)
        {
            // A typed refusal: a message the channel manager did not take. The state says so, with the code.
            logger.LogWarning(
                "OTA message {MessageId} of thread {ThreadId} (org {OrgId}) was not sent: {Code} (HTTP {Status})",
                row.Id, thread.Id, thread.OrgId, ex.Code, ex.OctorateStatusCode);
            await StoreOutcomeAsync(thread, row, null, OtaMessageDeliveryState.Error, CancellationToken.None);
            return ResultOf(key, row, replayed: false, errorCode: ex.Code);
        }
        catch (ArgumentException)
        {
            // An id Octorate could not take (the checks of the client): nothing left CasaZen.
            logger.LogWarning(
                "OTA message {MessageId} of thread {ThreadId} (org {OrgId}) was not sent: the ids of the conversation are not valid",
                row.Id, thread.Id, thread.OrgId);
            await StoreOutcomeAsync(thread, row, null, OtaMessageDeliveryState.Error, CancellationToken.None);
            return ResultOf(key, row, replayed: false, errorCode: OctorateRequestRejectedException.RejectedCode);
        }

        await StoreOutcomeAsync(thread, row, sent.MessageId, sent.State, CancellationToken.None);
        logger.LogInformation(
            "OTA message {MessageId} of thread {ThreadId} (org {OrgId}) handed to the channel manager: {State}",
            row.Id, thread.Id, thread.OrgId, row.DeliveryState);
        return ResultOf(key, row, replayed: false, errorCode: row.DeliveryState == OtaMessageDeliveryState.Error ? ChannelReportedErrorCode : null);
    }

    /// <summary>
    /// Writes the outbound row (<see cref="OtaMessageDeliveryState.Created"/>) under the lock of the conversation, or finds the one the same key wrote
    /// before. A row that was written and never sent is turned into <see cref="OtaMessageDeliveryState.ManualVerify"/> when it is found after
    /// <see cref="StaleSendWindow"/>: it is not known whether it left.
    /// </summary>
    private async Task<(OtaMessage Row, bool Replayed)> WriteBeforeSendingAsync(
        ThreadFacts thread,
        string text,
        string idempotencyKey,
        string? userId,
        MessageOrigin origin,
        string? ruleCode,
        CancellationToken ct)
    {
        var now = Now();
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OtaMessageThread, LockKey(thread)));

        var earlier = await db.OtaMessages
            .FirstOrDefaultAsync(m => m.OrgId == thread.OrgId && m.ThreadId == thread.Id && m.IdempotencyKey == idempotencyKey, ct);
        if (earlier is not null)
        {
            if (earlier.DeliveryState == OtaMessageDeliveryState.Created
                && earlier.OctorateMessageId is null
                && earlier.CreatedAt <= now - StaleSendWindow)
            {
                earlier.DeliveryState = OtaMessageDeliveryState.ManualVerify;
                await db.SaveChangesAsync(ct);
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return (earlier, true);
        }

        var row = new OtaMessage
        {
            OrgId = thread.OrgId,
            ThreadId = thread.Id,
            Direction = OtaMessageDirection.Outbound,
            Text = text,
            SentAt = now,
            DeliveryState = OtaMessageDeliveryState.Created,
            IdempotencyKey = idempotencyKey,
            Origin = origin,
            RuleCode = ruleCode,
            SentByUserId = userId is { Length: > 0 and <= OtaMessage.SentByUserIdMaxLength } ? userId : null,
            CreatedAt = now,
        };
        db.OtaMessages.Add(row);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return (row, false);
    }

    /// <summary>
    /// Stores what came of the send, under the lock of the conversation. With an id, the message is the one the channel manager has: if the copy of the
    /// conversation already stored it as a message of the host side (it read the thread between the call and this save), that row is the same message and goes,
    /// because this one knows who wrote it; a message that went out marks the conversation answered and its inbound messages read.
    /// </summary>
    private async Task StoreOutcomeAsync(ThreadFacts thread, OtaMessage row, string? octorateMessageId, OtaMessageDeliveryState state, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OtaMessageThread, LockKey(thread)));

        if (octorateMessageId is not null)
        {
            var twins = await db.OtaMessages
                .Where(m => m.OrgId == thread.OrgId && m.OctorateMessageId == octorateMessageId && m.Id != row.Id)
                .ToListAsync(ct);
            db.OtaMessages.RemoveRange(twins);
            row.OctorateMessageId = octorateMessageId;
        }

        row.DeliveryState = state;

        if (state is OtaMessageDeliveryState.Created or OtaMessageDeliveryState.WaitingAuthorization or OtaMessageDeliveryState.Sent)
        {
            var now = Now();
            var unread = await db.OtaMessages
                .Where(m => m.OrgId == thread.OrgId && m.ThreadId == thread.Id && m.Direction == OtaMessageDirection.Inbound && m.ReadAt == null)
                .ToListAsync(ct);
            foreach (var message in unread)
                message.ReadAt = now;

            // A conversation that was erased in the meantime has nothing left to count.
            if (await db.OtaMessageThreads.FirstOrDefaultAsync(t => t.OrgId == thread.OrgId && t.Id == thread.Id, ct) is { } conversation)
            {
                conversation.UnreadCount = 0;
                if (row.SentAt >= conversation.LastMessageAt)
                {
                    conversation.LastMessageAt = row.SentAt;
                    conversation.LastMessageDirection = OtaMessageDirection.Outbound;
                }

                conversation.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    /// <summary>Takes the row of a send that did not happen away again (a 429): the same key can be sent later.</summary>
    private async Task DiscardAsync(OtaMessage row)
    {
        db.OtaMessages.Remove(row);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static MessageReplyResult ResultOf(MessageThreadKey key, OtaMessage row, bool replayed, string? errorCode) =>
        new(MessageContactChannel.Ota, key, HubMessageOf(row), row.DeliveryState, replayed, errorCode);

    /// <summary>The message as the hub shows an outbound message of a channel: no name of the sender, never read, no file.</summary>
    private static MessageHubMessage HubMessageOf(OtaMessage row) => new(
        row.Id,
        row.Direction,
        row.Origin,
        row.RuleCode,
        null,
        row.Text,
        row.SentAt,
        null,
        row.Direction == OtaMessageDirection.Outbound ? row.DeliveryState : null,
        null);

    // ------------------------------------------------------------------------------------------------------------------------------------
    // Which channel reaches the guest of a booking
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<Choice> ChooseAsync(HostScope scope, Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId)
            .InScope(scope)
            .Select(b => new BookingFacts(
                b.Id,
                b.OrgId,
                b.PropertyId,
                b.Source,
                b.Status,
                b.CheckOutDate,
                b.OctorateReservationId,
                b.Guest.DataAnonymizedDate == null && !b.Guest.IsDeleted && b.Guest.Email != string.Empty,
                b.Guest.DataAnonymizedDate == null && !b.Guest.IsDeleted && b.Guest.PhoneNumber != string.Empty))
            .FirstOrDefaultAsync(ct)
            ?? throw BookingNotFound();

        if (OtaChannelActionPolicy.IsChannelBooking(booking.Source, booking.OctorateReservationId))
            return await ChooseForChannelBookingAsync(booking, ct);

        if (booking.Source == BookingSource.Direct)
        {
            return GuestMessageRules.StateOf(booking.Source, booking.Status, booking.CheckOutDate, Now()) switch
            {
                GuestThreadState.Open => new Choice(
                    booking.Id,
                    new MessageContactOptions(MessageContactChannel.Direct, MessageThreadKey.ForDirect(booking.Id), null, booking.HasEmail, booking.HasPhone)),
                GuestThreadState.Closed => new Choice(booking.Id, None(MessageContactOptions.ThreadClosedReason, booking.HasEmail, booking.HasPhone)),
                _ => new Choice(booking.Id, None(MessageContactOptions.NoConversationReason, booking.HasEmail, booking.HasPhone)),
            };
        }

        // A stay entered by the host or taken from an iCal feed has no conversation anywhere.
        return new Choice(booking.Id, None(MessageContactOptions.NoConversationReason, false, false));
    }

    private async Task<Choice> ChooseForChannelBookingAsync(BookingFacts booking, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new Choice(booking.Id, None(MessageContactOptions.NotConnectedReason, false, false));

        // The conversation of the booking, at the property of the booking (a conversation moved to another house by a new mapping is not its own).
        var thread = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == booking.OrgId && t.BookingId == booking.Id && t.PropertyId == booking.PropertyId && t.AnonymizedAt == null)
            .OrderByDescending(t => t.LastMessageAt)
            .ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.PropertyId })
            .FirstOrDefaultAsync(ct);
        if (thread is not null)
        {
            // OCT-04: a conversation of a listing nobody decided is not chosen.
            return thread.PropertyId is not { } propertyId || await channelMapping.IsWriteBlockedAsync(propertyId, ct)
                ? new Choice(booking.Id, None(MessageContactOptions.MappingNotConfirmedReason, false, false))
                : new Choice(booking.Id, new MessageContactOptions(MessageContactChannel.Ota, MessageThreadKey.ForOta(thread.Id), null, false, false));
        }

        // A channel that has a chat but no conversation yet: the guest has not written, and the channel does not let the host start one.
        if (booking.OtaChat)
            return new Choice(booking.Id, None(MessageContactOptions.NoConversationReason, false, false));

        // A channel with no chat: the only way is an e-mail, if the channel manager holds the guest's address (a flag; the address is not asked for).
        if (await targets.AccommodationOfAsync(booking.PropertyId, ct) is not { } accommodation)
            return new Choice(booking.Id, None(MessageContactOptions.MappingNotConfirmedReason, false, false));

        OctorateReservation reservation;
        using (orgScope.Use(booking.OrgId))
            reservation = await client.GetReservationAsync(accommodation, booking.OctorateReservationId!, ct);

        var emailAvailable = reservation.GuestEmailAvailable == true;
        var phoneAvailable = reservation.GuestPhoneAvailable == true;
        if (!emailAvailable || string.IsNullOrWhiteSpace(reservation.Refer))
            return new Choice(booking.Id, None(MessageContactOptions.ContactInfoUnavailableReason, emailAvailable, phoneAvailable));

        return new Choice(
            booking.Id,
            new MessageContactOptions(MessageContactChannel.Email, null, null, true, phoneAvailable),
            accommodation,
            reservation.Refer.Trim());
    }

    private static MessageContactOptions None(string reason, bool emailAvailable, bool phoneAvailable) =>
        new(MessageContactChannel.None, null, reason, emailAvailable, phoneAvailable);

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The e-mail to the guest of a booking of a channel with no chat
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<MessageReplyResult> SendByEmailAsync(
        HostScope scope,
        Choice choice,
        string? text,
        string? idempotencyKey,
        string? userId,
        CancellationToken ct)
    {
        var bookingId = choice.BookingId;
        var clean = GuestMessageRules.NormalizeText(text);
        if (clean is null || clean.Length > GuestMessageRules.TextMaxLength)
            throw new DomainRuleException(MessageChannelRules.TextInvalidCode, "GuestMessageTextInvalid");

        var idempotency = GuestMessageRules.NormalizeIdempotencyKey(idempotencyKey)
            ?? throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");

        // The same key is the same e-mail: nothing is read from the channel manager and nothing is queued twice.
        var memory = new EmailSendKey(scope.OrgId, bookingId, idempotency);
        if (cache.TryGetValue(memory, out _))
            return new MessageReplyResult(MessageContactChannel.Email, null, null, OtaMessageDeliveryState.Sent, true, null);

        // The contact of THIS booking, asked for once, because a person is writing to it now; Octorate records the read and so does the activity log.
        OctorateGuestContactInfo contact;
        try
        {
            using (orgScope.Use(scope.OrgId))
                contact = await client.GetReservationContactInfoAsync(choice.AccommodationId!, choice.Refer!, ct);
        }
        catch (Exception ex) when (ex is OctorateSensitiveDataNotAllowedException or OctorateNotFoundException or OctorateRequestRejectedException)
        {
            // Nothing was released: no line in the activity log, no e-mail, and the answer says the contact cannot be read.
            logger.LogWarning(
                "The contact of booking {BookingId} (org {OrgId}) was not released by the channel manager ({Code})",
                bookingId, scope.OrgId, ((OctorateException)ex).Code);
            throw ContactInfoUnavailable();
        }

        activityLog.Record(OrgActivity.Of(
            scope.OrgId,
            OrgActivityType.ContactInfoRead,
            userId is { Length: > 0 and <= OrgActivityCatalog.MaxIdLength } ? userId : null,
            bookingId.ToString(),
            (OrgActivityDetailKeys.Purpose, ContactReadPurposes.MessageToGuest)));
        await db.SaveChangesAsync(ct);

        // An answer that was not understood, or with no usable address, is not a guest with no contact: say the contact cannot be read.
        var address = contact.Recognized ? contact.Email?.Trim() : null;
        if (string.IsNullOrEmpty(address)
            || !MailAddress.TryCreate(address, out var parsed)
            || !string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("The channel manager gave no usable address for booking {BookingId} (org {OrgId})", bookingId, scope.OrgId);
            throw ContactInfoUnavailable();
        }

        var facts = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId)
            .Select(b => new EmailFacts(
                b.Property.Name, b.Org.HostName, b.Org.DisplayName, b.Org.ContactEmail, b.CheckInDate, b.CheckOutDate, b.Guest.Country))
            .FirstAsync(ct);
        var hostName = !string.IsNullOrWhiteSpace(facts.HostName) ? facts.HostName.Trim()
            : !string.IsNullOrWhiteSpace(facts.OrgDisplayName) ? facts.OrgDisplayName.Trim()
            : facts.PropertyName;
        var content = EmailTemplates.HostGuestMessage(
            EmailCultureOf(facts.GuestCountry),
            hostName,
            facts.PropertyName,
            RomeCalendar.DateInRome(facts.CheckInDate).ToDateTime(TimeOnly.MinValue),
            RomeCalendar.DateInRome(facts.CheckOutDate).ToDateTime(TimeOnly.MinValue),
            clean,
            string.IsNullOrWhiteSpace(facts.ContactEmail) ? null : facts.ContactEmail.Trim());

        // The address goes to the queue and nowhere else: not in the answer, not in a log, not in a row of ours.
        if (!emailQueue.Enqueue(address, content, EmailTemplates.Names.HostGuestMessage))
        {
            logger.LogWarning("The e-mail to the guest of booking {BookingId} (org {OrgId}) could not be queued", bookingId, scope.OrgId);
            return new MessageReplyResult(MessageContactChannel.Email, null, null, OtaMessageDeliveryState.Error, false, EmailNotQueuedCode);
        }

        cache.Set(memory, true, EmailKeyMemory);
        logger.LogInformation("The e-mail to the guest of booking {BookingId} (org {OrgId}) was queued", bookingId, scope.OrgId);
        return new MessageReplyResult(MessageContactChannel.Email, null, null, OtaMessageDeliveryState.Sent, false, null);
    }

    /// <summary>Italian for a guest of Italy or of no known country, English for everyone else: the e-mail has no other language to go by.</summary>
    internal static System.Globalization.CultureInfo EmailCultureOf(string? country)
    {
        var normalized = country?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(normalized) || normalized is "IT" or "ITA" or "ITALIA" or "ITALY"
            ? EmailTemplates.DefaultCulture
            : System.Globalization.CultureInfo.GetCultureInfo("en");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static void CheckAuthor(MessageOrigin origin, string? ruleCode)
    {
        if (origin == MessageOrigin.Guest)
            throw new ArgumentException("A reply is written by the host side, never by the guest.", nameof(origin));

        if (ruleCode is not null && (origin != MessageOrigin.Assistant || ruleCode.Length is 0 or > OtaMessage.RuleCodeMaxLength))
            throw new ArgumentException("A rule code belongs to a message of the assistant and is at most 64 characters.", nameof(ruleCode));
    }

    private static string LockKey(ThreadFacts thread) => $"{thread.OrgId:N}:{thread.OctorateThreadId}";

    private DateTime Now()
    {
        var utc = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    private static NotFoundException ThreadNotFound() =>
        new("The message thread was not found or is not within reach") { Code = MessageHubService.ThreadNotFoundCode, MessageKey = "MessageThreadNotFound" };

    private static NotFoundException BookingNotFound() =>
        new("The booking was not found or is not within reach") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private static DomainConflictException ThreadClosed() => new("thread_closed", "GuestThreadClosed");

    private static DomainConflictException MappingNotConfirmed() => new(MappingNotConfirmedCode, "MessageMappingNotConfirmed");

    private static DomainConflictException ContactInfoUnavailable() => new(ContactInfoUnavailableCode, "MessageContactInfoUnavailable");

    /// <summary>The 409 of a booking with nobody to write to: the reason the options gave, as the code the client already knows when there is one.</summary>
    private static DomainConflictException NoChannel(string? reason) => reason switch
    {
        MessageContactOptions.MappingNotConfirmedReason => MappingNotConfirmed(),
        MessageContactOptions.ThreadClosedReason => ThreadClosed(),
        MessageContactOptions.ContactInfoUnavailableReason => ContactInfoUnavailable(),
        MessageContactOptions.NotConnectedReason => new DomainConflictException(OctorateAuthException.NotConnectedCode, "OctorateNotConnected"),
        _ => new DomainConflictException(NoContactChannelCode, "MessageNoContactChannel"),
    };

    private sealed record ThreadFacts(
        Guid Id,
        Guid OrgId,
        Guid? PropertyId,
        string OctorateThreadId,
        string OctorateAccommodationId,
        OtaChannel Channel,
        OtaThreadKind Kind,
        bool Anonymized);

    private sealed record BookingFacts(
        Guid Id,
        Guid OrgId,
        Guid PropertyId,
        BookingSource Source,
        BookingStatus Status,
        DateTime CheckOutDate,
        string? OctorateReservationId,
        bool HasEmail,
        bool HasPhone)
    {
        /// <summary>The channel of the booking is one of the four that have a chat (the others are written to by e-mail).</summary>
        public bool OtaChat => OtaChannelMapper.Of(Source) is not null;
    }

    /// <summary>What <see cref="ChooseAsync"/> found: the options for the caller, and for an e-mail the accommodation and the <c>refer</c> the read of the contact needs.</summary>
    private sealed record Choice(Guid BookingId, MessageContactOptions Options, string? AccommodationId = null, string? Refer = null);

    private sealed record EmailFacts(
        string PropertyName,
        string? HostName,
        string OrgDisplayName,
        string ContactEmail,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        string GuestCountry);

    private readonly record struct EmailSendKey(Guid OrgId, Guid BookingId, string Key);
}
