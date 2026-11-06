using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAlertRouter" />
/// <remarks>
/// <list type="bullet">
/// <item><b>The org of the row</b> is the host org of the alerted user (<see cref="CallerTenantQuery"/>, the query behind the request
/// tenant): the router runs for a user from a handler or a job, where there is no request tenant. Every read and write of the alerts and
/// of the preferences names that org and the user explicitly (<c>IgnoreQueryFilters</c>), so the router behaves the same inside a request
/// and outside one. A user with no host org, a deactivated account or a deactivated member is told nothing.</item>
/// <item><b>Quiet hours.</b> The preferences of the user (created with the defaults when missing) say when they run, on the clock of
/// Europe/Rome (<see cref="RomeCalendar"/>). The alert is stored in every case, because an alert deferred by the quiet hours would
/// otherwise be lost: held back, it carries <c>RemindAt</c> = the instant the quiet hours end (<c>RomeCalendar.ToUtc</c>, which also
/// answers on the two days the clock changes) and nothing is sent now; the job <c>popup-alert-due</c> pushes it then. Otherwise it is
/// stored as delivered and published at once.</item>
/// <item><b>Legal alerts</b> (<see cref="PopupAlert.IsLegal"/>: Alloggiati, ISTAT) always bypass the quiet hours (ADR-025-R3). The
/// <c>LegalAlertsOverrideQuietHours</c> field of the preferences is not consulted: the rule is not a preference.</item>
/// <item><b>Open once.</b> While an alert for the same user, type and entity is not dismissed, a second call changes nothing.</item>
/// <item>No user id and no text in the logs: type, entity and org only.</item>
/// </list>
/// </remarks>
public sealed class AlertRouter(
    AppDbContext db,
    IPopupAlertPublisher publisher,
    TimeProvider timeProvider,
    ILogger<AlertRouter> logger) : IAlertRouter
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    public async Task RouteAlertAsync(
        AlertType type,
        Guid entityId,
        string entityType,
        string userId,
        AlertUrgency urgency,
        DateTime? deadline,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(type) || !Enum.IsDefined(urgency))
            throw new ArgumentException("The type and the urgency of an alert must be declared members.", nameof(type));
        if (entityId == Guid.Empty)
            throw new ArgumentException("An alert names the entity it is about.", nameof(entityId));
        var kind = Bounded(entityType, PopupAlert.EntityTypeMaxLength, nameof(entityType));
        var user = Bounded(userId, PopupAlert.UserIdMaxLength, nameof(userId));

        var caller = await CallerTenantQuery.For(db, user).FirstOrDefaultAsync(ct);
        if (caller?.OrgId is not Guid orgId || !caller.IsActive || caller.MemberStatus == OrgMemberStatus.Deactivated)
        {
            logger.LogInformation(
                "Popup alert {AlertType} on {EntityType} {EntityId} was not routed: its user has no active host org",
                type,
                kind,
                entityId);
            return;
        }

        if (await IsOpenAsync(orgId, user, type, kind, entityId, ct))
        {
            logger.LogDebug("Popup alert {AlertType} on {EntityType} {EntityId} is already open: not written twice", type, kind, entityId);
            return;
        }

        var preferences = await PreferencesAsync(orgId, user, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var holdUntil = HoldUntil(type, preferences, now);

        var alert = new PopupAlert
        {
            OrgId = orgId,
            UserId = user,
            AlertType = type,
            EntityType = kind,
            EntityId = entityId,
            Urgency = urgency,
            CreatedAt = now,
            Deadline = deadline is { } due ? AsUtc(due) : null,
            RemindAt = holdUntil,
            DeliveredAt = holdUntil is null ? now : null,
        };
        db.PopupAlerts.Add(alert);
        await db.SaveChangesAsync(ct);

        if (holdUntil is { } until)
        {
            logger.LogInformation(
                "Popup alert {AlertId} ({AlertType} on {EntityType} {EntityId}) is held back until the quiet hours end at {RemindAt:O}",
                alert.Id,
                type,
                kind,
                entityId,
                until);
            return;
        }

        // The alert is stored: a push that fails (the publisher never throws) is only a hint lost, the page load reads it anyway.
        await publisher.PublishAsync(user, alert.Id, type, kind, entityId, urgency, ct);
    }

    /// <summary>
    /// The instant a new alert has to wait for, or <c>null</c> to deliver it now: the end of the quiet hours of the user when the alert
    /// is not legal and <paramref name="now"/> is inside them.
    /// </summary>
    private static DateTime? HoldUntil(AlertType type, UserNotificationPreferences preferences, DateTime now)
    {
        if (PopupAlert.IsLegal(type))
            return null;

        // The one definition of the quiet hours, shared with the preferences of the notifications (NF-65).
        return NotificationPreferenceRules.QuietHoursEndUtc(preferences.QuietHoursStart, preferences.QuietHoursEnd, now);
    }

    private Task<bool> IsOpenAsync(Guid orgId, string userId, AlertType type, string entityType, Guid entityId, CancellationToken ct) =>
        db.PopupAlerts
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId
                     && a.UserId == userId
                     && a.AlertType == type
                     && a.EntityType == entityType
                     && a.EntityId == entityId
                     && a.DismissedAt == null,
                ct);

    /// <summary>The preferences of the user in the org, created with the defaults (22:00-08:00) when there are none yet.</summary>
    private async Task<UserNotificationPreferences> PreferencesAsync(Guid orgId, string userId, CancellationToken ct)
    {
        if (await ReadPreferences(orgId, userId).FirstOrDefaultAsync(ct) is { } existing)
            return existing;

        var created = new UserNotificationPreferences { OrgId = orgId, UserId = userId };
        db.UserNotificationPreferences.Add(created);
        try
        {
            await db.SaveChangesAsync(ct);
            return created;
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, UserNotificationPreferences.OncePerUserIndexName))
        {
            // A parallel call created the row first (UIX_UserNotificationPreferences_OrgId_UserId): that row is the one.
            db.Entry(created).State = EntityState.Detached;
            return await ReadPreferences(orgId, userId).FirstAsync(ct);
        }
    }

    private IQueryable<UserNotificationPreferences> ReadPreferences(Guid orgId, string userId) =>
        db.UserNotificationPreferences
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.UserId == userId);

    /// <summary>The value trimmed; blank or longer than <paramref name="maxLength"/> is a programming error of the caller.</summary>
    private static string Bounded(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException($"{name} is required.", name);

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"{name} is longer than {maxLength} characters.", name);
    }

    /// <summary>A deadline as a UTC instant: a value without a kind counts as UTC (the storage convention), a local one is converted.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}
