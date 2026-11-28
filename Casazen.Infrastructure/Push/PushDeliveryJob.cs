using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Push;

/// <summary>
/// Hangfire job that delivers one queued push (MO-04, A6-29): resolves the devices of the audience, claims one
/// <see cref="PushDelivery"/> per device for the event's key, then sends the pending ones to Expo in batches of at most
/// <see cref="ExpoPushOptions.MaxMessagesPerRequest"/> messages and records each ticket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once per event and device.</b> A device already holding a row for the key is never sent again, whatever the
/// retries of this job or the same event queued twice. A batch is marked <see cref="PushDeliveryStatus.Sending"/> before
/// the request: if Expo certainly did not take it (HTTP 429/5xx, no connection) it goes back to
/// <see cref="PushDeliveryStatus.Pending"/> and the job throws so Hangfire retries only what is still pending; if the
/// outcome is unknown (timeout, crash) it stays <c>Sending</c> and is never repeated (at most once).
/// </para>
/// <para>
/// Runs of the same key never overlap (<see cref="DisableConcurrentExecutionAttribute"/> on the key). Logs carry the key,
/// the push type and device registration ids, never a push token or the text.
/// </para>
/// </remarks>
public sealed class PushDeliveryJob(
    AppDbContext db,
    IExpoPushClient expoClient,
    TimeProvider timeProvider,
    ILogger<PushDeliveryJob> logger,
    IContextAuthorizationService? contextAuthorization = null)
{
    public const int MaxAttempts = 5;

    public const string AndroidChannelId = "default";

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution("PushDeliveryJob.SendAsync:{0}", 60)]
    public async Task SendAsync(string deliveryKey, QueuedPush push, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryKey);
        ArgumentNullException.ThrowIfNull(push);

        var devices = await ResolveDevicesAsync(push.ToAudience(), cancellationToken);
        var deliveries = await ClaimAsync(deliveryKey, push.Type, devices, cancellationToken);
        var pending = deliveries
            .Where(d => d.Status == PushDeliveryStatus.Pending)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation(
                "Push {Type} (key {DeliveryKey}): nothing to send ({Devices} devices, {Deliveries} already handled)",
                push.Type,
                deliveryKey,
                devices.Count,
                deliveries.Count);
            return;
        }

        var accepted = 0;
        foreach (var batch in pending.Chunk(ExpoPushOptions.MaxMessagesPerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accepted += await SendBatchAsync(deliveryKey, push, batch, cancellationToken);
        }

        logger.LogInformation(
            "Push {Type} (key {DeliveryKey}): {Accepted} of {Pending} messages accepted by Expo",
            push.Type,
            deliveryKey,
            accepted,
            pending.Count);
    }

    private async Task<int> SendBatchAsync(
        string deliveryKey,
        QueuedPush push,
        IReadOnlyList<PushDelivery> batch,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var delivery in batch)
        {
            delivery.Status = PushDeliveryStatus.Sending;
            delivery.SentAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        var result = await expoClient.SendAsync(batch.Select(d => ToMessage(d.PushToken, push)).ToList(), cancellationToken);
        switch (result.Outcome)
        {
            case ExpoSendOutcome.NotSent:
                foreach (var delivery in batch)
                {
                    delivery.Status = PushDeliveryStatus.Pending;
                    delivery.SentAt = null;
                }

                await db.SaveChangesAsync(CancellationToken.None);
                throw new PushDeliveryException(
                    $"Push {push.Type} (key {deliveryKey}): Expo did not take {batch.Count} messages ({result.Error}), retried by Hangfire");

            case ExpoSendOutcome.Refused:
                foreach (var delivery in batch)
                    Complete(delivery, PushDeliveryStatus.Failed, result.Error);

                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogError(
                    "Push {Type} (key {DeliveryKey}): Expo refused {Count} messages ({Error}), not retried",
                    push.Type,
                    deliveryKey,
                    batch.Count,
                    result.Error);
                return 0;

            case ExpoSendOutcome.Unknown:
                logger.LogWarning(
                    "Push {Type} (key {DeliveryKey}): outcome of {Count} messages unknown ({Error}), not repeated",
                    push.Type,
                    deliveryKey,
                    batch.Count,
                    result.Error);
                return 0;
        }

        var accepted = 0;
        var unregisteredTokens = new List<string>();
        for (var i = 0; i < batch.Count; i++)
        {
            var delivery = batch[i];
            var ticket = result.Tickets[i];
            if (ticket.Ok)
            {
                delivery.Status = PushDeliveryStatus.Accepted;
                delivery.TicketId = ticket.Id;
                accepted++;
                continue;
            }

            Complete(delivery, PushDeliveryStatus.Failed, ticket.Error);
            logger.LogWarning(
                "Push {Type} (key {DeliveryKey}) refused for device registration {DeviceRegistrationId}: {Error}",
                push.Type,
                deliveryKey,
                delivery.DeviceRegistrationId,
                ticket.Error);
            if (ExpoPushErrors.IsDeviceNotRegistered(ticket.Error))
                unregisteredTokens.Add(delivery.PushToken);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        if (unregisteredTokens.Count > 0)
            await PushDeviceCleanup.RemoveUnregisteredAsync(db, unregisteredTokens, logger, CancellationToken.None);

        return accepted;
    }

    private void Complete(PushDelivery delivery, PushDeliveryStatus status, string? error)
    {
        delivery.Status = status;
        delivery.Error = error;
        delivery.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// One row per device for the key: the existing ones (any status) plus a new <see cref="PushDeliveryStatus.Pending"/>
    /// row for each device without one. A concurrent claim of the same key (unique index) is re-read.
    /// </summary>
    private async Task<List<PushDelivery>> ClaimAsync(
        string deliveryKey,
        string type,
        IReadOnlyList<(Guid Id, string PushToken)> devices,
        CancellationToken cancellationToken)
    {
        var existing = await db.PushDeliveries
            .Where(d => d.DeliveryKey == deliveryKey)
            .ToListAsync(cancellationToken);
        var claimed = existing.Select(d => d.PushToken).ToHashSet(StringComparer.Ordinal);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var added = devices
            .Where(d => claimed.Add(d.PushToken))
            .Select(d => new PushDelivery
            {
                DeliveryKey = deliveryKey,
                PushToken = d.PushToken,
                DeviceRegistrationId = d.Id,
                Type = type,
                Status = PushDeliveryStatus.Pending,
                CreatedAt = now,
            })
            .ToList();

        if (added.Count == 0)
            return existing;

        db.PushDeliveries.AddRange(added);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return [.. existing, .. added];
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Push key {DeliveryKey} claimed concurrently: rows re-read", deliveryKey);
            return await db.PushDeliveries
                .Where(d => d.DeliveryKey == deliveryKey)
                .ToListAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<(Guid Id, string PushToken)>> ResolveDevicesAsync(
        PushAudience audience,
        CancellationToken cancellationToken)
    {
        var devices = audience.Kind switch
        {
            PushAudienceKind.BookingHosts => await BookingHostDevicesAsync(audience.Id, cancellationToken),
            PushAudienceKind.PropertyHosts => await PropertyHostDevicesAsync(audience.Id, audience.RequiredPermission, cancellationToken),
            PushAudienceKind.SupplierOrg => await SupplierDevicesAsync(audience.Id, cancellationToken),
            PushAudienceKind.VerificationOperators => await VerificationOperatorDevicesAsync(audience.Id, cancellationToken),
            PushAudienceKind.User => await UserDevicesAsync(audience.Id, audience.UserId, cancellationToken),

            PushAudienceKind.PlatformStaff or PushAudienceKind.OrgAdmins => [],
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience.Kind, "Unknown push audience."),
        };

        if (devices.Count == 0)
            logger.LogInformation("No push device registered for {Audience} {AudienceId}", audience.Kind, audience.Id);

        return devices
            .Where(d => !string.IsNullOrWhiteSpace(d.PushToken))
            .DistinctBy(d => d.PushToken, StringComparer.Ordinal)
            .Select(d => (d.Id, d.PushToken))
            .ToList();
    }

    private async Task<List<DeviceRegistration>> BookingHostDevicesAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OrgId, b.PropertyId, b.Property.OwnerId, b.Property.ResponsibleUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            logger.LogWarning("Push to the hosts of booking {BookingId} skipped: booking not found", bookingId);
            return [];
        }

        return await HostDevicesAsync(booking.OrgId, booking.PropertyId, booking.ResponsibleUserId, booking.OwnerId, cancellationToken);
    }

    private async Task<List<DeviceRegistration>> VerificationOperatorDevicesAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OrgId, b.PropertyId, b.Property.OwnerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            logger.LogWarning("Push to the verification operators of booking {BookingId} skipped: booking not found", bookingId);
            return [];
        }

        return await db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                VerificationOperatorAudience.UsersToTell(db, booking.OrgId, booking.PropertyId, booking.OwnerId),
                device => device.UserId,
                user => user.Id,
                (device, user) => device)
            .Where(device => device.OrgId == booking.OrgId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The devices of one user of a host org (NF-65): the person <c>INotificationPreferenceService</c> selected for the event. Who may hear
    /// about it was decided there; here the person must still be an active account and an active member of the org, and only the devices it
    /// registered in that org are read.
    /// </summary>
    private async Task<List<DeviceRegistration>> UserDevicesAsync(Guid orgId, string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning("Push to one user of org {OrgId} skipped: the audience names no user", orgId);
            return [];
        }

        return await db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                db.Users.AsNoTracking()
                    .Where(u => u.Id == userId
                                && u.OrgId == orgId
                                && u.IsActive
                                && !db.OrgMembers.IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.Status == OrgMemberStatus.Deactivated)),
                device => device.UserId,
                user => user.Id,
                (device, user) => device)
            .Where(device => device.OrgId == orgId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<DeviceRegistration>> PropertyHostDevicesAsync(Guid propertyId, NotificationPermission? requiredPermission, CancellationToken cancellationToken)
    {
        var property = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.OrgId, p.OwnerId, p.ResponsibleUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            logger.LogWarning("Push to the hosts of property {PropertyId} skipped: property not found", propertyId);
            return [];
        }

        if (requiredPermission is null)
            return await HostDevicesAsync(property.OrgId, propertyId, property.ResponsibleUserId, property.OwnerId, cancellationToken);

        return await HostDevicesHoldingAsync(property.OrgId, propertyId, property.ResponsibleUserId, property.OwnerId, requiredPermission, cancellationToken);
    }

    /// <summary>
    /// The devices of the hosts of a property who also hold <paramref name="required"/> (an alert only a person with a permission can act on, <see cref="PushAudience.RequiredPermission"/>): the hosts of
    /// <see cref="HostDevicesAsync"/> narrowed by the one evaluation of a permission (<see cref="HostNotificationAudience.HoldingAsync"/>), the same the job of the bell applies to the same event: the phone
    /// and the bell tell the same people. Without that evaluation (a job built without it) nobody is told: a permission is never taken as held, and the log says so.
    /// </summary>
    private async Task<List<DeviceRegistration>> HostDevicesHoldingAsync(
        Guid orgId,
        Guid propertyId,
        string? responsibleUserId,
        string ownerId,
        NotificationPermission required,
        CancellationToken cancellationToken)
    {
        var hosts = await HostNotificationAudience.UsersToTell(db, orgId, propertyId, responsibleUserId, ownerId)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        if (hosts.Count == 0)
            return [];

        if (contextAuthorization is null)
        {
            logger.LogError("Push skipped: its audience asks the permission {Permission} and no evaluation of permissions is available, so nobody is told", required.PermissionKey);
            return [];
        }

        var holders = await HostNotificationAudience.HoldingAsync(contextAuthorization, hosts, required, cancellationToken);
        if (holders.Count == 0)
            return [];

        return await db.DeviceRegistrations
            .AsNoTracking()
            .Where(device => device.OrgId == orgId && holders.Contains(device.UserId))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    private Task<List<DeviceRegistration>> HostDevicesAsync(
        Guid orgId,
        Guid propertyId,
        string? responsibleUserId,
        string ownerId,
        CancellationToken cancellationToken) =>
        db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                HostNotificationAudience.UsersToTell(db, orgId, propertyId, responsibleUserId, ownerId),
                device => device.UserId,
                user => user.Id,
                (device, user) => device)
            .Where(device => device.OrgId == orgId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

    private Task<List<DeviceRegistration>> SupplierDevicesAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                db.Users.AsNoTracking(),
                device => device.UserId,
                user => user.Id,
                (device, user) => new { Device = device, User = user })
            .Where(x =>
                x.User.IsActive &&
                (x.User.SupplierOrgId == supplierOrgId || x.User.OrgId == supplierOrgId))
            .Select(x => x.Device)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

    private static ExpoPushMessage ToMessage(string pushToken, QueuedPush push)
    {
        var message = new ExpoPushMessage
        {
            To = pushToken,
            Title = push.Title,
            Body = push.Body,
            ChannelId = AndroidChannelId,
            Data = new Dictionary<string, string>
            {
                ["type"] = push.Type,
                ["route"] = push.Route,
            },
        };

        if (push.BookingId is Guid bookingId)
            message.Data["bookingId"] = bookingId.ToString();
        if (push.ServiceRequestId is Guid serviceRequestId)
            message.Data["serviceRequestId"] = serviceRequestId.ToString();

        if (push.ApprovalItemId is Guid approvalItemId)
            message.Data["approvalItemId"] = approvalItemId.ToString();
        if (!string.IsNullOrWhiteSpace(push.CategoryId))
            message.CategoryId = push.CategoryId;

        return message;
    }
}

/// <summary>Expo did not take the messages: rethrown so Hangfire retries the job.</summary>
public sealed class PushDeliveryException(string message) : Exception(message);

public static class ExpoPushErrors
{
    public const string DeviceNotRegistered = "DeviceNotRegistered";

    public static bool IsDeviceNotRegistered(string? error) =>
        string.Equals(error, DeviceNotRegistered, StringComparison.OrdinalIgnoreCase);
}

internal static class PushDeviceCleanup
{
    public static async Task<int> RemoveUnregisteredAsync(
        AppDbContext db,
        IReadOnlyCollection<string> pushTokens,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tokens = pushTokens.Distinct(StringComparer.Ordinal).ToList();
        var devices = await db.DeviceRegistrations
            .Where(d => tokens.Contains(d.PushToken))
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
            return 0;

        db.DeviceRegistrations.RemoveRange(devices);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var device in devices)
        {
            logger.LogInformation(
                "Push device registration {DeviceRegistrationId} removed: Expo reports DeviceNotRegistered",
                device.Id);
        }

        return devices.Count;
    }
}
