using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Push;

/// <summary>
/// Arguments of a queued in-app notification (Hangfire job argument): who is told (the audience of the push of the same
/// event), what kind of event it is and which booking or service request it is about. <b>No text and no name</b>: the
/// push text (property, dates, category) is not copied here, and the users are resolved by <see cref="InAppNotificationJob"/>
/// when it runs, not when the event happens.
/// </summary>
public sealed class QueuedInAppNotification
{
    public PushAudienceKind AudienceKind { get; set; }

    public Guid AudienceId { get; set; }

    /// <summary>A value of <c>PushTypes</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The service request for the <c>service-request-*</c> types, the subject of the event for the platform staff (a «Fallo per me» request),
    /// else the booking; <c>null</c> when there is none.
    /// </summary>
    public Guid? EntityId { get; set; }

    /// <summary>UTC instant of the event (when the push was queued), so a late run or a retry does not move it.</summary>
    public DateTime OccurredAt { get; set; }

    public PushAudience ToAudience() => new(AudienceKind, AudienceId);

    public static QueuedInAppNotification From(PushAudience audience, PushNotificationPayload payload, DateTime occurredAtUtc) => new()
    {
        AudienceKind = audience.Kind,
        AudienceId = audience.Id,
        Type = payload.Type,

        // The audience of the platform staff names the subject of the event itself (PushAudience.PlatformStaff): that is what the bell opens.
        EntityId = audience.Kind == PushAudienceKind.PlatformStaff ? audience.Id : payload.ServiceRequestId ?? payload.BookingId,
        OccurredAt = UtcDateTime.Normalize(occurredAtUtc),
    };
}

/// <summary>
/// Hangfire job that writes the in-app notifications of one event (UI-12a): resolves the <b>users</b> of the audience (the
/// push resolves devices from the same audience), then gives each one a row <see cref="InAppNotification"/> for the delivery
/// key of the event. Queued by <see cref="InAppNotificationPushDecorator"/> next to the push.
/// Runbook: <c>docs/runbooks/in-app-notifications.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Once per event and user.</b> <c>(DeliveryKey, UserId)</c> is unique. A user who already has a row for the key
/// (a retry of this job, the same event queued twice, a manual <i>Requeue</i> from the dashboard) is skipped; two runs that
/// write the same user at the same moment lose the race on the index (23505) for that user and go on with the next one; each user is written
/// with a save of its own, in a fixed order, so two runs never wait for each other's rows in a cycle (a batch deadlocked, 40P01).
/// Runs of the same key never overlap (<see cref="DisableConcurrentExecutionAttribute"/> on the key), so the index is the net
/// under the lock, not the way the job usually works.</para>
/// <para><b>Who.</b> Exactly the people the push tells, with the same rules (<see cref="HostNotificationAudience"/> for the hosts
/// of a booking or a property, the active users linked to the supplier org for the supplier's inbox), so the bell and the phone
/// cannot disagree. A user without a registered device still gets the notification: the bell does not depend on a phone. An
/// audience with nobody in it (a booking that is gone, an org whose members are all deactivated) writes nothing and is not an error.
/// The platform staff (<see cref="PushAudienceKind.PlatformStaff"/>, <see cref="PlatformAdministrators"/>) has no push on a phone: the
/// bell is its only channel, and each of its rows carries the staff member's own org, the one its bell reads, never the org of the event.</para>
/// <para>Runs without a tenant (no HTTP request): every read states the org it means. Logs carry the key, the type, the
/// audience and counts, never a user id or a push text.</para>
/// </remarks>
public sealed class InAppNotificationJob(
    AppDbContext db,
    TimeProvider timeProvider,
    IInAppNotificationPublisher publisher,
    ILogger<InAppNotificationJob> logger)
{
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution("InAppNotificationJob.CreateAsync:{0}", 60)]
    public async Task CreateAsync(string deliveryKey, QueuedInAppNotification notification, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryKey);
        ArgumentNullException.ThrowIfNull(notification);

        var occurredAt = notification.OccurredAt == default
            ? timeProvider.GetUtcNow().UtcDateTime
            : UtcDateTime.Normalize(notification.OccurredAt);
        var createdAt = UtcDateTime.TruncateToMicroseconds(occurredAt);

        var recipients = await ResolveRecipientsAsync(notification, cancellationToken);
        if (recipients.Users.Count == 0)
        {
            logger.LogInformation(
                "In-app notification {Type} (key {DeliveryKey}): nobody to tell for {Audience} {AudienceId}",
                notification.Type,
                deliveryKey,
                notification.AudienceKind,
                notification.AudienceId);
            return;
        }

        // IgnoreQueryFilters([Tenant]): a job without a tenant; the key identifies the event, whatever its org.
        var alreadyTold = (await db.InAppNotifications
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(n => n.DeliveryKey == deliveryKey)
                .Select(n => n.UserId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var toTell = recipients.Users
            .Where(recipient => !alreadyTold.Contains(recipient.UserId))
            .OrderBy(recipient => recipient.UserId, StringComparer.Ordinal)
            .ToList();
        if (toTell.Count == 0)
        {
            logger.LogInformation(
                "In-app notification {Type} (key {DeliveryKey}): the {Told} users were told already",
                notification.Type,
                deliveryKey,
                alreadyTold.Count);
            return;
        }

        // One row per SaveChanges. A batch of several rows takes its index entries in the order EF sorts the commands (by row id,
        // not by user), so two runs of the same key could each hold a row the other needs. Even one row at a time, several runs
        // that insert the same user together deadlock on the unique index (40P01). Npgsql reports that as a transient failure
        // wrapped in InvalidOperationException, which a catch of DbUpdateException alone does not see. Retry that user: the run
        // that won has committed, so the next attempt is the unique violation (23505) and is skipped.
        var written = 0;
        var writtenUsers = new List<string>();
        foreach (var recipient in toTell)
        {
            for (var attempt = 1; ; attempt++)
            {
                db.InAppNotifications.Add(new InAppNotification
                {
                    OrgId = recipient.OrgId,
                    UserId = recipient.UserId,
                    Type = notification.Type,
                    EntityId = notification.EntityId,
                    DeliveryKey = deliveryKey,
                    CreatedAt = createdAt,
                });

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    written++;
                    writtenUsers.Add(recipient.UserId);
                    break;
                }
                catch (Exception ex) when (IsRaceOrVanishedUser(ex))
                {
                    // Another run of the same key wrote this user first (23505), or the account was deleted since the read (23503):
                    // there is nothing left to write for them.
                    db.ChangeTracker.Clear();
                    break;
                }
                catch (Exception ex) when (IsDeadlock(ex) && attempt < DeadlockAttempts)
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        // Real time (UI-12a): the open sessions of each user written now learn the new count. Best effort, after the rows are stored.
        foreach (var userId in writtenUsers)
        {
            try
            {
                await publisher.PublishAsync(userId, notification.Type, notification.EntityId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "In-app notification {Type} (key {DeliveryKey}): the real-time update failed", notification.Type, deliveryKey);
            }
        }

        logger.LogInformation(
            "In-app notification {Type} (key {DeliveryKey}) written for {Count} of {Candidates} users of {Audience} {AudienceId}",
            notification.Type,
            deliveryKey,
            written,
            toTell.Count,
            notification.AudienceKind,
            notification.AudienceId);
    }

    /// <summary>How many times one user is inserted again after PostgreSQL aborts the statement with 40P01.</summary>
    private const int DeadlockAttempts = 4;

    private static bool IsRaceOrVanishedUser(Exception ex) =>
        PostgresState(ex) is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation;

    private static bool IsDeadlock(Exception ex) => PostgresState(ex) == PostgresErrorCodes.DeadlockDetected;

    private static string? PostgresState(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres.SqlState;
        }

        return null;
    }

    /// <summary>One user to tell, with the org its row belongs to (the org its bell reads it under).</summary>
    private sealed record Recipient(string UserId, Guid OrgId);

    private sealed record Recipients(IReadOnlyList<Recipient> Users)
    {
        public static Recipients None { get; } = new([]);

        /// <summary>Users of the org the event belongs to, each once.</summary>
        public static Recipients Of(Guid orgId, IEnumerable<string> userIds) =>
            new([.. userIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(userId => new Recipient(userId, orgId))]);
    }

    /// <summary>The users of the audience, each once, with the org of each row: the org the event belongs to, or the staff member's own one.</summary>
    private async Task<Recipients> ResolveRecipientsAsync(QueuedInAppNotification notification, CancellationToken cancellationToken)
    {
        switch (notification.AudienceKind)
        {
            case PushAudienceKind.BookingHosts:
                return await BookingHostsAsync(notification.AudienceId, cancellationToken);
            case PushAudienceKind.PropertyHosts:
                return await PropertyHostsAsync(notification.AudienceId, cancellationToken);
            case PushAudienceKind.SupplierOrg:
                return await SupplierUsersAsync(notification.AudienceId, cancellationToken);
            case PushAudienceKind.VerificationOperators:
                return await VerificationOperatorsAsync(notification.AudienceId, cancellationToken);
            case PushAudienceKind.PlatformStaff:
                return await PlatformStaffAsync(cancellationToken);
            default:
                // A new kind of audience the push knows and this job does not: nothing to retry, the log says which.
                logger.LogError(
                    "In-app notification {Type}: no rule for the audience {Audience}, nobody is told",
                    notification.Type,
                    notification.AudienceKind);
                return Recipients.None;
        }
    }

    private async Task<Recipients> BookingHostsAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: background job without a tenant; the recipients are limited to the booking's org.
        var booking = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OrgId, b.PropertyId, b.Property.OwnerId, b.Property.ResponsibleUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            logger.LogWarning("In-app notification to the hosts of booking {BookingId} skipped: booking not found", bookingId);
            return Recipients.None;
        }

        return await HostsAsync(booking.OrgId, booking.PropertyId, booking.ResponsibleUserId, booking.OwnerId, cancellationToken);
    }

    private async Task<Recipients> PropertyHostsAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: background job without a tenant; the recipients are limited to the property's org.
        var property = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.OrgId, p.OwnerId, p.ResponsibleUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            logger.LogWarning("In-app notification to the hosts of property {PropertyId} skipped: property not found", propertyId);
            return Recipients.None;
        }

        return await HostsAsync(property.OrgId, propertyId, property.ResponsibleUserId, property.OwnerId, cancellationToken);
    }

    /// <summary>
    /// The people the push tells about a property of the org (AM-03, AM-03b): the member in charge, while they still reach the
    /// property, and the org's administrators, active and not deactivated (<see cref="HostNotificationAudience"/>), the same query
    /// the delivery job joins to the devices.
    /// </summary>
    /// <summary>
    /// The users who decide the identity verification of the booking's guest (E03/F04/US02/T03): <c>verification.decision</c> and the property
    /// in reach (<see cref="VerificationOperatorAudience"/>), the same people the push job reads the devices of.
    /// </summary>
    private async Task<Recipients> VerificationOperatorsAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: background job without a tenant; the recipients are limited to the booking's org.
        var booking = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OrgId, b.PropertyId, b.Property.OwnerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            logger.LogWarning("In-app notification to the verification operators of booking {BookingId} skipped: booking not found", bookingId);
            return Recipients.None;
        }

        var users = await VerificationOperatorAudience.UsersToTell(db, booking.OrgId, booking.PropertyId, booking.OwnerId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return Recipients.Of(booking.OrgId, users);
    }

    /// <summary>
    /// The platform administrators (<see cref="PlatformAdministrators"/>: the user role <c>Admin</c> or a membership of the administration context),
    /// active, each with its <b>own</b> org: its host org, else its supplier org. The bell lists only the rows of the user's own org
    /// (<c>InAppNotificationService.OwnNotifications</c>), so a row under the org of the event (the host who asked) would never show; a staff account
    /// in no org has no bell to write to and is left out.
    /// </summary>
    private async Task<Recipients> PlatformStaffAsync(CancellationToken cancellationToken)
    {
        var staff = await PlatformAdministrators.Of(db)
            .AsNoTracking()
            .Where(u => u.IsActive && (u.OrgId != null || u.SupplierOrgId != null))
            .Select(u => new { u.Id, OrgId = u.OrgId ?? u.SupplierOrgId })
            .ToListAsync(cancellationToken);
        return new Recipients(
        [
            .. staff
                .DistinctBy(u => u.Id, StringComparer.Ordinal)
                .OrderBy(u => u.Id, StringComparer.Ordinal)
                .Select(u => new Recipient(u.Id, u.OrgId!.Value)),
        ]);
    }

    private async Task<Recipients> HostsAsync(
        Guid orgId,
        Guid propertyId,
        string? responsibleUserId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var users = await HostNotificationAudience.UsersToTell(db, orgId, propertyId, responsibleUserId, ownerId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return Recipients.Of(orgId, users);
    }

    /// <summary>
    /// The active users who operate the supplier org's inbox (linked by <c>User.SupplierOrgId</c>, or members whose org is the
    /// supplier org): the people whose devices the delivery job reads for the same audience. The rows belong to the supplier org.
    /// </summary>
    private async Task<Recipients> SupplierUsersAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && (u.SupplierOrgId == supplierOrgId || u.OrgId == supplierOrgId))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return Recipients.Of(supplierOrgId, users);
    }
}
