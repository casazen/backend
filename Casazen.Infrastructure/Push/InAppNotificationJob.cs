using Casazen.Core.Entities;
using Casazen.Core.Features;
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

    /// <summary>The service request for the <c>service-request-*</c> types, else the booking; <c>null</c> when there is none.</summary>
    public Guid? EntityId { get; set; }

    /// <summary>UTC instant of the event (when the push was queued), so a late run or a retry does not move it.</summary>
    public DateTime OccurredAt { get; set; }

    public PushAudience ToAudience() => new(AudienceKind, AudienceId);

    public static QueuedInAppNotification From(PushAudience audience, PushNotificationPayload payload, DateTime occurredAtUtc) => new()
    {
        AudienceKind = audience.Kind,
        AudienceId = audience.Id,
        Type = payload.Type,
        EntityId = payload.ServiceRequestId ?? payload.BookingId,
        OccurredAt = UtcDateTime.Normalize(occurredAtUtc),
    };
}

/// <summary>
/// Hangfire job that writes the in-app notifications of one event (UI-12a): resolves the <b>users</b> of the audience (the
/// push resolves devices from the same audience), then gives each one a row <see cref="InAppNotification"/> for the delivery
/// key of the event. Queued by <see cref="InAppNotificationPushDecorator"/> next to the push, with
/// <c>Features:InAppNotifications</c> on. Runbook: <c>docs/runbooks/in-app-notifications.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Once per event and user.</b> <c>(DeliveryKey, UserId)</c> is unique. A user who already has a row for the key
/// (a retry of this job, the same event queued twice, a manual <i>Requeue</i> from the dashboard) is skipped; two runs that
/// write the same user at the same moment lose the race on the index (23505), re-read and write what is still missing.
/// Runs of the same key never overlap (<see cref="DisableConcurrentExecutionAttribute"/> on the key), so the index is the net
/// under the lock, not the way the job usually works.</para>
/// <para><b>Who.</b> Exactly the people the push tells, with the same rules (<see cref="HostNotificationAudience"/> for the hosts
/// of a booking or a property, the active users linked to the supplier org for the supplier's inbox), so the bell and the phone
/// cannot disagree. A user without a registered device still gets the notification: the bell does not depend on a phone. An
/// audience with nobody in it (a booking that is gone, an org whose members are all deactivated) writes nothing and is not an error.</para>
/// <para>Runs without a tenant (no HTTP request): every read states the org it means. Logs carry the key, the type, the
/// audience and counts, never a user id or a push text.</para>
/// </remarks>
public sealed class InAppNotificationJob(
    AppDbContext db,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<InAppNotificationJob> logger)
{
    public const int MaxAttempts = 5;

    /// <summary>Times the job reads and writes again after losing a race on the unique index (or a user vanishing) before giving up to Hangfire.</summary>
    private const int MaxWriteAttempts = 3;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution("InAppNotificationJob.CreateAsync:{0}", 60)]
    public async Task CreateAsync(string deliveryKey, QueuedInAppNotification notification, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryKey);
        ArgumentNullException.ThrowIfNull(notification);

        // The flag may have been turned off between the event and this run: nothing is written while it is off.
        if (!featureFlags.IsEnabled(FeatureFlags.InAppNotifications))
        {
            logger.LogInformation(
                "In-app notification {Type} (key {DeliveryKey}) skipped: {Flag} is off", notification.Type, deliveryKey, FeatureFlags.InAppNotifications);
            return;
        }

        var occurredAt = notification.OccurredAt == default
            ? timeProvider.GetUtcNow().UtcDateTime
            : UtcDateTime.Normalize(notification.OccurredAt);
        var createdAt = UtcDateTime.TruncateToMicroseconds(occurredAt);

        for (var attempt = 1; ; attempt++)
        {
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
            var toTell = recipients.Users.Where(userId => !alreadyTold.Contains(userId)).ToList();
            if (toTell.Count == 0)
            {
                logger.LogInformation(
                    "In-app notification {Type} (key {DeliveryKey}): the {Told} users were told already",
                    notification.Type,
                    deliveryKey,
                    alreadyTold.Count);
                return;
            }

            db.InAppNotifications.AddRange(toTell.Select(userId => new InAppNotification
            {
                OrgId = recipients.OrgId,
                UserId = userId,
                Type = notification.Type,
                EntityId = notification.EntityId,
                DeliveryKey = deliveryKey,
                CreatedAt = createdAt,
            }));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "In-app notification {Type} (key {DeliveryKey}) written for {Count} users of {Audience} {AudienceId}",
                    notification.Type,
                    deliveryKey,
                    toTell.Count,
                    notification.AudienceKind,
                    notification.AudienceId);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxWriteAttempts && IsRaceOrVanishedUser(ex))
            {
                // Another run of the same key wrote some of these users first, or an account was deleted since the read: the
                // batch was refused as a whole. Read what is in the table now (and who is left) and write the rest.
                db.ChangeTracker.Clear();
                logger.LogInformation(
                    "In-app notification {Type} (key {DeliveryKey}) written concurrently or a user is gone: read again (attempt {Attempt})",
                    notification.Type,
                    deliveryKey,
                    attempt);
            }
        }
    }

    private static bool IsRaceOrVanishedUser(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation,
        };

    private sealed record Recipients(Guid OrgId, IReadOnlyList<string> Users)
    {
        public static Recipients None { get; } = new(Guid.Empty, []);
    }

    /// <summary>The org the event belongs to and the ids of the users of the audience, each once.</summary>
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
        return new Recipients(orgId, users.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
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
        return new Recipients(supplierOrgId, users.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }
}
