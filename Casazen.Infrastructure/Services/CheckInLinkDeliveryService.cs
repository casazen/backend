using System.Runtime.ExceptionServices;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICheckInLinkDeliveryService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The order of the ways.</b> An address the booking has (the e-mail as for any booking); else, for a stay from a portal, the chat first (it reads no personal
/// data: one message in the reservation thread of the booking) and then, when the chat did not deliver and <see cref="FeatureFlags.GuestContactInfo"/> is on, the contact
/// of the guest read once for this link (<see cref="IGuestContactInfoService"/>) and the e-mail; else the host. The state of the row is the chat's while the chat is on, and
/// the contact's otherwise.</item>
/// <item><b>One message, once.</b> The message goes through <see cref="IMessageReplyService.ReplyAsync"/> with the origin <see cref="MessageOrigin.Automation"/> and no
/// user, so it is never shown as written by a person, never marks the conversation answered or the guest's messages read, and follows every rule of a reply (the
/// listing mapped, the connector on, the rules of the channel, the thread of the booking within the org). Its key is the booking and the token
/// (<see cref="CheckInLinkDeliveryRules.IdempotencyKey"/>): a second attempt for the same link finds the message that exists and sends nothing. Only a message the
/// channel manager <b>refused</b> (the state <see cref="OtaMessageDeliveryState.Error"/>, nothing left) lets the next attempt use the next key of the same link; a
/// message whose outcome is unknown (<see cref="OtaMessageDeliveryState.ManualVerify"/>) is never sent again: the row is <see cref="CheckInLinkDeliveryState.Failed"/>
/// with <see cref="CheckInLinkDeliveryRules.ManualVerifyCode"/> and the host looks at the thread.</item>
/// <item><b>Delivered means sent.</b> A message the channel manager has only created (<see cref="OtaMessageDeliveryState.Created"/>) or holds for the authorization of the channel
/// (<see cref="OtaMessageDeliveryState.WaitingAuthorization"/>) is not delivered: the row is <see cref="CheckInLinkDeliveryState.Sending"/>, keeps its token and the «To do» it has, and the hourly
/// pass reads the message again (<c>SettleSendingAsync</c>): sent, the row is <see cref="CheckInLinkDeliveryState.Sent"/> (the token is cleared and the «To do» closed); in error, it is
/// <see cref="CheckInLinkDeliveryState.Failed"/> with <see cref="MessageReplyService.ChannelReportedErrorCode"/>, the host gets its «To do» and the notice, and «Copia link» still has the link.</item>
/// <item><b>Nothing is swallowed.</b> What the channel manager answers is the state of the row, with its stable code; the codes of a limit and of an outage
/// (<see cref="TransientCodes"/>) are tried again by the hourly pass up to <see cref="CheckInLinkDeliveryRules.MaxAttempts"/> attempts, the others wait for the thing
/// that changes them (the sync of the chat, a thread, a day) or for the host. An exception that is not an answer of the channel manager reaches the caller.</item>
/// <item><b>The host.</b> Only while one of the new flags is on and the booking is a stay from a portal: a waiting or failed row has a «To do»
/// (<see cref="ActivityType.CheckInLinkToSend"/>, once per booking, however it ends), closed when the link is sent or the row ends; a row that failed for good tells the
/// hosts of the booking through the bell and the live update (<see cref="PushTypes.CheckInLinkFailed"/>, once per link). With both flags off, nothing of this exists.</item>
/// <item><b>The token.</b> Kept encrypted on the row only while the new behavior is on for the booking and the link has not been sent; cleared when it is sent or the row
/// ends. Never in a log, never in a notification.</item>
/// <item><b>Logs</b> carry the booking, the org, the state and a code: never a link, an address, a number or a word of a message.</item>
/// </list>
/// </remarks>
public sealed class CheckInLinkDeliveryService(
    AppDbContext db,
    IGuestCheckInLinkEmailQueue linkEmails,
    IMessageReplyService replies,
    IGuestContactInfoService contacts,
    IOctorateBookingTargets targets,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    PublicSiteLinks publicSiteLinks,
    ILogger<CheckInLinkDeliveryService> logger,
    TimeProvider? timeProvider = null,
    IPushNotificationService? push = null) : ICheckInLinkDeliveryService
{
    /// <summary>
    /// The codes of a failure worth a retry: the channel manager used up the calls of the org (<c>octorate_rate_limited</c>), or did not answer or answered something
    /// that cannot be read (<c>octorate_unavailable</c>). The same as <see cref="OctorateRateLimitException"/> and <see cref="OctorateChatNotReadException"/> carry; a test keeps them equal.
    /// </summary>
    internal static readonly string[] TransientCodes = ["octorate_rate_limited", "octorate_unavailable"];

    /// <summary>The most rows one pass of the hourly job looks at.</summary>
    internal const int RetryBatchSize = 200;

    /// <summary>The code of a failure to queue the e-mail that has none of its own.</summary>
    internal const string EmailNotQueuedCode = "email_not_queued";

    /// <summary>The code of a link that cannot be delivered because its check-in session is not there.</summary>
    internal const string LinkNotUsableCode = "link_not_usable";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CheckInLinkDeliveryView> DeliverAsync(Guid bookingId, IssuedCheckInLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var now = Now();
        var booking = await LoadBookingAsync(bookingId, null, ct) ?? throw BookingNotFound();

        var delivery = await db.CheckInLinkDeliveries
            .FirstOrDefaultAsync(d => d.OrgId == booking.OrgId && d.BookingId == bookingId, ct);
        if (delivery is not null && delivery.EndedAt is null && delivery.SessionId == link.SessionId)
            return ViewOf(delivery, link.ExpiresAt, link: null);

        if (delivery is null)
        {
            // DbSet.Add: the row carries its own Guid, so only Add tracks it as new.
            delivery = new CheckInLinkDelivery { OrgId = booking.OrgId, BookingId = booking.Id, CreatedAt = now };
            db.CheckInLinkDeliveries.Add(delivery);
        }

        // The row starts again for the new link. With both flags off it holds no token (nobody needs it) and says only why the link is not going anywhere.
        StartAgain(delivery, link.SessionId, NewBehaviorOn(booking) ? link.Token : null, now);
        await db.SaveChangesAsync(ct);

        await AttemptAsync(delivery, booking, link.Token, link.ExpiresAt, null, now, ct);
        return ViewOf(delivery, link.ExpiresAt, link: null);
    }

    public async Task RecordEmailQueuedAsync(Guid bookingId, IssuedCheckInLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var now = Now();
        var booking = await LoadBookingAsync(bookingId, null, ct) ?? throw BookingNotFound();

        var delivery = await db.CheckInLinkDeliveries
            .FirstOrDefaultAsync(d => d.OrgId == booking.OrgId && d.BookingId == bookingId, ct);
        if (delivery is null)
        {
            // DbSet.Add: the row carries its own Guid, so only Add tracks it as new.
            delivery = new CheckInLinkDelivery { OrgId = booking.OrgId, BookingId = booking.Id, CreatedAt = now };
            db.CheckInLinkDeliveries.Add(delivery);
        }

        // The row starts again for this link and waits for the provider: «in attesa» (Sending), by e-mail, with no token (the link is in the e-mail, nobody has to carry it). It is written
        // BEFORE the e-mail is queued, so that the job that sends it can never finish before the row exists. Never «sent» here: RecordEmailSentAsync says so, once the provider accepted it.
        StartAgain(delivery, link.SessionId, null, now);
        Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.Sending, null);
        delivery.LastAttemptAt = now;

        var session = await FindSessionAsync(bookingId, link.SessionId, ct);
        if (session is not null)
        {
            session.LinkEmailStatus = GuestCheckInLinkEmailStatus.Queued;
            session.LinkEmailError = null;
            session.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Check-in link of booking {BookingId} (org {OrgId}): {State} by {Channel}, inside the confirmation of the booking",
            booking.Id,
            booking.OrgId,
            delivery.State,
            delivery.Channel);
    }

    public async Task RecordEmailSentAsync(Guid bookingId, Guid sessionId, CancellationToken ct = default)
    {
        var now = Now();
        var session = await FindSessionAsync(bookingId, sessionId, ct);
        if (session is null)
            return;

        // The session of the link says what the e-mail job of the link writes when the provider accepts the e-mail: handed over, and when.
        session.LinkEmailStatus = GuestCheckInLinkEmailStatus.Sent;
        session.LinkEmailError = null;
        session.SentAt = now;
        session.UpdatedAt = now;

        // The row, only while it is still the one of this link: a later link started it again for itself.
        var delivery = await OpenRowOfLinkAsync(bookingId, sessionId, ct);
        if (delivery is not null)
        {
            MarkSent(delivery, CheckInLinkChannel.Email, null, now);
            delivery.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Check-in link of booking {BookingId} (org {OrgId}): handed to the provider inside the confirmation of the booking", bookingId, session.OrgId);

        if (delivery is null)
            return;

        try
        {
            // The link is with the guest: a «To do» to carry it, if the booking had one, is over (the same as any other way that ends in «sent»).
            await CloseTodoAsync(session.OrgId, bookingId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The To do of the check-in link of booking {BookingId} (org {OrgId}) could not be closed", bookingId, session.OrgId);
        }
    }

    public async Task RecordEmailFailedAsync(Guid bookingId, Guid sessionId, string errorCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        var now = Now();
        var session = await FindSessionAsync(bookingId, sessionId, ct);

        // A link the guest already opened (or completed) reached the guest, whatever the provider answered: nothing failed, nothing to take back.
        if (session is null || session.Status != GuestCheckInSessionStatus.Inviato)
            return;

        // Taken back: nobody has this link (only its hash is stored), so it expires and the daily job issues and sends a new one at its next run.
        session.LinkEmailStatus = GuestCheckInLinkEmailStatus.Failed;
        session.LinkEmailError = errorCode;
        session.Status = GuestCheckInSessionStatus.Scaduto;
        session.UpdatedAt = now;

        var delivery = await OpenRowOfLinkAsync(bookingId, sessionId, ct);
        if (delivery is not null)
        {
            Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, errorCode);
            delivery.LastAttemptAt = now;
            delivery.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Check-in link of booking {BookingId} (org {OrgId}): its confirmation e-mail did not leave ({Code}), the link is taken back",
            bookingId,
            session.OrgId,
            errorCode);
    }

    /// <summary>The check-in session <paramref name="sessionId"/>, only when it is a session of the booking (the tenant filter is off: the callers are jobs and the notifier, by id).</summary>
    private async Task<GuestCheckInSession?> FindSessionAsync(Guid bookingId, Guid sessionId, CancellationToken ct) =>
        await db.GuestCheckInSessions
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.BookingId == bookingId, ct);

    /// <summary>The row of the booking, when it has not ended and still stands for the link <paramref name="sessionId"/>.</summary>
    private async Task<CheckInLinkDelivery?> OpenRowOfLinkAsync(Guid bookingId, Guid sessionId, CancellationToken ct) =>
        await db.CheckInLinkDeliveries
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(d => d.BookingId == bookingId && d.SessionId == sessionId && d.EndedAt == null, ct);

    public async Task<int> RetryWaitingAsync(Func<Guid, Guid, Task<IssuedCheckInLink>> issueLink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issueLink);
        if (!ChatOn && !ContactOn)
        {
            logger.LogInformation("Check-in link delivery: both flags are off, nothing done");
            return 0;
        }

        if (!publicSiteLinks.IsConfigured)
        {
            logger.LogError("Check-in link delivery not tried again: App:PublicSiteBaseUrl is missing or invalid");
            return 0;
        }

        var now = Now();
        var ids = await RowsToLookAtAsync(now, ct);

        var tried = 0;
        ExceptionDispatchInfo? failure = null;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            // One row at a time: nothing of the previous row stays in the change tracker.
            db.ChangeTracker.Clear();
            try
            {
                if (await RetryOneAsync(id, issueLink, now, ct))
                    tried++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Check-in link delivery {DeliveryId} could not be tried again", id);
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        logger.LogInformation("Check-in link delivery: {Rows} rows looked at, {Tried} tried again", ids.Count, tried);
        failure?.Throw();
        return tried;
    }

    public async Task<CheckInLinkDeliveryView?> GetAsync(Guid orgId, Guid bookingId, bool includeLink, CancellationToken ct = default)
    {
        var delivery = await db.CheckInLinkDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.BookingId == bookingId, ct);
        if (delivery is null)
            return null;

        DateTime? expiresAt = null;
        string? link = null;
        if (delivery.SessionId is { } sessionId)
        {
            var session = await db.GuestCheckInSessions
                .AsNoTracking()
                .Where(s => s.Id == sessionId && s.OrgId == orgId)
                .Select(s => new { s.Status, s.ExpiresAt })
                .FirstOrDefaultAsync(ct);
            expiresAt = session?.ExpiresAt;

            // The link only to a caller who can already copy one, and only while it can be copied and used: the row waits or failed, it did not end, its token is there and
            // the link is still open.
            if (includeLink
                && delivery.EndedAt is null
                && CheckInLinkDeliveryRules.NeedsTheHost(delivery.State)
                && !string.IsNullOrEmpty(delivery.LinkToken)
                && session is { Status: GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione }
                && session.ExpiresAt >= Now()
                && publicSiteLinks.IsConfigured)
            {
                link = publicSiteLinks.GuestCheckIn(delivery.LinkToken);
            }
        }

        var view = ViewOf(delivery, expiresAt, link);

        // E23/F02/US01/T01: a row that went by e-mail describes the link it was written for. The reminder of the missing data, a link the guest asks for, the compliance autopilot and the
        // daily job after an expiry issue other links without touching it, so the row is read against the newest link of the booking and never says «sent» for a link that is over.
        return delivery.Channel == CheckInLinkChannel.Email && delivery.EndedAt is null
            ? await FollowNewestLinkAsync(delivery, view, ct)
            : view;
    }

    /// <summary>
    /// The view of an e-mail row that has not ended, read against the newest check-in link of its booking. Its own link is the newest: the row is the truth while the link is open, a failure
    /// stays what the host sees (the link was taken back), and a delivery that was in flight or sent for a link that is over is over. A later link exists: the state of its e-mail is where the
    /// link of the booking stands (<c>Sent</c>, <c>Sending</c> while queued, <c>Failed</c> with its code; a link nobody e-mailed is the host's to carry), and it is over when that link is.
    /// </summary>
    private async Task<CheckInLinkDeliveryView> FollowNewestLinkAsync(CheckInLinkDelivery delivery, CheckInLinkDeliveryView view, CancellationToken ct)
    {
        var newest = await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.BookingId == delivery.BookingId && s.OrgId == delivery.OrgId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Status, s.ExpiresAt, s.SentAt, s.LinkEmailStatus, s.LinkEmailError, s.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (newest is null)
            return view;

        var openStatus = newest.Status is GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione;
        var open = openStatus && newest.ExpiresAt >= Now();
        var completed = newest.Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato;
        var endedAt = openStatus ? newest.ExpiresAt : newest.UpdatedAt;
        var endedReason = completed ? CheckInLinkEndReasons.CheckInCompleted : CheckInLinkEndReasons.LinkExpired;

        if (newest.Id == delivery.SessionId)
        {
            return open || delivery.State == CheckInLinkDeliveryState.Failed
                ? view
                : view with
                {
                    EndedAt = endedAt,
                    EndedReason = endedReason,
                };
        }

        var emailed = newest.LinkEmailStatus is GuestCheckInLinkEmailStatus.Sent or GuestCheckInLinkEmailStatus.Queued or GuestCheckInLinkEmailStatus.Failed;
        var state = newest.LinkEmailStatus switch
        {
            GuestCheckInLinkEmailStatus.Sent => CheckInLinkDeliveryState.Sent,
            GuestCheckInLinkEmailStatus.Queued => CheckInLinkDeliveryState.Sending,
            GuestCheckInLinkEmailStatus.Failed => CheckInLinkDeliveryState.Failed,
            _ => CheckInLinkDeliveryState.WaitingContact,
        };
        return new CheckInLinkDeliveryView(
            emailed ? CheckInLinkChannel.Email : CheckInLinkChannel.Manual,
            state,
            0,
            newest.LinkEmailStatus == GuestCheckInLinkEmailStatus.Failed ? newest.LinkEmailError : null,
            newest.SentAt,
            open ? null : endedAt,
            open ? null : endedReason,
            newest.ExpiresAt,
            null);
    }

    public async Task EndAsync(Guid orgId, Guid bookingId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var delivery = await db.CheckInLinkDeliveries
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.BookingId == bookingId, ct);
        if (delivery is null || delivery.EndedAt is not null)
            return;

        await EndRowAsync(delivery, reason, Now(), ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // One attempt
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Tries the ways to the guest for the link <paramref name="token"/> and leaves the row in the state it ends in (saved), then keeps the «To do» and the notice of the
    /// host in step. <paramref name="sessionLanguage"/> is the language the guest chose on the check-in page, when it did.
    /// </summary>
    private async Task AttemptAsync(
        CheckInLinkDelivery delivery,
        Booking booking,
        string token,
        DateTime expiresAt,
        string? sessionLanguage,
        DateTime now,
        CancellationToken ct)
    {
        delivery.LastAttemptAt = now;

        // An address the booking has (the host wrote it, or an earlier read gave it): the e-mail, as for any booking.
        if (HasEmail(booking))
        {
            await SendByEmailAsync(delivery, token, now, ct);
            await SaveAndTellHostAsync(delivery, booking, now, ct);
            return;
        }

        var chatOn = ChatOn;
        var contactOn = ContactOn;
        if (!OtaChannelActionPolicy.IsChannelBooking(booking) || (!chatOn && !contactOn))
        {
            // No way to the guest that CasaZen may use: the host carries the link. The row says why.
            Settle(delivery, CheckInLinkChannel.Manual, CheckInLinkDeliveryState.WaitingContact, null);
            await SaveAndTellHostAsync(delivery, booking, now, ct);
            return;
        }

        ChatAttempt? chat = null;
        if (chatOn)
        {
            chat = await TryChatAsync(booking, token, expiresAt, sessionLanguage, ct);
            if (chat.Delivered)
            {
                MarkSent(delivery, CheckInLinkChannel.PortalChat, chat.MessageId, now);
                await SaveAndTellHostAsync(delivery, booking, now, ct);
                return;
            }

            if (chat.State == CheckInLinkDeliveryState.Sending)
            {
                // The message is with the channel manager and has not been sent yet: the link is not delivered. The row stays open with its token (an error, or the host, may need it),
                // the contact of the guest is not asked for (the chat has the link), and the hourly pass reads the message again.
                Settle(delivery, CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Sending, null);
                delivery.OtaMessageId = chat.MessageId;
                await SaveAndTellHostAsync(delivery, booking, now, ct);
                return;
            }

            // Only a failure worth a retry (a limit, an outage) counts toward the limit of retries: waiting for the permission, the conversation or a way to the guest is not an attempt that failed.
            if (chat.State == CheckInLinkDeliveryState.Failed && IsTransient(chat.ErrorCode))
                delivery.Attempts++;
        }

        string? contactError = null;
        if (contactOn && ContactMayBeRead(delivery, now))
        {
            try
            {
                var read = await contacts.ReadForCheckInLinkAsync(delivery.OrgId, booking.Id, ct);
                delivery.ContactReadAt = now;
                delivery.ContactRefused = read.Status == GuestContactInfoStatus.StaticIpsRequired;
                if (read.Status == GuestContactInfoStatus.EmailFilled)
                {
                    await SendByEmailAsync(delivery, token, now, ct);
                    await SaveAndTellHostAsync(delivery, booking, now, ct);
                    return;
                }
            }
            catch (Exception ex) when (ex is OctorateException or DomainException)
            {
                // Not an answer about the contact (a limit, an outage): the read did not happen, it is asked again at the next pass, and the code is on the row.
                contactError = CodeOf(ex);
                logger.LogWarning(
                    "The contact of the guest of booking {BookingId} (org {OrgId}) could not be read for the check-in link ({Code})", booking.Id, delivery.OrgId, contactError);
            }
        }

        if (chat is not null)
        {
            // The chat is on: its state is the one the host sees (it resolves by itself: the sync reads the chat, a thread arrives).
            Settle(delivery, CheckInLinkChannel.PortalChat, chat.State, chat.ErrorCode);
        }
        else if (delivery.ContactRefused)
        {
            Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.WaitingStaticIps, OctorateSensitiveDataCode);
        }
        else if (contactError is not null)
        {
            if (IsTransient(contactError))
                delivery.Attempts++;

            Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, contactError);
        }
        else
        {
            // The contact was read and gave no address (or it cannot be read now): no way to the guest, the host carries the link.
            Settle(delivery, CheckInLinkChannel.Manual, CheckInLinkDeliveryState.WaitingContact, null);
        }

        await SaveAndTellHostAsync(delivery, booking, now, ct);
    }

    /// <summary>The code of a refusal of the contact (the same the exception carries; a test keeps them equal).</summary>
    internal const string OctorateSensitiveDataCode = "octorate_sensitive_data_not_allowed";

    /// <summary>The one message of the chat: the thread of the booking, the text in the language of the guest, one send for one key.</summary>
    private async Task<ChatAttempt> TryChatAsync(Booking booking, string token, DateTime expiresAt, string? sessionLanguage, CancellationToken ct)
    {
        if (await targets.AccommodationOfAsync(booking.PropertyId, ct) is not { } accommodation)
            return ChatAttempt.Failed(CheckInLinkDeliveryRules.NotMappedCode);

        // The chat of the listing is not in its license (the last pass of the copy of the conversations said so): there is no thread to find and a send would be
        // refused, so no call is made and the link waits for the permission.
        var notLicensed = await db.OtaMessageSyncStates
            .AsNoTracking()
            .AnyAsync(
                s => s.OrgId == booking.OrgId
                     && s.OctorateAccommodationId == accommodation
                     && s.LastError == OtaMessageSyncErrorCodes.ChatNotLicensed,
                ct);
        if (notLicensed)
            return ChatAttempt.Waiting(CheckInLinkDeliveryState.WaitingChatPermission, OtaMessageSyncErrorCodes.ChatNotLicensed);

        var threadId = await ReservationThreadAsync(booking, ct);
        if (threadId is null)
            return ChatAttempt.Waiting(CheckInLinkDeliveryState.WaitingThread, MessageContactOptions.NoConversationReason);

        var key = await KeyOfAsync(booking, threadId.Value, token, ct);
        var text = ChatText(booking, token, expiresAt, sessionLanguage);

        MessageReplyResult result;
        try
        {
            result = await replies.ReplyAsync(
                new HostScope(booking.OrgId),
                MessageThreadKey.ForOta(threadId.Value),
                text,
                key,
                null,
                MessageOrigin.Automation,
                null,
                ct);
        }
        catch (OctorateAuthException ex) when (ex.IsChatNotLicensed)
        {
            return ChatAttempt.Waiting(CheckInLinkDeliveryState.WaitingChatPermission, ex.Code);
        }
        catch (NotFoundException)
        {
            // The conversation went between the read and the send (a new mapping moved it, the guest was erased): there is none now.
            return ChatAttempt.Waiting(CheckInLinkDeliveryState.WaitingThread, MessageContactOptions.NoConversationReason);
        }
        catch (Exception ex) when (ex is OctorateException or DomainException)
        {
            // A refusal before or around the send: the connector is not connected, the listing is not mapped, the thread is closed, a limit, an outage. The code is the state.
            var code = CodeOf(ex);
            logger.LogWarning("The check-in link of booking {BookingId} (org {OrgId}) did not go to the chat ({Code})", booking.Id, booking.OrgId, code);
            return ChatAttempt.Failed(code);
        }

        switch (result.State)
        {
            case OtaMessageDeliveryState.Sent:
                return ChatAttempt.Done(result.Message?.Id);
            case OtaMessageDeliveryState.Created or OtaMessageDeliveryState.WaitingAuthorization:
                // With the channel manager, which has not sent it yet (or waits for the authorization of the channel): the link is not delivered, and the message may still end in
                // error. The row stays open; the hourly pass reads the message again.
                return ChatAttempt.OnItsWay(result.Message?.Id);
            case OtaMessageDeliveryState.ManualVerify:
                // It may have left: never sent again, and never shown as sent.
                return ChatAttempt.Failed(CheckInLinkDeliveryRules.ManualVerifyCode);
            default:
                return result.ErrorCode == OtaMessageSyncErrorCodes.ChatNotLicensed
                    ? ChatAttempt.Waiting(CheckInLinkDeliveryState.WaitingChatPermission, OtaMessageSyncErrorCodes.ChatNotLicensed)
                    : ChatAttempt.Failed(result.ErrorCode ?? MessageReplyService.ChannelReportedErrorCode);
        }
    }

    /// <summary>The reservation conversation of the booking, at the property of the booking (a conversation moved to another house by a new mapping is not its own).</summary>
    private async Task<Guid?> ReservationThreadAsync(Booking booking, CancellationToken ct) =>
        await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == booking.OrgId
                        && t.BookingId == booking.Id
                        && t.PropertyId == booking.PropertyId
                        && t.Kind == OtaThreadKind.Reservation
                        && t.AnonymizedAt == null)
            .OrderByDescending(t => t.LastMessageAt)
            .ThenBy(t => t.Id)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The key of the next send of the link: the booking and the token, and for each message of the same link that the channel manager refused (the state
    /// <see cref="OtaMessageDeliveryState.Error"/>: nothing left, so the key is spent) the next of <c>-1</c>, <c>-2</c>, ... A key whose message is in any other state is
    /// the one to use again: the send finds that message and sends nothing.
    /// </summary>
    private async Task<string> KeyOfAsync(Booking booking, Guid threadId, string token, CancellationToken ct)
    {
        var baseKey = CheckInLinkDeliveryRules.IdempotencyKey(booking.Id, token);
        var written = await db.OtaMessages
            .AsNoTracking()
            .Where(m => m.OrgId == booking.OrgId
                        && m.ThreadId == threadId
                        && m.IdempotencyKey != null
                        && m.IdempotencyKey.StartsWith(baseKey))
            .Select(m => new { m.IdempotencyKey, m.DeliveryState })
            .ToListAsync(ct);

        var key = baseKey;
        for (var round = 0; round <= CheckInLinkDeliveryRules.MaxAttempts * 4; round++)
        {
            key = round == 0 ? baseKey : $"{baseKey}-{round}";
            var message = written.FirstOrDefault(m => m.IdempotencyKey == key);
            if (message is null || message.DeliveryState != OtaMessageDeliveryState.Error)
                return key;
        }

        return key;
    }

    /// <summary>
    /// The text of the message: the language the guest chose on the check-in page, else the one of the country on the booking, else Italian followed by English in the
    /// same message (a stay from a portal comes with no country, and the guest's language is not known). Plain text, short, no name of a portal or of a supplier.
    /// </summary>
    private string ChatText(Booking booking, string token, DateTime expiresAt, string? sessionLanguage)
    {
        var url = publicSiteLinks.GuestCheckIn(token);
        var language = GuestLanguages.Normalize(sessionLanguage) ?? GuestLanguages.OfCountry(booking.Guest?.Country);

        string Compose(string code) =>
            EmailTemplates.GuestCheckInChatMessage(GuestLanguages.CultureOf(code), booking.Property.Name, booking.CheckInDate, url, expiresAt);

        return language is { } known ? Compose(known) : Compose("it") + "\n\n" + Compose("en");
    }

    /// <summary>The e-mail of the link to the address the booking has: handed to the queue of the e-mails, whose job records its outcome on the check-in session.</summary>
    private async Task SendByEmailAsync(CheckInLinkDelivery delivery, string token, DateTime now, CancellationToken ct)
    {
        if (delivery.SessionId is not { } sessionId)
        {
            Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, LinkNotUsableCode);
            return;
        }

        GuestCheckInLinkEmailOutcome outcome;
        try
        {
            outcome = await linkEmails.QueueAsync(sessionId, token, ct);
        }
        catch (NotFoundException)
        {
            Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, LinkNotUsableCode);
            return;
        }

        if (outcome.Status == GuestCheckInLinkEmailStatus.Queued)
            MarkSent(delivery, CheckInLinkChannel.Email, null, now);
        else
            Settle(delivery, CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, outcome.Error ?? EmailNotQueuedCode);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The hourly pass
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The rows one pass looks at: at most <see cref="RetryBatchSize"/>, the oldest attempt first, <b>chosen in the query among the ones that are due or over</b>. A pass takes a bounded
    /// number of rows; the rows that cannot change (a wait for a permission still missing, for a conversation that is not there, for a day that has not passed) stay where they are and
    /// never change their place, so taking the oldest of all of them would leave the ones that are due behind them for as long as more rows than a pass takes are open.
    /// </summary>
    /// <remarks>
    /// The query is a superset of the exact rules (<see cref="IsDueAsync"/>, <see cref="EndReasonAsync"/>, <see cref="SettleSendingAsync"/>), which are still applied to each row it gives: it never
    /// leaves out a row that is due or over, it leaves out the ones that cannot be. Over: the link of the row is not open any more, the guest completed the check-in, or the booking takes
    /// none. Due: a stay from a portal (the booking has a reservation of the channel manager; the exact rule is <see cref="OtaChannelActionPolicy.IsChannelBooking(Booking)"/>) whose
    /// message is on its way, whose chat waits for the permission (the copy read the listing of the property without error since the last attempt, and, for a wait that came from a refused
    /// send, a day has passed) or for the conversation (a conversation of the booking exists), whose contact may be read again, whose link waited its hour with no way to the guest, or whose
    /// failure is worth a retry.
    /// </remarks>
    private async Task<List<Guid>> RowsToLookAtAsync(DateTime now, CancellationToken ct)
    {
        var hourAgo = now - CheckInLinkDeliveryRules.RetryInterval;
        var contactAgo = now - CheckInLinkDeliveryRules.ContactRetryInterval;
        var refusedSendAgo = now - CheckInLinkDeliveryRules.RefusedSendRetryInterval;
        var chatOn = ChatOn;
        var contactOn = ContactOn;
        var maxAttempts = CheckInLinkDeliveryRules.MaxAttempts;
        string[] transient = TransientCodes;
        BookingStatus[] active = AlloggiatiStatusRules.ActiveBookingStatuses;
        GuestCheckInSessionStatus[] open = [GuestCheckInSessionStatus.Inviato, GuestCheckInSessionStatus.InCompilazione];
        GuestCheckInSessionStatus[] completed = [GuestCheckInSessionStatus.Completo, GuestCheckInSessionStatus.AlloggiatiInviato];

        return await db.CheckInLinkDeliveries
            .AsNoTracking()
            .Where(d => d.EndedAt == null && d.State != CheckInLinkDeliveryState.Sent)
            .Where(d =>
                !db.GuestCheckInSessions.Any(s => s.Id == d.SessionId && open.Contains(s.Status) && s.ExpiresAt >= now)
                || db.GuestCheckInSessions.Any(s => s.BookingId == d.BookingId && s.OrgId == d.OrgId && completed.Contains(s.Status))
                || !db.Bookings.Any(b => b.Id == d.BookingId && active.Contains(b.Status) && b.Status != BookingStatus.CheckedOut && b.NoShowAt == null)
                || (db.Bookings.Any(b => b.Id == d.BookingId && b.OctorateReservationId != null && b.OctorateReservationId != "")
                    && (d.State == CheckInLinkDeliveryState.Sending
                        || (d.State == CheckInLinkDeliveryState.WaitingContact
                            && (d.LastAttemptAt == null || d.LastAttemptAt <= hourAgo)
                            && (chatOn || (contactOn && (d.ContactReadAt == null || (d.ContactRefused && d.ContactReadAt <= contactAgo)))))
                        || (d.State == CheckInLinkDeliveryState.WaitingStaticIps
                            && contactOn
                            && (d.ContactReadAt == null || (d.ContactRefused && d.ContactReadAt <= contactAgo)))
                        || (d.State == CheckInLinkDeliveryState.Failed
                            && d.LastErrorCode != null
                            && transient.Contains(d.LastErrorCode)
                            && d.Attempts < maxAttempts
                            && (d.LastAttemptAt == null || d.LastAttemptAt <= hourAgo))
                        || (d.State == CheckInLinkDeliveryState.WaitingThread
                            && chatOn
                            && db.OtaMessageThreads.Any(t => t.OrgId == d.OrgId && t.BookingId == d.BookingId && t.Kind == OtaThreadKind.Reservation && t.AnonymizedAt == null))
                        || (d.State == CheckInLinkDeliveryState.WaitingChatPermission
                            && chatOn
                            && db.OtaMessageSyncStates.Any(s => s.OrgId == d.OrgId
                                                                && s.LastError == null
                                                                && (d.LastAttemptAt == null || s.UpdatedAt > d.LastAttemptAt)
                                                                && db.Bookings.Any(b => b.Id == d.BookingId && b.PropertyId == s.PropertyId))
                            && (d.LastAttemptAt == null
                                || d.LastAttemptAt <= refusedSendAgo
                                || !db.OtaMessages.Any(m => m.OrgId == d.OrgId
                                                            && m.Origin == MessageOrigin.Automation
                                                            && m.DeliveryState == OtaMessageDeliveryState.Error
                                                            && db.OtaMessageThreads.Any(t => t.Id == m.ThreadId && t.BookingId == d.BookingId)))))))
            .OrderBy(d => d.LastAttemptAt ?? d.CreatedAt)
            .Select(d => d.Id)
            .Take(RetryBatchSize)
            .ToListAsync(ct);
    }

    /// <summary>One row of the hourly pass: ended, left alone, or tried again. True when it was tried again.</summary>
    private async Task<bool> RetryOneAsync(Guid id, Func<Guid, Guid, Task<IssuedCheckInLink>> issueLink, DateTime now, CancellationToken ct)
    {
        var delivery = await db.CheckInLinkDeliveries.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null || delivery.EndedAt is not null || delivery.State == CheckInLinkDeliveryState.Sent)
            return false;

        var booking = await LoadBookingAsync(delivery.BookingId, delivery.OrgId, ct);
        if (booking is null)
        {
            await EndRowAsync(delivery, CheckInLinkEndReasons.BookingNotActive, now, ct);
            return false;
        }

        if (await EndReasonAsync(delivery, booking, now, ct) is { } reason)
        {
            await EndRowAsync(delivery, reason, now, ct);
            return false;
        }

        // A message the channel manager had not sent yet is read again, never sent again.
        if (delivery.State == CheckInLinkDeliveryState.Sending)
            return await SettleSendingAsync(delivery, booking, now, ct);

        if (!await IsDueAsync(delivery, booking, now, ct))
            return false;

        var session = await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.Id == delivery.SessionId)
            .Select(s => new { s.ExpiresAt, s.Language })
            .FirstAsync(ct);
        var token = delivery.LinkToken;
        var expiresAt = session.ExpiresAt;
        var language = session.Language;
        if (string.IsNullOrEmpty(token))
        {
            // A row made while the flags were off holds no token, and nobody has its link (only its hash exists): a new link replaces it.
            var link = await issueLink(booking.Id, booking.OrgId);
            StartAgain(delivery, link.SessionId, link.Token, now);
            token = link.Token;
            expiresAt = link.ExpiresAt;
            language = null;
        }

        await AttemptAsync(delivery, booking, token, expiresAt, language, now, ct);
        return true;
    }

    /// <summary>
    /// A row whose message the channel manager had not sent yet (<see cref="CheckInLinkDeliveryState.Sending"/>): the message is read again from the copy of the conversation (the sync stores
    /// the state the channel manager reached; nothing is asked of it here). <b>Sent</b>: the link is delivered (the token is cleared and the «To do» closed). <b>Error</b>: the link did not reach
    /// the guest, so the row fails with the code of a message the channel manager reports as failed, the host gets its «To do» and the notice, and the token is still there for «Copia link».
    /// Any other state (an outcome nobody knows, a message that is gone) fails the same way with the code of an outcome nobody knows. Still on its way: nothing changes. True when the row changed.
    /// </summary>
    private async Task<bool> SettleSendingAsync(CheckInLinkDelivery delivery, Booking booking, DateTime now, CancellationToken ct)
    {
        var messageId = delivery.OtaMessageId;
        OtaMessageDeliveryState? state = messageId is { } id
            ? await db.OtaMessages
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(m => m.Id == id && m.OrgId == delivery.OrgId)
                .Select(m => (OtaMessageDeliveryState?)m.DeliveryState)
                .FirstOrDefaultAsync(ct)
            : null;

        switch (state)
        {
            case OtaMessageDeliveryState.Created or OtaMessageDeliveryState.WaitingAuthorization:
                return false;
            case OtaMessageDeliveryState.Sent:
                MarkSent(delivery, CheckInLinkChannel.PortalChat, messageId, now);
                break;
            case OtaMessageDeliveryState.Error:
                Settle(delivery, CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Failed, MessageReplyService.ChannelReportedErrorCode);
                break;
            default:
                Settle(delivery, CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Failed, CheckInLinkDeliveryRules.ManualVerifyCode);
                break;
        }

        await SaveAndTellHostAsync(delivery, booking, now, ct);
        return true;
    }

    /// <summary>
    /// Whether the thing a row waits for may have changed: the chat of the listing was read without error since the last attempt, the booking has its conversation now,
    /// the contact may be asked again (a day after a refusal), the flags were turned on after the row was made, or a failure worth a retry has waited its hour.
    /// </summary>
    private async Task<bool> IsDueAsync(CheckInLinkDelivery delivery, Booking booking, DateTime now, CancellationToken ct)
    {
        var sinceLastAttempt = delivery.LastAttemptAt is { } last ? now - last : TimeSpan.MaxValue;
        var portal = OtaChannelActionPolicy.IsChannelBooking(booking);
        if (!portal)
            return false;

        switch (delivery.State)
        {
            case CheckInLinkDeliveryState.WaitingChatPermission:
                if (!ChatOn)
                    return false;
                var accommodation = await targets.AccommodationOfAsync(booking.PropertyId, ct);
                var state = accommodation is null
                    ? null
                    : await db.OtaMessageSyncStates
                        .AsNoTracking()
                        .Where(s => s.OrgId == booking.OrgId && s.OctorateAccommodationId == accommodation)
                        .Select(s => new { s.LastError, s.UpdatedAt })
                        .FirstOrDefaultAsync(ct);

                // Recovered: the copy of the conversations read the listing without error since the last attempt.
                var recovered = state is { LastError: null } && (delivery.LastAttemptAt is not { } at || state.UpdatedAt > at);
                if (!recovered)
                    return false;

                // A wait that came from the send itself (the channel manager refused an automatic message of the booking) is not one the copy can lift: it reads the listing fine and moves
                // its state at every pass, so a pass proves nothing. The send is tried again at most once a day, never one refused message an hour in the thread of the guest.
                return sinceLastAttempt >= CheckInLinkDeliveryRules.RefusedSendRetryInterval || !await SendWasRefusedAsync(booking, ct);

            case CheckInLinkDeliveryState.WaitingThread:
                return ChatOn && await ReservationThreadAsync(booking, ct) is not null;

            case CheckInLinkDeliveryState.WaitingStaticIps:
                return ContactOn && ContactMayBeRead(delivery, now);

            case CheckInLinkDeliveryState.WaitingContact:
                // Made while the flags were off (or by a read that gave no address): due only when a way is on that was not tried, and not more than once an hour.
                return sinceLastAttempt >= CheckInLinkDeliveryRules.RetryInterval
                       && (ChatOn || (ContactOn && ContactMayBeRead(delivery, now)));

            case CheckInLinkDeliveryState.Failed:
                return IsTransient(delivery.LastErrorCode)
                       && delivery.Attempts < CheckInLinkDeliveryRules.MaxAttempts
                       && sinceLastAttempt >= CheckInLinkDeliveryRules.RetryInterval;

            default:
                return false;
        }
    }

    /// <summary>
    /// The channel manager refused an automatic message written in a conversation of the booking (the state <see cref="OtaMessageDeliveryState.Error"/>): the permission the link waits for is
    /// the one the send was refused for, which the copy of the conversations does not read.
    /// </summary>
    private async Task<bool> SendWasRefusedAsync(Booking booking, CancellationToken ct)
    {
        return await db.OtaMessages
            .AsNoTracking()
            .AnyAsync(
                m => m.OrgId == booking.OrgId
                     && m.Origin == MessageOrigin.Automation
                     && m.DeliveryState == OtaMessageDeliveryState.Error
                     && db.OtaMessageThreads.Any(t => t.Id == m.ThreadId && t.OrgId == booking.OrgId && t.BookingId == booking.Id),
                ct);
    }

    /// <summary>
    /// Why the row is over, or <c>null</c> while it is not: the booking is no longer one that takes a check-in, the guest completed it, or the link expired or was
    /// replaced (by another link of the booking: the host issued one).
    /// </summary>
    private async Task<string?> EndReasonAsync(CheckInLinkDelivery delivery, Booking booking, DateTime now, CancellationToken ct)
    {
        // Active is the one definition (confirmed, checked in or checked out, not a no-show); a stay that is over takes no check-in any more.
        if (!AlloggiatiStatusRules.IsDue(booking) || booking.Status == BookingStatus.CheckedOut)
            return CheckInLinkEndReasons.BookingNotActive;

        var sessions = await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.BookingId == booking.Id && s.OrgId == booking.OrgId)
            .Select(s => new { s.Id, s.Status, s.ExpiresAt })
            .ToListAsync(ct);
        if (sessions.Any(s => s.Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato))
            return CheckInLinkEndReasons.CheckInCompleted;

        bool Open(GuestCheckInSessionStatus status, DateTime expires) =>
            (status is GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione) && expires >= now;

        var own = sessions.FirstOrDefault(s => s.Id == delivery.SessionId);
        if (own is not null && Open(own.Status, own.ExpiresAt))
            return null;

        return sessions.Any(s => s.Id != delivery.SessionId && Open(s.Status, s.ExpiresAt))
            ? CheckInLinkEndReasons.LinkReplaced
            : CheckInLinkEndReasons.LinkExpired;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The row and the host
    // ------------------------------------------------------------------------------------------------------------------------------------

    private static void StartAgain(CheckInLinkDelivery delivery, Guid sessionId, string? token, DateTime now)
    {
        delivery.SessionId = sessionId;
        delivery.Channel = CheckInLinkChannel.Manual;
        delivery.State = CheckInLinkDeliveryState.WaitingContact;
        delivery.Attempts = 0;
        delivery.LastErrorCode = null;
        delivery.SentAt = null;
        delivery.OtaMessageId = null;
        delivery.LinkToken = token;
        delivery.LastAttemptAt = null;
        delivery.EndedAt = null;
        delivery.EndedReason = null;
        delivery.UpdatedAt = now;
    }

    private static void Settle(CheckInLinkDelivery delivery, CheckInLinkChannel channel, CheckInLinkDeliveryState state, string? errorCode)
    {
        delivery.Channel = channel;
        delivery.State = state;
        delivery.LastErrorCode = errorCode;
    }

    private static void MarkSent(CheckInLinkDelivery delivery, CheckInLinkChannel channel, Guid? messageId, DateTime now)
    {
        delivery.Channel = channel;
        delivery.State = CheckInLinkDeliveryState.Sent;
        delivery.LastErrorCode = null;
        delivery.SentAt = now;
        delivery.OtaMessageId = messageId;

        // Delivered: nobody needs the raw token any more.
        delivery.LinkToken = null;
    }

    private async Task EndRowAsync(CheckInLinkDelivery delivery, string reason, DateTime now, CancellationToken ct)
    {
        delivery.EndedAt = now;
        delivery.EndedReason = reason;
        delivery.LinkToken = null;
        delivery.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        try
        {
            await CloseTodoAsync(delivery.OrgId, delivery.BookingId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The To do of the check-in link of booking {BookingId} (org {OrgId}) could not be closed", delivery.BookingId, delivery.OrgId);
        }

        logger.LogInformation(
            "Check-in link delivery of booking {BookingId} (org {OrgId}) ended: {Reason}", delivery.BookingId, delivery.OrgId, reason);
    }

    /// <summary>Saves the row and keeps the host's «To do» and notice in step with it. The notice is best effort: it never fails the delivery.</summary>
    private async Task SaveAndTellHostAsync(CheckInLinkDelivery delivery, Booking booking, DateTime now, CancellationToken ct)
    {
        delivery.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Check-in link of booking {BookingId} (org {OrgId}): {State} by {Channel}, attempt {Attempts}, code {Code}",
            booking.Id,
            delivery.OrgId,
            delivery.State,
            delivery.Channel,
            delivery.Attempts,
            delivery.LastErrorCode);

        try
        {
            if (delivery.State == CheckInLinkDeliveryState.Sent)
            {
                await CloseTodoAsync(delivery.OrgId, booking.Id, ct);
                return;
            }

            // Only while the new behavior is on for a stay from a portal: with both flags off nothing is added to what the host sees.
            if (!NewBehaviorOn(booking) || !CheckInLinkDeliveryRules.NeedsTheHost(delivery.State))
                return;

            await RaiseTodoAsync(delivery, booking, ct);
            if (delivery.State == CheckInLinkDeliveryState.Failed && IsFinalFailure(delivery))
                TellHosts(delivery, booking, await OrgCultureAsync(delivery.OrgId, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The host of booking {BookingId} (org {OrgId}) could not be told about its check-in link", booking.Id, delivery.OrgId);
        }
    }

    /// <summary>A failure that no retry will change: not worth a retry, or the retries are used.</summary>
    private static bool IsFinalFailure(CheckInLinkDelivery delivery) =>
        !IsTransient(delivery.LastErrorCode) || delivery.Attempts >= CheckInLinkDeliveryRules.MaxAttempts;

    /// <summary>The code is one of a failure worth a retry (a limit, an outage: <see cref="TransientCodes"/>).</summary>
    private static bool IsTransient(string? code) => code is not null && TransientCodes.Contains(code, StringComparer.Ordinal);

    /// <summary>The «To do» «Copia link»: once for the booking, however it ends (a host who closed it by hand does not get it back), due at the start of the check-in day.</summary>
    private async Task RaiseTodoAsync(CheckInLinkDelivery delivery, Booking booking, CancellationToken ct)
    {
        if (await BookingTodoItems.WasRaisedAsync(db, delivery.OrgId, ActivityType.CheckInLinkToSend, booking.Id, ct))
            return;

        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.CheckInLinkToSend,
                    Action = ActivityAction.CopyCheckInLink,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = booking.Id,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = booking.PropertyId,
                    OrgId = booking.OrgId,
                    Deadline = RomeCalendar.StartOfDayUtc(booking.CheckInDate),
                    Reason = await BookingTodoItems.ReasonAsync(db, delivery.OrgId, TodoReasonKey, ct),
                },
                ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nothing to carry the link for.
            logger.LogInformation("The property of booking {BookingId} (org {OrgId}) is gone: no To do for its check-in link", booking.Id, delivery.OrgId);
        }
    }

    /// <summary>Key of the reason of the «To do» in <c>EmailTexts</c> (Italian and English).</summary>
    internal const string TodoReasonKey = "TodoReason_CheckInLinkToSend";

    private async Task CloseTodoAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var open = await activities.FindOpenAsync(orgId, nameof(ActivityAction.CopyCheckInLink), InternalActivity.BookingEntityType, bookingId, ct);
        if (open is null)
            return;

        try
        {
            await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
        }
        catch (NotFoundException)
        {
            // Closed meanwhile.
        }
    }

    /// <summary>The bell, the pop-up and the live update of the hosts of the booking (the decorator of the push does the three): once for each link that failed for good.</summary>
    private void TellHosts(CheckInLinkDelivery delivery, Booking booking, System.Globalization.CultureInfo culture)
    {
        if (push is null || delivery.SessionId is not { } sessionId)
            return;

        var text = EmailTemplates.CheckInLinkFailedPush(culture, booking.Property.Name);
        push.Enqueue(
            PushDeliveryKeys.CheckInLinkFailed(sessionId),
            PushAudience.BookingHosts(booking.Id),
            new PushNotificationPayload(text.Title, text.Body, PushTypes.CheckInLinkFailed, booking.Id, PushRoutes.Booking(booking.Id)));
    }

    private async Task<System.Globalization.CultureInfo> OrgCultureAsync(Guid orgId, CancellationToken ct)
    {
        var language = await db.Orgs
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.DefaultLanguage)
            .FirstOrDefaultAsync(ct);
        return string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.CultureInfo.GetCultureInfo("en")
            : EmailTemplates.DefaultCulture;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // Small things
    // ------------------------------------------------------------------------------------------------------------------------------------

    private bool ChatOn => featureFlags.IsEnabled(FeatureFlags.OctorateConnector) && featureFlags.IsEnabled(FeatureFlags.PortalChatCheckInLink);

    private bool ContactOn => featureFlags.IsEnabled(FeatureFlags.OctorateConnector) && featureFlags.IsEnabled(FeatureFlags.GuestContactInfo);

    /// <summary>The new behavior is on for a booking: a stay from a portal, with at least one of the two ways on.</summary>
    private bool NewBehaviorOn(Booking booking) => OtaChannelActionPolicy.IsChannelBooking(booking) && (ChatOn || ContactOn);

    /// <summary>
    /// The contact of the guest is read once for the booking, and again only after a refusal, at most once a day (<see cref="CheckInLinkDeliveryRules.ContactRetryInterval"/>):
    /// a channel manager that said no is not asked in a loop, and one that answered is not asked twice.
    /// </summary>
    private static bool ContactMayBeRead(CheckInLinkDelivery delivery, DateTime now) =>
        delivery.ContactReadAt is not { } at
        || (delivery.ContactRefused && now - at >= CheckInLinkDeliveryRules.ContactRetryInterval);

    private static bool HasEmail(Booking booking) => !string.IsNullOrWhiteSpace(booking.Guest?.Email);

    private static string CodeOf(Exception ex) => ex switch
    {
        OctorateException octorate => octorate.Code,
        DomainException domain => domain.Code,
        _ => ex.GetType().Name,
    };

    /// <summary>The booking with its guest and its property, or <c>null</c> when it is not there (or its property was deleted: no check-in link is delivered for it).</summary>
    private async Task<Booking?> LoadBookingAsync(Guid bookingId, Guid? orgId, CancellationToken ct)
    {
        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .FirstOrDefaultAsync(b => b.Id == bookingId && (orgId == null || b.OrgId == orgId), ct);
        return booking?.Property is null ? null : booking;
    }

    private static CheckInLinkDeliveryView ViewOf(CheckInLinkDelivery delivery, DateTime? expiresAt, string? link) =>
        new(
            delivery.Channel,
            delivery.State,
            delivery.Attempts,
            delivery.LastErrorCode,
            delivery.SentAt,
            delivery.EndedAt,
            delivery.EndedReason,
            expiresAt,
            link);

    private static NotFoundException BookingNotFound() =>
        new("The booking was not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>What the chat of the portal answered for the link: delivered, on its way (with the channel manager, not sent yet), waiting for something, or failed with a code.</summary>
    private sealed record ChatAttempt(bool Delivered, CheckInLinkDeliveryState State, string? ErrorCode, Guid? MessageId)
    {
        public static ChatAttempt Done(Guid? messageId) => new(true, CheckInLinkDeliveryState.Sent, null, messageId);

        public static ChatAttempt OnItsWay(Guid? messageId) => new(false, CheckInLinkDeliveryState.Sending, null, messageId);

        public static ChatAttempt Waiting(CheckInLinkDeliveryState state, string code) => new(false, state, code, null);

        public static ChatAttempt Failed(string code) => new(false, CheckInLinkDeliveryState.Failed, code, null);
    }
}
