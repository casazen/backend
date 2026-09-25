using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
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
    ILogger<PushDeliveryJob> logger)
{
    public const int MaxAttempts = 5;

    /// <summary>
    /// Android notification channel created by the app before it asks for the permission (MO-03,
    /// <c>mobile/src/notifications/push-registration.ts</c>). Ignored on iOS.
    /// </summary>
    public const string AndroidChannelId = "default";

    private static readonly UserRole[] OrgWideHostRoles = [UserRole.Admin, UserRole.PropertyManager];

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
                // Expo may have taken them: left "Sending" and never repeated (at most once per device and event).
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
            // Another run of the same key claimed some of these devices first: take what is in the table now.
            db.ChangeTracker.Clear();
            logger.LogInformation("Push key {DeliveryKey} claimed concurrently: rows re-read", deliveryKey);
            return await db.PushDeliveries
                .Where(d => d.DeliveryKey == deliveryKey)
                .ToListAsync(cancellationToken);
        }
    }

    /// <summary>Devices of the audience, one per push token.</summary>
    private async Task<IReadOnlyList<(Guid Id, string PushToken)>> ResolveDevicesAsync(
        PushAudience audience,
        CancellationToken cancellationToken)
    {
        var devices = audience.Kind switch
        {
            PushAudienceKind.BookingHosts => await BookingHostDevicesAsync(audience.Id, cancellationToken),
            PushAudienceKind.PropertyHosts => await PropertyHostDevicesAsync(audience.Id, cancellationToken),
            PushAudienceKind.SupplierOrg => await SupplierDevicesAsync(audience.Id, cancellationToken),
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
        // IgnoreQueryFilters: background job without a tenant; the recipients are limited to the booking's org.
        var booking = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.OrgId, b.Property.OwnerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            logger.LogWarning("Push to the hosts of booking {BookingId} skipped: booking not found", bookingId);
            return [];
        }

        return await HostDevicesAsync(booking.OrgId, booking.OwnerId, cancellationToken);
    }

    private async Task<List<DeviceRegistration>> PropertyHostDevicesAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: background job without a tenant; the recipients are limited to the property's org.
        var property = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.OrgId, p.OwnerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            logger.LogWarning("Push to the hosts of property {PropertyId} skipped: property not found", propertyId);
            return [];
        }

        return await HostDevicesAsync(property.OrgId, property.OwnerId, cancellationToken);
    }

    /// <summary>
    /// Hosts of a property: its owner and the org-wide roles (Admin, PropertyManager) of its org, active, with devices
    /// registered in that org (a phone registered under another org does not get this org's pushes).
    /// </summary>
    private Task<List<DeviceRegistration>> HostDevicesAsync(Guid orgId, string ownerId, CancellationToken cancellationToken) =>
        db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                db.Users.AsNoTracking(),
                device => device.UserId,
                user => user.Id,
                (device, user) => new { Device = device, User = user })
            .Where(x =>
                x.Device.OrgId == orgId &&
                x.User.OrgId == orgId &&
                x.User.IsActive &&
                (x.User.Id == ownerId || OrgWideHostRoles.Contains(x.User.Role)))
            .Select(x => x.Device)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Active users who operate the supplier org's inbox (linked by <c>User.SupplierOrgId</c>, or members whose org is
    /// the supplier org), every device they registered.
    /// </summary>
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

        return message;
    }
}

/// <summary>Expo did not take the messages: rethrown so Hangfire retries the job.</summary>
public sealed class PushDeliveryException(string message) : Exception(message);

/// <summary>Expo error codes the backend acts on.</summary>
public static class ExpoPushErrors
{
    /// <summary>The token is no longer valid (app uninstalled, token rotated): its device registration is removed.</summary>
    public const string DeviceNotRegistered = "DeviceNotRegistered";

    public static bool IsDeviceNotRegistered(string? error) =>
        string.Equals(error, DeviceNotRegistered, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Removal of the device registrations whose token Expo reports as <c>DeviceNotRegistered</c>.</summary>
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
