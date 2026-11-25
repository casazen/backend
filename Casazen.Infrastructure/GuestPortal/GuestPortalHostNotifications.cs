using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class GuestPortalHostNotifications(
    AppDbContext db,
    INotificationPreferenceService preferences,
    IInAppNotificationPublisher publisher,
    TimeProvider clock)
{
    public async Task NotifyAsync(Guid orgId, Guid propertyId, Guid bookingId, Guid requestId, string type,
        string requiredPermission, CancellationToken ct)
    {
        var recipients = await preferences.ResolveAsync(new NotificationEventContext(orgId,
            NotificationEventType.NewGuestMessage, propertyId,
            RequiredPermission: new NotificationPermission(OrgRoleCatalog.ShortRent, requiredPermission)), ct);

        var key = $"{type}:{requestId:N}";
        foreach (var recipient in recipients.Where(r => r.NowChannels.HasFlag(NotificationChannels.CounterOnly)))
        {
            if (await db.InAppNotifications.AsNoTracking()
                .AnyAsync(n => n.OrgId == orgId && n.UserId == recipient.UserId && n.DeliveryKey == key, ct))
                continue;

            var notification = new InAppNotification
            {
                OrgId = orgId,
                UserId = recipient.UserId,
                Type = type,
                EntityId = bookingId,
                DeliveryKey = key,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.InAppNotifications.Add(notification);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException error) when (OrgTeamAccess.IsUniqueViolation(error, InAppNotification.OncePerEventAndUserIndexName))
            {
                db.Entry(notification).State = EntityState.Detached;
                continue;
            }

            await publisher.PublishAsync(recipient.UserId, type, bookingId, ct);
        }
    }
}
