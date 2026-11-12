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

/// <summary>
/// An item of the approval queue that waits for a decision and the hosts who may decide are alerted about (E05/F04/US01/T02, NF-68): ids and an instant, never a text, a
/// name or an address. <paramref name="OccurredAtUtc"/> is when the alert was raised (the creation of the item, or the moment it went back to pending with a new text):
/// the instant the quiet hours are judged at and part of the keys of the push and of the bell, so an alert raised again for the same moment tells nobody twice.
/// </summary>
public sealed record ApprovalAlert(Guid OrgId, Guid ItemId, DateTime OccurredAtUtc);

/// <summary>
/// Raises the "To do" of an item of the approval queue and alerts the hosts who may decide it, through the decision point of the preferences (E05/F04/US01/T02, NF-68,
/// ADR-025, ADR-026): <see cref="INotificationPreferenceService.ResolveAsync"/> says who is told and how (the audience of the item's property or the administrators of the
/// org, narrowed to the people who hold <c>approval.decide</c>, the choice of each one for <see cref="NotificationEventType.AiDraftToApprove"/>, the quiet hours), and each
/// channel the person chose is delivered. It replaces the old notice «there is an item to approve», written to every active member of the org with no scope and no permission.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The "To do"</b> is ONE item for the approval (type <c>MessageToApprove</c>, action <c>ApproveAiDraft</c>, entity <c>ApprovalQueueItem</c>), raised with
/// <see cref="IInternalActivityService.CreateAsync"/> (idempotent while it is open) and linked in <see cref="ApprovalQueueItem.ActivityId"/>, which is closed by the decision.
/// Its deadline is the expiry of the item.</item>
/// <item><b>The counter</b> is an <see cref="InAppNotification"/> (<c>approval-queue-item</c>, entity the item), once for each alert and person (its delivery key), published to
/// the open sessions of the person; it never waits for the quiet hours.</item>
/// <item><b>The push</b> goes to the devices of that one person (<see cref="PushAudience.User"/>) with the route of the conversation for the draft of a message
/// (<see cref="PushRoutes.MessageThread"/>) and <see cref="PushRoutes.Approvals"/> for every other item, the field <c>approvalItemId</c> and the category
/// <see cref="ApprovalQueueRules.PushCategory"/> (the buttons of the app).</item>
/// <item><b>The e-mail</b> goes to the person's own address, once for each alert and person: the slot (<see cref="NotificationEmailSlot"/>, key <c>approval:{item}:{time}</c>) is
/// claimed by an insert and given back when the e-mail cannot be queued; one held back by the quiet hours holds the slot until it is due.</item>
/// <item><b>The quiet hours</b> defer the push and the e-mail: they are scheduled for the instant the service gives (<see cref="ApprovalAlertJob.DeliverAsync"/>), which asks the
/// service again before it delivers, so a person who lost the property, changed its choice or was deactivated meanwhile is not told.</item>
/// <item><b>Once, in any order.</b> A person who could not be reached makes <see cref="RaiseAsync"/> throw after the others were told; <see cref="RaiseOrRetryAsync"/> hands the
/// whole alert to a job that tries again with the retries of Hangfire, and what the first run did is not done twice (the keys, the slot, the open "To do"). Nothing is
/// swallowed: every failure is logged with the item and the org, and retried.</item>
/// <item><b>Privacy.</b> No text of the item, no name of a guest and no address is in a push, an e-mail, a notification or a log: the property and the kind of item only.
/// The log carries the item and the org, never the user id.</item>
/// </list>
/// </remarks>
public sealed class ApprovalAlertDelivery(
    AppDbContext db,
    INotificationPreferenceService preferences,
    IInternalActivityService activities,
    IPushNotificationService pushNotifications,
    IEmailQueue emailQueue,
    IInAppNotificationPublisher notificationPublisher,
    IBackgroundJobClient jobs,
    PublicSiteLinks links,
    ILogger<ApprovalAlertDelivery> logger)
{
    /// <summary>The kind of entity the "To do" of an item is about (<see cref="InternalActivity.EntityType"/>).</summary>
    public const string TodoEntityType = nameof(ApprovalQueueItem);

    private const string Tenant = AppDbContext.TenantQueryFilter;

    /// <summary>How long an e-mail slot is kept after its last use before the next claim of the person deletes it.</summary>
    private static readonly TimeSpan SlotRetention = TimeSpan.FromDays(7);

    /// <summary>How long a failed alert waits before the job raises it again (a passing error has time to pass).</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="RaiseAsync"/> for the path of a submission, which must never fail because of an alert: when the "To do" or somebody could not be reached (or the audience
    /// could not be read) the failure is logged and the whole alert is handed to a job (<see cref="ApprovalAlertJob.RaiseAsync"/>) that raises it again with the retries of
    /// Hangfire. Only a failure to queue the job itself is thrown.
    /// </summary>
    public async Task RaiseOrRetryAsync(ApprovalAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);

        try
        {
            await RaiseAsync(alert, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Approval alert {ItemId} of org {OrgId} failed: it is raised again by a job", alert.ItemId, alert.OrgId);
            jobs.Schedule<ApprovalAlertJob>(job => job.RaiseAsync(alert, CancellationToken.None), RetryDelay);
        }
    }

    /// <summary>
    /// Alerts every host the preferences select and raises the "To do" of the item. Throws, after trying everything, when a person could not be alerted or the "To do" could not be
    /// raised. An item that is not waiting any more (decided, expired, gone) tells nobody.
    /// </summary>
    public async Task RaiseAsync(ApprovalAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var item = await LoadPendingAsync(alert.OrgId, alert.ItemId, ct);
        if (item is null)
        {
            logger.LogInformation(
                "Approval alert {ItemId} of org {OrgId}: the item is not waiting for a decision any more, nobody is told",
                alert.ItemId,
                alert.OrgId);
            return;
        }

        var subject = await SubjectOfAsync(item, ct);
        var recipients = await preferences.ResolveAsync(ContextOf(item, alert.OccurredAtUtc, onlyUserId: null), ct);

        var failures = new List<Exception>();
        foreach (var recipient in recipients)
        {
            try
            {
                await DeliverToAsync(alert, item, subject, recipient, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never the user id in the log: the item and the org tell where to look.
                logger.LogError(ex, "Approval alert {ItemId} of org {OrgId}: a person could not be alerted", item.Id, item.OrgId);
                failures.Add(ex);
            }
        }

        if (recipients.Count == 0)
            logger.LogInformation("Approval alert {ItemId} of org {OrgId}: nobody to tell", item.Id, item.OrgId);

        try
        {
            await EnsureTodoAsync(item, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Approval alert {ItemId} of org {OrgId}: the To do could not be raised", item.Id, item.OrgId);
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} of {recipients.Count + 1} steps of the alert of approval item {item.Id} failed",
                failures);
        }
    }

    /// <summary>
    /// Delivers the channels that waited for the end of the quiet hours of one person (<see cref="ApprovalAlertJob"/>): asks the service again, so the person is told only if it is
    /// still in the audience, and only through the channels it still has. A channel the person put back into quiet hours meanwhile is not delivered: it asked not to be
    /// disturbed, and the counter of the bell has been there since the alert. An item decided or expired meanwhile is not alerted.
    /// </summary>
    public async Task DeliverDeferredAsync(QueuedApprovalAlert queued, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queued);

        var item = await LoadPendingAsync(queued.OrgId, queued.ItemId, ct);
        if (item is null)
            return;

        var subject = await SubjectOfAsync(item, ct);
        var waiting = queued.Channels & (NotificationChannels.MobilePush | NotificationChannels.Email);
        var recipient = (await preferences.ResolveAsync(ContextOf(item, atUtc: null, onlyUserId: queued.UserId), ct))
            .FirstOrDefault(r => string.Equals(r.UserId, queued.UserId, StringComparison.Ordinal));
        if (recipient is null)
        {
            logger.LogInformation("Deferred approval alert {ItemId} of org {OrgId}: the person is not in the audience any more", item.Id, item.OrgId);
            return;
        }

        var due = waiting & recipient.NowChannels;
        if (due != NotificationChannels.None)
            await SendHeldChannelsAsync(item, subject, queued.OccurredAtUtc, recipient.UserId, recipient.Email, due, claimedAtUtc: queued.DueAtUtc, ct);

        if ((waiting & ~due) != NotificationChannels.None)
        {
            logger.LogInformation(
                "Deferred approval alert {ItemId} of org {OrgId}: {Channels} not delivered, the person's choice or quiet hours changed meanwhile",
                item.Id,
                item.OrgId,
                waiting & ~due);
        }
    }

    // ─── One person ───

    private async Task DeliverToAsync(ApprovalAlert alert, ApprovalQueueItem item, Subject subject, NotificationRecipient recipient, CancellationToken ct)
    {
        if (recipient.NowChannels.HasFlag(NotificationChannels.CounterOnly))
            await WriteBellAsync(item, alert.OccurredAtUtc, recipient.UserId, ct);

        var now = recipient.NowChannels & (NotificationChannels.MobilePush | NotificationChannels.Email);
        if (now != NotificationChannels.None)
            await SendHeldChannelsAsync(item, subject, alert.OccurredAtUtc, recipient.UserId, recipient.Email, now, claimedAtUtc: null, ct);

        if (recipient.DeferredChannels == NotificationChannels.None || recipient.DueAtUtc is not { } due)
            return;

        // An e-mail that waits holds the slot of the alert until it is due: the job only queues it.
        var deferred = recipient.DeferredChannels;
        var claimedEmail = false;
        if (deferred.HasFlag(NotificationChannels.Email))
        {
            claimedEmail = await ClaimEmailSlotAsync(item, alert.OccurredAtUtc, recipient.UserId, due, ct);
            if (!claimedEmail)
                deferred &= ~NotificationChannels.Email;
        }

        if (deferred == NotificationChannels.None)
            return;

        try
        {
            Schedule(
                new QueuedApprovalAlert
                {
                    OrgId = item.OrgId,
                    ItemId = item.Id,
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
            await ReleaseEmailSlotAsync(item, alert.OccurredAtUtc, recipient.UserId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The push and the e-mail of one person. <paramref name="claimedAtUtc"/> is the instant the e-mail slot was already claimed for (an e-mail that waited for the quiet hours to
    /// end); <c>null</c> means the slot is claimed here, at the instant of the alert.
    /// </summary>
    private async Task SendHeldChannelsAsync(
        ApprovalQueueItem item,
        Subject subject,
        DateTime raisedAtUtc,
        string userId,
        string email,
        NotificationChannels channels,
        DateTime? claimedAtUtc,
        CancellationToken ct)
    {
        if (channels.HasFlag(NotificationChannels.MobilePush))
        {
            var text = EmailTemplates.ApprovalPush(EmailTemplates.DefaultCulture, subject.MessageDraft, subject.PropertyName);
            var queued = pushNotifications.Enqueue(
                PushDeliveryKeys.ApprovalQueueItem(item.Id, raisedAtUtc, userId),
                PushAudience.User(item.OrgId, userId),
                new PushNotificationPayload(
                    text.Title,
                    text.Body,
                    PushTypes.ApprovalQueueItem,
                    null,
                    subject.Route,
                    ApprovalItemId: item.Id,
                    CategoryId: ApprovalQueueRules.PushCategory));
            if (!queued)
                throw new InvalidOperationException($"The push about approval item {item.Id} could not be queued.");
        }

        if (!channels.HasFlag(NotificationChannels.Email))
            return;

        if (claimedAtUtc is null && !await ClaimEmailSlotAsync(item, raisedAtUtc, userId, raisedAtUtc, ct))
            return;

        var content = EmailTemplates.HostApprovalToDecide(
            EmailTemplates.DefaultCulture,
            subject.PropertyName,
            links.IsConfigured ? links.HostApprovalQueue() : null);
        if (!emailQueue.Enqueue(email, content, EmailTemplates.Names.HostApprovalToDecide))
        {
            // Not queued (no provider, no address): give the slot back, so an alert raised again tries again.
            await ReleaseEmailSlotAsync(item, raisedAtUtc, userId, ct);
        }
    }

    // ─── Counter ───

    /// <summary>Writes the notification of the bell for the person, once for each alert, and tells its open sessions. False when it was there already.</summary>
    private async Task<bool> WriteBellAsync(ApprovalQueueItem item, DateTime raisedAtUtc, string userId, CancellationToken ct)
    {
        var key = PushDeliveryKeys.ApprovalQueueItem(item.Id, raisedAtUtc, userId);
        if (await db.InAppNotifications
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(n => n.DeliveryKey == key && n.UserId == userId, ct))
        {
            return false;
        }

        var row = new InAppNotification
        {
            OrgId = item.OrgId,
            UserId = userId,
            Type = PushTypes.ApprovalQueueItem,
            EntityId = item.Id,
            DeliveryKey = key,
            CreatedAt = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(raisedAtUtc)),
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
            logger.LogWarning(ex, "Approval alert {ItemId}: the real-time update of the bell failed", item.Id);
        }

        return true;
    }

    // ─── Email slot ───

    /// <summary>
    /// Claims the slot of the person and the alert: true when no e-mail went (or waits) for it. One insert on the unique index <c>(org, user, key)</c>, so two runs at the same
    /// moment queue one e-mail, and an alert delivered again (after another person of the same alert failed) finds the slot taken and e-mails nobody twice.
    /// </summary>
    private async Task<bool> ClaimEmailSlotAsync(ApprovalQueueItem item, DateTime raisedAtUtc, string userId, DateTime atUtc, CancellationToken ct)
    {
        var key = EmailSlotKeyOf(item.Id, raisedAtUtc);
        var at = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(atUtc));
        if (await db.NotificationEmailSlots
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(s => s.OrgId == item.OrgId && s.UserId == userId && s.ThreadKey == key, ct))
        {
            return false;
        }

        var created = new NotificationEmailSlot { OrgId = item.OrgId, UserId = userId, ThreadKey = key, LastEmailAt = at };
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

        await PruneEmailSlotsAsync(item.OrgId, userId, at, ct);
        return true;
    }

    /// <summary>Gives the slot back after an e-mail that could not be queued: the row of this very alert goes, so the alert raised again is free.</summary>
    private async Task ReleaseEmailSlotAsync(ApprovalQueueItem item, DateTime raisedAtUtc, string userId, CancellationToken ct)
    {
        var key = EmailSlotKeyOf(item.Id, raisedAtUtc);
        var claimed = db.NotificationEmailSlots
            .IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == item.OrgId && s.UserId == userId && s.ThreadKey == key);
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

    /// <summary>The key of the slot: the alert, not the item, so an item that goes back to pending is e-mailed again. At most 64 characters.</summary>
    private static string EmailSlotKeyOf(Guid itemId, DateTime raisedAtUtc) =>
        $"approval:{itemId:N}:{ApprovalQueueRules.StampOf(raisedAtUtc)}";

    // ─── The "To do" ───

    /// <summary>
    /// Raises the "To do" of the item once and keeps its id: an item that already has one is left alone (a "To do" the host closed by hand is not opened again by a retry), and
    /// <see cref="IInternalActivityService.CreateAsync"/> returns the open one when the item has none linked yet (a run that stopped between the two).
    /// </summary>
    private async Task EnsureTodoAsync(ApprovalQueueItem item, CancellationToken ct)
    {
        if (item.ActivityId is not null)
            return;

        try
        {
            item.ActivityId = await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.MessageToApprove,
                    Action = ActivityAction.ApproveAiDraft,
                    EntityType = TodoEntityType,
                    EntityId = item.Id,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = item.PropertyId,
                    OrgId = item.OrgId,
                    Deadline = item.ExpiresAt,
                    Reason = item.SituationCode,
                },
                ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // A "To do" that could not be saved must not stay in the tracker, or every later save of this context would try it again.
            item.ActivityId = null;
            foreach (var entry in db.ChangeTracker.Entries<InternalActivity>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            throw;
        }
    }

    // ─── Deferral ───

    private void Schedule(QueuedApprovalAlert queued, DateTime dueUtc)
    {
        var due = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc));
        var jobId = jobs.Schedule<ApprovalAlertJob>(job => job.DeliverAsync(queued, CancellationToken.None), due);
        logger.LogInformation(
            "Approval alert {ItemId} of org {OrgId}: {Channels} held by the quiet hours until {DueAt:O} (job {JobId})",
            queued.ItemId,
            queued.OrgId,
            queued.Channels,
            due,
            jobId);
    }

    // ─── What the alert is about ───

    private Task<ApprovalQueueItem?> LoadPendingAsync(Guid orgId, Guid itemId, CancellationToken ct) =>
        db.ApprovalQueueItems
            .IgnoreQueryFilters([Tenant])
            .FirstOrDefaultAsync(i => i.Id == itemId && i.OrgId == orgId && i.Status == ApprovalStatus.Pending, ct);

    /// <summary>Who is told about an item: the audience of its property (the administrators of the org when it has none), narrowed to who holds <c>approval.decide</c>.</summary>
    private static NotificationEventContext ContextOf(ApprovalQueueItem item, DateTime? atUtc, string? onlyUserId) =>
        new(
            item.OrgId,
            NotificationEventType.AiDraftToApprove,
            item.PropertyId,
            RequiredPermission: new NotificationPermission(OrgRoleCatalog.ShortRent, HostPermissions.ApprovalDecide),
            OnlyUserId: onlyUserId,
            AtUtc: atUtc);

    /// <summary>What the texts of the alert need: the property (never the guest, never the text) and where a tap leads.</summary>
    private async Task<Subject> SubjectOfAsync(ApprovalQueueItem item, CancellationToken ct)
    {
        string? propertyName = null;
        if (item.PropertyId is { } propertyId)
        {
            propertyName = await db.Properties
                .AsNoTracking()
                .IgnoreQueryFilters([Tenant])
                .Where(p => p.Id == propertyId && p.OrgId == item.OrgId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);
        }

        var messageDraft = string.Equals(item.ItemType, ApprovalItemTypes.MessageDraft, StringComparison.Ordinal);
        var route = messageDraft && MessageThreadKey.TryParse(item.ThreadKey, out var key)
            ? PushRoutes.MessageThread(key.ToString())
            : PushRoutes.Approvals();
        return new Subject(propertyName, messageDraft, route);
    }

    /// <param name="PropertyName">The property of the item, <c>null</c> for an item of the org.</param>
    /// <param name="MessageDraft">The item is the draft of a message to a guest.</param>
    /// <param name="Route">Where a tap on the push leads: the conversation of a draft, the approvals for every other item.</param>
    private sealed record Subject(string? PropertyName, bool MessageDraft, string Route);
}
