using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>Where a message the host is alerted about comes from: a conversation of a channel or the guest area of a direct booking.</summary>
public enum HostMessageSource
{
    /// <summary>A conversation copied from the channel manager (<see cref="OtaMessageThread"/>, key <c>ota:{guid}</c>).</summary>
    Ota = 1,

    /// <summary>The conversation of a direct booking (<see cref="GuestThread"/>, key <c>direct:{guid}</c>).</summary>
    Direct = 2,
}

/// <summary>
/// A message of a guest the hosts are alerted about (E05/F03, NF-65): ids and an instant, never a text, a name or an address. The delivery reads
/// what it needs (the property, the channel, the names the email of a direct booking has always carried) from the thread, in the org.
/// </summary>
/// <param name="OrgId">The org of the thread; every read is scoped to it.</param>
/// <param name="Source">Where the message comes from.</param>
/// <param name="EventType"><see cref="NotificationEventType.NewGuestMessage"/>, or <see cref="NotificationEventType.UrgentGuestMessage"/> for a message the assistant flagged.</param>
/// <param name="ThreadId">The <see cref="OtaMessageThread"/> or the <see cref="GuestThread"/>.</param>
/// <param name="MessageId">The message: the identity of the alert, so an event delivered again alerts once.</param>
/// <param name="OccurredAtUtc">When the message was written: the instant the quiet hours and the email slot are judged at, the same on every redelivery.</param>
public sealed record HostMessageAlert(
    Guid OrgId,
    HostMessageSource Source,
    NotificationEventType EventType,
    Guid ThreadId,
    Guid MessageId,
    DateTime OccurredAtUtc)
{
    /// <summary>The key of the conversation in the hub of messages.</summary>
    public string ThreadKey => (Source == HostMessageSource.Ota ? "ota:" : "direct:") + ThreadId.ToString("D");
}

/// <summary>
/// Alerts the hosts about a new message of a guest, OTA or direct, through the decision point of the preferences (E05/F03/US01/T03, NF-65,
/// ADR-032-R12): asks <see cref="INotificationPreferenceService.ResolveAsync"/> who is told and how, and delivers each channel the person chose.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The counter</b> is an <see cref="InAppNotification"/> (<c>ota-message</c>, entity the thread; <c>guest-message</c>, entity the booking, for
/// a direct thread, so reading the thread clears it) published to the open sessions of the person. The delivery key is that of the event and the
/// person, so an event delivered again writes it once. The counter never waits for the quiet hours.</item>
/// <item><b>The push</b> goes to the devices of that one person (<see cref="PushAudience.User"/>) with the route of the conversation
/// (<see cref="PushRoutes.MessageThread"/>; the booking's Messaggi route for a direct thread, which the app already opens).</item>
/// <item><b>The email</b> goes to the person's own address, at most one per conversation and person every
/// <see cref="GuestMessageRules.NotifyIntervalMinutes"/> minutes (<see cref="NotificationEmailSlot"/>, claimed with one conditional statement and
/// given back when the email cannot be queued). An email held back by the quiet hours holds the slot until it is due, so the messages of the night
/// are one email in the morning.</item>
/// <item><b>The quiet hours</b> defer the push and the email: they are scheduled for the instant the service gives (<see cref="HostMessageAlertDueJob"/>),
/// which asks the service again before it delivers, so a person who lost the property, changed its choice or was deactivated meanwhile is not told.</item>
/// <item><b>No text, no name of the guest, no address</b> is in the push, the email (of a channel) or the notification: the property, the channel and
/// the thread only. A direct booking's email keeps the names it has always had (the guest, the stay), for a host who can open that stay.</item>
/// </list>
/// A failure of one person does not stop the others; after all of them were tried the call throws, so the event bus delivers the event again: what
/// was done is not done twice (the delivery keys of the notification and of the push, and the slot of the email, which a redelivery finds taken: an
/// email is at most once, never twice). The caller that must not fail (the send of a message) catches.
/// </remarks>
public sealed class HostMessageAlertDelivery(
    AppDbContext db,
    INotificationPreferenceService preferences,
    IPushNotificationService pushNotifications,
    IEmailQueue emailQueue,
    IInAppNotificationPublisher notificationPublisher,
    IBackgroundJobClient jobs,
    PublicSiteLinks links,
    ILogger<HostMessageAlertDelivery> logger)
{
    /// <summary>
    /// The permission that opens the conversations of the hub (the policy <c>BookingRead</c> of <c>api/messages</c>): who cannot open a
    /// thread is not told about it. In the context <see cref="OrgRoleCatalog.ShortRent"/>.
    /// </summary>
    public const string MessageReadPermission = "booking.read";

    private const string Tenant = AppDbContext.TenantQueryFilter;

    /// <summary>How long an email slot is kept after its last use before the next claim of the person deletes it.</summary>
    private static readonly TimeSpan SlotRetention = TimeSpan.FromDays(7);

    /// <summary>Alerts every host the preferences select. Throws, after trying everybody, when a person could not be alerted.</summary>
    public async Task DeliverAsync(HostMessageAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var subject = await SubjectAsync(alert, ct);
        if (subject is null)
        {
            logger.LogInformation(
                "Message alert {ThreadKey} of org {OrgId}: the conversation or the message is not there, nobody is told",
                alert.ThreadKey,
                alert.OrgId);
            return;
        }

        var recipients = await preferences.ResolveAsync(ContextOf(alert, subject, atUtc: alert.OccurredAtUtc), ct);
        if (recipients.Count == 0)
        {
            logger.LogInformation("Message alert {ThreadKey} of org {OrgId}: nobody to tell", alert.ThreadKey, alert.OrgId);
            return;
        }

        var failures = new List<Exception>();
        foreach (var recipient in recipients)
        {
            try
            {
                await DeliverToAsync(alert, subject, recipient, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never the user id in the log: the thread and the org tell where to look.
                logger.LogError(ex, "Message alert {ThreadKey} of org {OrgId}: a person could not be alerted", alert.ThreadKey, alert.OrgId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} of {recipients.Count} people could not be alerted about {alert.ThreadKey}",
                failures);
        }
    }

    /// <summary>
    /// Delivers the channels that waited for the end of the quiet hours of one person (<see cref="HostMessageAlertDueJob"/>): asks the service
    /// again, so the person is told only if it is still in the audience, and only through the channels it still has. A channel the person put
    /// back into quiet hours meanwhile (it changed them after the message) is not delivered: it asked not to be disturbed, and the counter of the
    /// bell has been there since the message.
    /// </summary>
    public async Task DeliverDeferredAsync(QueuedMessageAlert queued, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queued);

        var alert = queued.ToAlert();
        var subject = await SubjectAsync(alert, ct);
        if (subject is null)
            return;

        var waiting = queued.Channels & (NotificationChannels.MobilePush | NotificationChannels.Email);
        var recipient = (await preferences.ResolveAsync(ContextOf(alert, subject, atUtc: null, onlyUserId: queued.UserId), ct))
            .FirstOrDefault(r => string.Equals(r.UserId, queued.UserId, StringComparison.Ordinal));
        if (recipient is null)
        {
            logger.LogInformation("Deferred message alert {ThreadKey} of org {OrgId}: the person is not in the audience any more", alert.ThreadKey, alert.OrgId);
            return;
        }

        var due = waiting & recipient.NowChannels;
        if (due != NotificationChannels.None)
            await SendHeldChannelsAsync(alert, subject, recipient.UserId, recipient.Email, due, claimedAtUtc: queued.DueAtUtc, ct);

        if ((waiting & ~due) != NotificationChannels.None)
        {
            logger.LogInformation(
                "Deferred message alert {ThreadKey} of org {OrgId}: {Channels} not delivered, the person's choice or quiet hours changed meanwhile",
                alert.ThreadKey,
                alert.OrgId,
                waiting & ~due);
        }
    }

    // ─── One person ───

    private async Task DeliverToAsync(HostMessageAlert alert, Subject subject, NotificationRecipient recipient, CancellationToken ct)
    {
        if (recipient.NowChannels.HasFlag(NotificationChannels.CounterOnly))
            await WriteBellAsync(alert, subject, recipient.UserId, ct);

        var now = recipient.NowChannels & (NotificationChannels.MobilePush | NotificationChannels.Email);
        if (now != NotificationChannels.None)
            await SendHeldChannelsAsync(alert, subject, recipient.UserId, recipient.Email, now, claimedAtUtc: null, ct);

        if (recipient.DeferredChannels == NotificationChannels.None || recipient.DueAtUtc is not { } due)
            return;

        // An email that waits holds the slot of the conversation until it is due: the other messages of the night queue no second one.
        var deferred = recipient.DeferredChannels;
        var claimedEmail = false;
        if (deferred.HasFlag(NotificationChannels.Email))
        {
            claimedEmail = await ClaimEmailSlotAsync(alert, recipient.UserId, due, ct);
            if (!claimedEmail)
                deferred &= ~NotificationChannels.Email;
        }

        if (deferred == NotificationChannels.None)
            return;

        try
        {
            Schedule(
                new QueuedMessageAlert
                {
                    OrgId = alert.OrgId,
                    Source = alert.Source,
                    EventType = alert.EventType,
                    ThreadId = alert.ThreadId,
                    MessageId = alert.MessageId,
                    OccurredAtUtc = alert.OccurredAtUtc,
                    UserId = recipient.UserId,
                    Channels = deferred,
                    DueAtUtc = due,
                },
                due);
        }
        catch (Exception) when (claimedEmail)
        {
            // Nothing is waiting for the slot: give it back, so the event delivered again can claim it and schedule the email.
            await ReleaseEmailSlotAsync(alert, recipient.UserId, due, ct);
            throw;
        }
    }

    /// <summary>
    /// The push and the email of one person. <paramref name="claimedAtUtc"/> is the instant the email slot was already claimed for (an email that
    /// waited for the quiet hours to end); <c>null</c> means the slot is claimed here, at the instant of the message.
    /// </summary>
    private async Task SendHeldChannelsAsync(
        HostMessageAlert alert,
        Subject subject,
        string userId,
        string email,
        NotificationChannels channels,
        DateTime? claimedAtUtc,
        CancellationToken ct)
    {
        if (channels.HasFlag(NotificationChannels.MobilePush))
        {
            var push = PushOf(alert, subject);
            var queued = pushNotifications.Enqueue(
                KeyOf(alert, userId),
                PushAudience.User(alert.OrgId, userId),
                new PushNotificationPayload(push.Title, push.Body, TypeOf(alert), subject.BookingIdForPush(alert), RouteOf(alert, subject)));
            if (!queued)
                throw new InvalidOperationException($"The push about {alert.ThreadKey} could not be queued.");
        }

        if (!channels.HasFlag(NotificationChannels.Email))
            return;

        var slotAt = claimedAtUtc ?? alert.OccurredAtUtc;
        if (claimedAtUtc is null && !await ClaimEmailSlotAsync(alert, userId, slotAt, ct))
            return;

        var content = EmailOf(alert, subject);
        if (!emailQueue.Enqueue(email, content, TemplateOf(alert)))
        {
            // Not queued (no provider, no address): give the slot back, so the next message tries again.
            await ReleaseEmailSlotAsync(alert, userId, slotAt, ct);
        }
    }

    // ─── Counter ───

    /// <summary>Writes the notification of the bell for the person, once per event, and tells its open sessions. False when it was there already.</summary>
    private async Task<bool> WriteBellAsync(HostMessageAlert alert, Subject subject, string userId, CancellationToken ct)
    {
        var key = KeyOf(alert, userId);
        if (await db.InAppNotifications
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(n => n.DeliveryKey == key && n.UserId == userId, ct))
        {
            return false;
        }

        var row = new InAppNotification
        {
            OrgId = alert.OrgId,
            UserId = userId,
            Type = TypeOf(alert),
            EntityId = EntityOf(alert, subject),
            DeliveryKey = key,
            CreatedAt = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(alert.OccurredAtUtc)),
        };
        db.InAppNotifications.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, InAppNotification.OncePerEventAndUserIndexName))
        {
            // The same event written by another run at the same moment: that row is the one.
            db.Entry(row).State = EntityState.Detached;
            return false;
        }

        try
        {
            await notificationPublisher.PublishAsync(userId, row.Type, row.EntityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row is stored: the real-time update is a hint, the bell reads the count on its next poll.
            logger.LogWarning(ex, "Message alert {ThreadKey}: the real-time update of the bell failed", alert.ThreadKey);
        }

        return true;
    }

    // ─── Email slot ───

    /// <summary>
    /// Claims the slot of the person and the conversation at <paramref name="atUtc"/>: true when no email went (or waits) in the last
    /// <see cref="GuestMessageRules.NotifyIntervalMinutes"/> minutes. One conditional statement, so two messages at the same moment queue one
    /// email, and an event delivered again (after another person of the same event failed) finds the slot taken and emails nobody twice.
    /// </summary>
    private async Task<bool> ClaimEmailSlotAsync(HostMessageAlert alert, string userId, DateTime atUtc, CancellationToken ct)
    {
        var at = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(atUtc));
        var cutoff = at.AddMinutes(-GuestMessageRules.NotifyIntervalMinutes);
        var threadKey = alert.ThreadKey;
        var slots = db.NotificationEmailSlots.IgnoreQueryFilters([Tenant]);

        bool claimed;
        if (db.Database.IsRelational())
        {
            claimed = await slots
                .Where(s => s.OrgId == alert.OrgId && s.UserId == userId && s.ThreadKey == threadKey && s.LastEmailAt <= cutoff)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastEmailAt, at), ct) == 1;
        }
        else
        {
            // EF InMemory (unit tests) has no conditional statement: the tracked row is changed and saved.
            var free = await slots
                .Where(s => s.OrgId == alert.OrgId && s.UserId == userId && s.ThreadKey == threadKey && s.LastEmailAt <= cutoff)
                .FirstOrDefaultAsync(ct);
            if (free is not null)
            {
                free.LastEmailAt = at;
                await db.SaveChangesAsync(ct);
            }

            claimed = free is not null;
        }

        if (!claimed)
        {
            // Either the slot is taken, or there is none yet.
            if (await slots.AsNoTracking().AnyAsync(s => s.OrgId == alert.OrgId && s.UserId == userId && s.ThreadKey == threadKey, ct))
                return false;

            var created = new NotificationEmailSlot { OrgId = alert.OrgId, UserId = userId, ThreadKey = threadKey, LastEmailAt = at };
            db.NotificationEmailSlots.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, NotificationEmailSlot.OncePerUserAndThreadIndexName))
            {
                // Another message created it first: that one has the slot.
                db.Entry(created).State = EntityState.Detached;
                return false;
            }
        }

        await PruneEmailSlotsAsync(alert.OrgId, userId, at, ct);
        return true;
    }

    /// <summary>Gives the slot back after an email that could not be queued: the row of this very claim goes, so the next message is free.</summary>
    private async Task ReleaseEmailSlotAsync(HostMessageAlert alert, string userId, DateTime atUtc, CancellationToken ct)
    {
        var at = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(atUtc));
        var threadKey = alert.ThreadKey;
        var claimed = db.NotificationEmailSlots
            .IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == alert.OrgId && s.UserId == userId && s.ThreadKey == threadKey && s.LastEmailAt == at);
        if (db.Database.IsRelational())
        {
            await claimed.ExecuteDeleteAsync(ct);
            return;
        }

        db.NotificationEmailSlots.RemoveRange(await claimed.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The slots of the person not used for a week are of conversations long over: they go with the next claim.</summary>
    private async Task PruneEmailSlotsAsync(Guid orgId, string userId, DateTime atUtc, CancellationToken ct)
    {
        var limit = atUtc - SlotRetention;
        var stale = db.NotificationEmailSlots
            .IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == orgId && s.UserId == userId && s.LastEmailAt < limit);
        if (db.Database.IsRelational())
        {
            await stale.ExecuteDeleteAsync(ct);
            return;
        }

        db.NotificationEmailSlots.RemoveRange(await stale.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    // ─── Deferral ───

    private void Schedule(QueuedMessageAlert queued, DateTime dueUtc)
    {
        var due = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc));
        var jobId = jobs.Schedule<HostMessageAlertDueJob>(job => job.DeliverAsync(queued, CancellationToken.None), due);
        logger.LogInformation(
            "Message alert {ThreadKey} of org {OrgId}: {Channels} held by the quiet hours until {DueAt:O} (job {JobId})",
            queued.ToAlert().ThreadKey,
            queued.OrgId,
            queued.Channels,
            due,
            jobId);
    }

    // ─── What the alert is about ───

    private NotificationEventContext ContextOf(HostMessageAlert alert, Subject subject, DateTime? atUtc, string? onlyUserId = null) =>
        new(
            alert.OrgId,
            alert.EventType,
            subject.PropertyId,
            RequiredPermission: new NotificationPermission(OrgRoleCatalog.ShortRent, MessageReadPermission),
            OnlyUserId: onlyUserId,
            AtUtc: atUtc);

    /// <summary>
    /// What the texts of the alert need, read from the thread in its org; <c>null</c> when the thread (or, for a channel, the inbound message)
    /// is not there any more: nothing to tell about. The property is the one of the thread <b>now</b>, so the audience follows a mapping that changed.
    /// </summary>
    private async Task<Subject?> SubjectAsync(HostMessageAlert alert, CancellationToken ct)
    {
        if (alert.Source == HostMessageSource.Ota)
        {
            // The inbound message of the event, in the org: read as a subquery (a collection expression cannot be in an expression tree).
            var messages = db.OtaMessages.IgnoreQueryFilters([Tenant]);
            var thread = await db.OtaMessageThreads
                .AsNoTracking()
                .IgnoreQueryFilters([Tenant])
                .Where(t => t.Id == alert.ThreadId
                            && t.OrgId == alert.OrgId
                            && messages.Any(m =>
                                m.Id == alert.MessageId
                                && m.ThreadId == t.Id
                                && m.OrgId == alert.OrgId
                                && m.Direction == OtaMessageDirection.Inbound))
                .Select(t => new { t.PropertyId, PropertyName = t.Property != null ? t.Property.Name : null, t.ChannelSource })
                .FirstOrDefaultAsync(ct);
            return thread is null
                ? null
                : new Subject(thread.PropertyId, thread.PropertyName, EmailTemplates.OtaChannelBrand(thread.ChannelSource), null, null, null, null);
        }

        var direct = await db.GuestThreads
            .AsNoTracking()
            .IgnoreQueryFilters([Tenant])
            .Where(t => t.Id == alert.ThreadId && t.OrgId == alert.OrgId)
            .Select(t => new
            {
                t.PropertyId,
                PropertyName = t.Property.Name,
                t.BookingId,
                t.Booking.Guest.FirstName,
                t.Booking.Guest.LastName,
                t.Booking.CheckInDate,
                t.Booking.CheckOutDate,
            })
            .FirstOrDefaultAsync(ct);
        return direct is null
            ? null
            : new Subject(
                direct.PropertyId,
                direct.PropertyName,
                null,
                direct.BookingId,
                $"{direct.FirstName} {direct.LastName}".Trim(),
                direct.CheckInDate,
                direct.CheckOutDate);
    }

    /// <summary>What an alert reads from its thread. <c>ChannelName</c> is set for a channel, the guest and the stay for a direct booking.</summary>
    private sealed record Subject(
        Guid? PropertyId,
        string? PropertyName,
        string? ChannelName,
        Guid? BookingId,
        string? GuestName,
        DateTime? CheckIn,
        DateTime? CheckOut)
    {
        /// <summary>The booking the push names: a direct thread's, none for a channel's.</summary>
        public Guid? BookingIdForPush(HostMessageAlert alert) => alert.Source == HostMessageSource.Direct ? BookingId : null;
    }

    /// <summary>The kind of the notification and of the push (<c>data.type</c>).</summary>
    private static string TypeOf(HostMessageAlert alert) =>
        alert.Source == HostMessageSource.Ota ? PushTypes.OtaMessage : PushTypes.GuestMessage;

    /// <summary>The entity of the notification: the thread for a channel, the booking for a direct thread (reading the thread clears it).</summary>
    private static Guid EntityOf(HostMessageAlert alert, Subject subject) =>
        alert.Source == HostMessageSource.Ota ? alert.ThreadId : subject.BookingId ?? alert.ThreadId;

    /// <summary>The delivery key of the notification and of the push: one per message, and per person for a channel.</summary>
    private static string KeyOf(HostMessageAlert alert, string userId) =>
        alert.Source == HostMessageSource.Ota
            ? PushDeliveryKeys.OtaMessage(alert.MessageId, userId)
            : PushDeliveryKeys.GuestMessage(alert.MessageId);

    private static string RouteOf(HostMessageAlert alert, Subject subject) =>
        alert.Source == HostMessageSource.Ota
            ? PushRoutes.MessageThread(alert.ThreadKey)
            : PushRoutes.BookingMessages(subject.BookingId ?? throw new InvalidOperationException("A direct thread has a booking."));

    private static PushText PushOf(HostMessageAlert alert, Subject subject) =>
        alert.Source == HostMessageSource.Ota
            ? EmailTemplates.OtaMessagePush(EmailTemplates.DefaultCulture, subject.PropertyName, subject.ChannelName ?? string.Empty)
            : EmailTemplates.GuestMessagePush(EmailTemplates.DefaultCulture, subject.PropertyName ?? string.Empty);

    private static string TemplateOf(HostMessageAlert alert) =>
        alert.Source == HostMessageSource.Ota ? EmailTemplates.Names.HostMessageReceived : EmailTemplates.Names.GuestMessageToHost;

    private EmailContent EmailOf(HostMessageAlert alert, Subject subject)
    {
        if (alert.Source == HostMessageSource.Ota)
        {
            return EmailTemplates.HostMessageReceived(
                EmailTemplates.DefaultCulture,
                subject.PropertyName,
                subject.ChannelName ?? string.Empty,
                links.IsConfigured ? links.HostMessageThread(alert.ThreadKey) : null);
        }

        var bookingId = subject.BookingId ?? throw new InvalidOperationException("A direct thread has a booking.");
        return EmailTemplates.GuestMessageToHost(
            EmailTemplates.DefaultCulture,
            subject.GuestName ?? string.Empty,
            subject.PropertyName ?? string.Empty,
            subject.CheckIn ?? alert.OccurredAtUtc,
            subject.CheckOut ?? alert.OccurredAtUtc,
            links.IsConfigured ? links.HostBookingMessages(bookingId) : null);
    }
}
