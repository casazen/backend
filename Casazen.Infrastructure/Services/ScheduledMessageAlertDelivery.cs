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

/// <summary>The kinds of problem a scheduled message alerts the hosts about: codes, part of the key of the push and of the bell (E05/F07/US01, NF-71, AC-NF71-3).</summary>
public static class ScheduledMessageAlertKinds
{
    /// <summary>The message is held back for a piece of data it has no value for.</summary>
    public const string Blocked = "blocked";

    /// <summary>The channel did not take the message and the retries are used.</summary>
    public const string Failed = "failed";

    /// <summary>The system skipped the message (the rules of the channel, no way to the guest, a mapping nobody decided) or a person skipped it.</summary>
    public const string Skipped = "skipped";

    /// <summary>The message went out when the clock of the property was outside the send window.</summary>
    public const string OutsideHours = "outside-hours";
}

/// <summary>
/// A scheduled message the hosts are alerted about: ids, a kind and an instant, <b>never the words of the message, a name or an address</b>. <paramref name="ExceptUserId"/> is the person who did
/// it themselves (the one who skipped the message or asked for «Send now»), who needs no alert about their own action. <paramref name="OccurredAtUtc"/> is the instant the quiet hours are judged at.
/// </summary>
public sealed record ScheduledMessageAlert(Guid OrgId, Guid PropertyId, Guid BookingId, Guid MessageId, string Kind, string? ExceptUserId, DateTime OccurredAtUtc);

/// <summary>
/// Alerts the hosts about a scheduled message that needs them (E05/F07/US01/T02, T03, NF-71, AC-NF71-3, ADR-025): <see cref="INotificationPreferenceService.ResolveAsync"/> says who is told
/// and how (the audience of the property of the booking, narrowed to who may open the booking, the choice of each one for <see cref="NotificationEventType.ScheduledMessageIssue"/>, the quiet
/// hours) and each channel the person chose is delivered: the bell with its live update (the pop-up and the open pages read it), the push, the e-mail. It is the producer of the notification type
/// that existed with its preferences and its screen and no producer.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The counter</b> is an <see cref="InAppNotification"/> (<c>scheduled-message-issue</c>, entity the booking), once for each message, kind and person (its delivery key), published to the open
/// sessions of the person; it never waits for the quiet hours.</item>
/// <item><b>The push</b> goes to the devices of that one person (<see cref="PushAudience.User"/>) with the route of the booking (<see cref="PushRoutes.Booking"/>), the property and nothing else.</item>
/// <item><b>The e-mail</b> goes to the person's own address, once for each message and kind: the slot (<see cref="NotificationEmailSlot"/>) is claimed by an insert and given back when the e-mail cannot be
/// queued; one held back by the quiet hours holds the slot until it is due.</item>
/// <item><b>Once, in any order.</b> A person who could not be reached makes <see cref="AlertAsync"/> throw after the others were told; <see cref="AlertOrRetryAsync"/> hands the whole alert to a job that tries
/// again with the retries of Hangfire, and what the first run did is not done twice (the keys and the slot). Nothing is swallowed: every failure is logged with the message and the org.</item>
/// <item><b>Privacy.</b> No word of the message, no name of a guest, no link, no code and no address is in a push, an e-mail, a notification or a log; the log carries the message and the org, never the user.</item>
/// </list>
/// </remarks>
public sealed class ScheduledMessageAlertDelivery(
    AppDbContext db,
    INotificationPreferenceService preferences,
    IPushNotificationService pushNotifications,
    IEmailQueue emailQueue,
    IInAppNotificationPublisher notificationPublisher,
    IBackgroundJobClient jobs,
    PublicSiteLinks links,
    ILogger<ScheduledMessageAlertDelivery> logger)
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    /// <summary>How long an e-mail slot is kept after its last use before the next claim of the person deletes it.</summary>
    private static readonly TimeSpan SlotRetention = TimeSpan.FromDays(7);

    /// <summary>How long a failed alert waits before the job raises it again (a passing error has time to pass).</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="AlertAsync"/> for the path of a send, which must never fail because of an alert: when somebody could not be reached the failure is logged and the whole alert is handed to a job
    /// (<see cref="ScheduledMessageAlertJob.RaiseAsync"/>) that raises it again with the retries of Hangfire. Only a failure to queue the job itself is thrown.
    /// </summary>
    public async Task AlertOrRetryAsync(ScheduledMessageAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);

        try
        {
            await AlertAsync(alert, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled message alert {MessageId} ({Kind}) of org {OrgId} failed: it is raised again by a job", alert.MessageId, alert.Kind, alert.OrgId);
            jobs.Schedule<ScheduledMessageAlertJob>(job => job.RaiseAsync(alert, CancellationToken.None), RetryDelay);
        }
    }

    /// <summary>Alerts every host the preferences select. Throws, after trying everybody, when a person could not be alerted.</summary>
    public async Task AlertAsync(ScheduledMessageAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var propertyName = await PropertyNameAsync(alert, ct);
        var recipients = (await preferences.ResolveAsync(ContextOf(alert, atUtc: alert.OccurredAtUtc, onlyUserId: null), ct))
            .Where(r => !string.Equals(r.UserId, alert.ExceptUserId, StringComparison.Ordinal))
            .ToList();
        if (recipients.Count == 0)
        {
            logger.LogInformation("Scheduled message alert {MessageId} ({Kind}) of org {OrgId}: nobody to tell", alert.MessageId, alert.Kind, alert.OrgId);
            return;
        }

        var failures = new List<Exception>();
        foreach (var recipient in recipients)
        {
            try
            {
                await DeliverToAsync(alert, propertyName, recipient, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never the user id in the log: the message and the org tell where to look.
                logger.LogError(ex, "Scheduled message alert {MessageId} ({Kind}) of org {OrgId}: a person could not be alerted", alert.MessageId, alert.Kind, alert.OrgId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} of {recipients.Count} people could not be alerted about scheduled message {alert.MessageId}",
                failures);
        }
    }

    /// <summary>
    /// Delivers the channels that waited for the end of the quiet hours of one person (<see cref="ScheduledMessageAlertJob"/>): asks the service again, so the person is told only if it is still
    /// in the audience, and only through the channels it still has. A channel the person put back into quiet hours meanwhile is not delivered: it asked not to be disturbed, and the counter of
    /// the bell has been there since the alert.
    /// </summary>
    public async Task DeliverDeferredAsync(QueuedScheduledMessageAlert queued, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queued);

        var alert = queued.ToAlert();
        var propertyName = await PropertyNameAsync(alert, ct);
        var waiting = queued.Channels & (NotificationChannels.MobilePush | NotificationChannels.Email);
        var recipient = (await preferences.ResolveAsync(ContextOf(alert, atUtc: null, onlyUserId: queued.UserId), ct))
            .FirstOrDefault(r => string.Equals(r.UserId, queued.UserId, StringComparison.Ordinal));
        if (recipient is null)
        {
            logger.LogInformation("Deferred scheduled message alert {MessageId} of org {OrgId}: the person is not in the audience any more", alert.MessageId, alert.OrgId);
            return;
        }

        var due = waiting & recipient.NowChannels;
        if (due != NotificationChannels.None)
            await SendHeldChannelsAsync(alert, propertyName, recipient.UserId, recipient.Email, due, claimedAtUtc: queued.DueAtUtc, ct);

        if ((waiting & ~due) != NotificationChannels.None)
        {
            logger.LogInformation(
                "Deferred scheduled message alert {MessageId} of org {OrgId}: {Channels} not delivered, the person's choice or quiet hours changed meanwhile",
                alert.MessageId,
                alert.OrgId,
                waiting & ~due);
        }
    }

    // ─── One person ───

    private async Task DeliverToAsync(ScheduledMessageAlert alert, string? propertyName, NotificationRecipient recipient, CancellationToken ct)
    {
        if (recipient.NowChannels.HasFlag(NotificationChannels.CounterOnly))
            await WriteBellAsync(alert, recipient.UserId, ct);

        var now = recipient.NowChannels & (NotificationChannels.MobilePush | NotificationChannels.Email);
        if (now != NotificationChannels.None)
            await SendHeldChannelsAsync(alert, propertyName, recipient.UserId, recipient.Email, now, claimedAtUtc: null, ct);

        if (recipient.DeferredChannels == NotificationChannels.None || recipient.DueAtUtc is not { } due)
            return;

        // An e-mail that waits holds the slot of the alert until it is due: the job only queues it.
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
                new QueuedScheduledMessageAlert
                {
                    OrgId = alert.OrgId,
                    PropertyId = alert.PropertyId,
                    BookingId = alert.BookingId,
                    MessageId = alert.MessageId,
                    Kind = alert.Kind,
                    OccurredAtUtc = alert.OccurredAtUtc,
                    UserId = recipient.UserId,
                    Channels = deferred,
                    DueAtUtc = due,
                },
                due);
        }
        catch (Exception) when (claimedEmail)
        {
            // Nothing is waiting for the slot: give it back, so the alert raised again can claim it and schedule the e-mail.
            await ReleaseEmailSlotAsync(alert, recipient.UserId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The push and the e-mail of one person. <paramref name="claimedAtUtc"/> is the instant the e-mail slot was already claimed for (an e-mail that waited for the quiet hours to end);
    /// <c>null</c> means the slot is claimed here, at the instant of the alert.
    /// </summary>
    private async Task SendHeldChannelsAsync(
        ScheduledMessageAlert alert,
        string? propertyName,
        string userId,
        string email,
        NotificationChannels channels,
        DateTime? claimedAtUtc,
        CancellationToken ct)
    {
        if (channels.HasFlag(NotificationChannels.MobilePush))
        {
            var text = EmailTemplates.ScheduledMessageIssuePush(EmailTemplates.DefaultCulture, propertyName);
            var queued = pushNotifications.Enqueue(
                KeyOf(alert, userId),
                PushAudience.User(alert.OrgId, userId),
                new PushNotificationPayload(text.Title, text.Body, PushTypes.ScheduledMessageIssue, alert.BookingId, PushRoutes.Booking(alert.BookingId)));
            if (!queued)
                throw new InvalidOperationException($"The push about scheduled message {alert.MessageId} could not be queued.");
        }

        if (!channels.HasFlag(NotificationChannels.Email))
            return;

        if (claimedAtUtc is null && !await ClaimEmailSlotAsync(alert, userId, alert.OccurredAtUtc, ct))
            return;

        var content = EmailTemplates.HostScheduledMessageIssue(
            EmailTemplates.DefaultCulture,
            propertyName,
            links.IsConfigured ? links.HostBooking(alert.BookingId) : null);
        if (!emailQueue.Enqueue(email, content, EmailTemplates.Names.HostScheduledMessageIssue))
        {
            // Not queued (no provider, no address): give the slot back, so an alert raised again tries again.
            await ReleaseEmailSlotAsync(alert, userId, ct);
        }
    }

    // ─── Counter ───

    /// <summary>Writes the notification of the bell for the person, once for each message and kind, and tells its open sessions. False when it was there already.</summary>
    private async Task<bool> WriteBellAsync(ScheduledMessageAlert alert, string userId, CancellationToken ct)
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
            Type = PushTypes.ScheduledMessageIssue,
            EntityId = alert.BookingId,
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
            // The same alert written by another run at the same moment: that row is the one.
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
        catch (Exception)
        {
            // A row that could not be saved must not stay in the tracker, or every later save of this context would try it again.
            db.Entry(row).State = EntityState.Detached;
            throw;
        }

        try
        {
            await notificationPublisher.PublishAsync(userId, row.Type, row.EntityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row is stored: the real-time update is a hint, the bell reads the count on its next poll.
            logger.LogWarning(ex, "Scheduled message alert {MessageId}: the real-time update of the bell failed", alert.MessageId);
        }

        return true;
    }

    // ─── E-mail slot ───

    /// <summary>
    /// Claims the slot of the person and the alert: true when no e-mail went (or waits) for it. One insert on the unique index <c>(org, user, key)</c>, so two runs at the same moment queue one e-mail,
    /// and an alert delivered again (after another person of the same alert failed) finds the slot taken and e-mails nobody twice.
    /// </summary>
    private async Task<bool> ClaimEmailSlotAsync(ScheduledMessageAlert alert, string userId, DateTime atUtc, CancellationToken ct)
    {
        var key = EmailSlotKeyOf(alert);
        var at = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(atUtc));
        if (await db.NotificationEmailSlots
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(s => s.OrgId == alert.OrgId && s.UserId == userId && s.ThreadKey == key, ct))
        {
            return false;
        }

        var created = new NotificationEmailSlot { OrgId = alert.OrgId, UserId = userId, ThreadKey = key, LastEmailAt = at };
        db.NotificationEmailSlots.Add(created);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, NotificationEmailSlot.OncePerUserAndThreadIndexName))
        {
            // Another run claimed it first: that one has the slot.
            db.Entry(created).State = EntityState.Detached;
            return false;
        }
        catch (Exception)
        {
            db.Entry(created).State = EntityState.Detached;
            throw;
        }

        await PruneEmailSlotsAsync(alert.OrgId, userId, at, ct);
        return true;
    }

    /// <summary>Gives the slot back after an e-mail that could not be queued: the row of this very alert goes, so the alert raised again is free.</summary>
    private async Task ReleaseEmailSlotAsync(ScheduledMessageAlert alert, string userId, CancellationToken ct)
    {
        var key = EmailSlotKeyOf(alert);
        var claimed = db.NotificationEmailSlots
            .IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == alert.OrgId && s.UserId == userId && s.ThreadKey == key);
        if (db.Database.IsRelational())
        {
            await claimed.ExecuteDeleteAsync(ct);
            return;
        }

        db.NotificationEmailSlots.RemoveRange(await claimed.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The slots of the person not used for a week are of alerts long over: they go with the next claim.</summary>
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

    /// <summary>The key of the slot: the message and the kind of the alert. At most 64 characters.</summary>
    private static string EmailSlotKeyOf(ScheduledMessageAlert alert) => $"scheduled:{alert.MessageId:N}:{alert.Kind}";

    // ─── Deferral ───

    private void Schedule(QueuedScheduledMessageAlert queued, DateTime dueUtc)
    {
        var due = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc));
        var jobId = jobs.Schedule<ScheduledMessageAlertJob>(job => job.DeliverAsync(queued, CancellationToken.None), due);
        logger.LogInformation(
            "Scheduled message alert {MessageId} of org {OrgId}: {Channels} held by the quiet hours until {DueAt:O} (job {JobId})",
            queued.MessageId,
            queued.OrgId,
            queued.Channels,
            due,
            jobId);
    }

    // ─── What the alert is about ───

    /// <summary>Who is told about a scheduled message: the audience of its property (the administrators of the org when it has none), narrowed to who may open the booking.</summary>
    private static NotificationEventContext ContextOf(ScheduledMessageAlert alert, DateTime? atUtc, string? onlyUserId) =>
        new(
            alert.OrgId,
            NotificationEventType.ScheduledMessageIssue,
            alert.PropertyId,
            RequiredPermission: new NotificationPermission(OrgRoleCatalog.ShortRent, HostMessageAlertDelivery.MessageReadPermission),
            OnlyUserId: onlyUserId,
            AtUtc: atUtc);

    private static string KeyOf(ScheduledMessageAlert alert, string userId) => PushDeliveryKeys.ScheduledMessageIssue(alert.MessageId, alert.Kind, 0, userId);

    /// <summary>The name of the property of the message (never the guest, never the words); <c>null</c> when it is gone.</summary>
    private Task<string?> PropertyNameAsync(ScheduledMessageAlert alert, CancellationToken ct) =>
        db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([Tenant])
            .Where(p => p.Id == alert.PropertyId && p.OrgId == alert.OrgId)
            .Select(p => (string?)p.Name)
            .FirstOrDefaultAsync(ct);
}
