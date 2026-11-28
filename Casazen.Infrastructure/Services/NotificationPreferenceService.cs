using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="INotificationPreferenceService"/>
/// <remarks>
/// <para><b>Where it reads.</b> The rows of a user are read by org and user, never by the request tenant: <see cref="ResolveAsync"/> runs in the
/// event bus and in jobs, with no HTTP request, and <see cref="GetAsync"/> and <see cref="SaveAsync"/> take the user from the token and find its
/// host org themselves (<see cref="CallerTenantQuery"/>), like the router of the popup alerts. A user with no active host org has no
/// settings of its own: it gets the defaults and nothing is ever written for it.</para>
/// <para><b>The audience</b> is <see cref="HostNotificationAudience"/>, the one query that tells who may hear about a property (and who
/// reaches it: a collaborator «Solo alcuni» only the properties it was given, in the area it works in), or its administrators when the event has no
/// property. A permission, when the caller names one, is checked per person against the contexts of the person
/// (<see cref="IContextAuthorizationService"/>), so a role that cannot open the thing is not told about it.</para>
/// <para><b>Logs</b> carry the org, the kind of event and counts: never a user id, an address or a text.</para>
/// </remarks>
public sealed class NotificationPreferenceService(
    AppDbContext db,
    IContextAuthorizationService contextAuthorization,
    TimeProvider timeProvider,
    ILogger<NotificationPreferenceService> logger) : INotificationPreferenceService
{
    public const string WebPushNotAvailableReason = "web_push_not_available";

    public const string EmptyUpdateCode = "notification_preferences_empty";

    public const string DuplicateEventCode = "notification_event_duplicated";

    private const string Tenant = AppDbContext.TenantQueryFilter;

    private const int SaveAttempts = 3;

    public async Task<NotificationPreferencesView> GetAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (await HostOrgOfAsync(userId, ct) is not { } orgId)
            return BuildView(null, []);

        var preferences = await ReadPreferences(orgId, [userId]).FirstOrDefaultAsync(ct);
        var rows = await ReadSettings(orgId, [userId], null).ToListAsync(ct);
        return BuildView(preferences, rows);
    }

    public async Task<NotificationPreferencesView> SaveAsync(string userId, NotificationPreferencesUpdate update, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(update);

        var events = update.Events ?? [];
        if (update.QuietHours is null && events.Count == 0)
            throw new DomainRuleException(EmptyUpdateCode, "NotificationPreferencesEmpty");

        if (events.Select(e => e.EventType).Distinct().Count() != events.Count)
            throw new DomainRuleException(DuplicateEventCode, "NotificationEventDuplicated");

        foreach (var entry in events)
        {
            if (!Enum.IsDefined(entry.EventType) || !NotificationPreferenceRules.IsDeclared(entry.Channels))
                throw new DomainRuleException(NotificationPreferenceRules.ChannelUnavailableCode, "NotificationChannelUnavailable");

            switch (NotificationPreferenceRules.ValidateSave(entry.EventType, entry.Channels))
            {
                case NotificationPreferenceRules.LegalChannelRequiredCode:
                    throw new DomainRuleException(NotificationPreferenceRules.LegalChannelRequiredCode, "NotificationLegalChannelRequired");
                case NotificationPreferenceRules.ChannelUnavailableCode:
                    throw new DomainRuleException(NotificationPreferenceRules.ChannelUnavailableCode, "NotificationChannelUnavailable");
            }
        }

        var orgId = await HostOrgOfAsync(userId, ct)
                    ?? throw new NotFoundException("Notification preferences not found.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAsync(orgId, userId, update.QuietHours, events, ct);
                break;
            }
            catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex) && attempt < SaveAttempts)
            {
                db.ChangeTracker.Clear();
            }
        }

        return await GetAsync(userId, ct);
    }

    public async Task<IReadOnlyList<NotificationRecipient>> ResolveAsync(NotificationEventContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.OrgId == Guid.Empty || !Enum.IsDefined(context.EventType))
            throw new ArgumentException("An event names its org and a declared kind of event.", nameof(context));

        var candidates = await AudienceAsync(context, ct);
        if (candidates.Count > 0 && context.RequiredPermission is { } required)
            candidates = await HoldingAsync(candidates, required, ct);

        if (candidates.Count == 0)
            return [];

        var ids = candidates.Select(c => c.UserId).ToList();
        var chosen = await ReadSettings(context.OrgId, ids, context.EventType).ToDictionaryAsync(s => s.UserId, s => s.Channels, StringComparer.Ordinal, ct);
        var quiet = await ReadPreferences(context.OrgId, ids).ToDictionaryAsync(p => p.UserId, StringComparer.Ordinal, ct);
        var now = context.AtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : timeProvider.GetUtcNow().UtcDateTime;

        var recipients = new List<NotificationRecipient>();
        foreach (var candidate in candidates.OrderBy(c => c.UserId, StringComparer.Ordinal))
        {
            var channels = NotificationPreferenceRules.EffectiveChannelsOf(
                context.EventType,
                chosen.TryGetValue(candidate.UserId, out var saved) ? saved : null);

            if (string.IsNullOrWhiteSpace(candidate.Email))
                channels &= ~NotificationChannels.Email;

            quiet.TryGetValue(candidate.UserId, out var hours);
            var decision = NotificationPreferenceRules.Decide(
                context.EventType,
                channels,
                hours?.QuietHoursStart ?? UserNotificationPreferences.DefaultQuietHoursStart,
                hours?.QuietHoursEnd ?? UserNotificationPreferences.DefaultQuietHoursEnd,
                hours?.LegalAlertsOverrideQuietHours ?? true,
                now);
            if (decision.Now == NotificationChannels.None && decision.Deferred == NotificationChannels.None)
                continue;

            recipients.Add(new NotificationRecipient(candidate.UserId, candidate.Email.Trim(), decision.Now, decision.Deferred, decision.DueAtUtc));
        }

        logger.LogInformation(
            "Notification {EventType} of org {OrgId}: {Candidates} people in the audience, {Recipients} to tell",
            context.EventType,
            context.OrgId,
            candidates.Count,
            recipients.Count);
        return recipients;
    }

    private async Task WriteAsync(
        Guid orgId,
        string userId,
        QuietHoursUpdate? quietHours,
        IReadOnlyList<NotificationEventUpdate> events,
        CancellationToken ct)
    {
        var now = UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);

        if (quietHours is not null)
        {
            var start = new TimeOnly(quietHours.Start.Hour, quietHours.Start.Minute);
            var end = new TimeOnly(quietHours.End.Hour, quietHours.End.Minute);
            var preferences = await ReadPreferences(orgId, [userId], tracked: true).FirstOrDefaultAsync(ct);
            if (preferences is null)
            {
                preferences = new UserNotificationPreferences { OrgId = orgId, UserId = userId };
                db.UserNotificationPreferences.Add(preferences);
            }

            preferences.QuietHoursStart = start;
            preferences.QuietHoursEnd = end;
        }

        if (events.Count > 0)
        {
            var existing = await ReadSettings(orgId, [userId], null, tracked: true).ToListAsync(ct);
            foreach (var entry in events)
            {
                var row = existing.FirstOrDefault(s => s.EventType == entry.EventType);
                if (row is null)
                {
                    db.UserNotificationChannelSettings.Add(new UserNotificationChannelSetting
                    {
                        OrgId = orgId,
                        UserId = userId,
                        EventType = entry.EventType,
                        Channels = entry.Channels,
                        UpdatedAt = now,
                    });
                    continue;
                }

                row.Channels = entry.Channels;
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private sealed record Candidate(string UserId, string Email);

    private async Task<List<Candidate>> AudienceAsync(NotificationEventContext context, CancellationToken ct)
    {
        IQueryable<User> audience;
        if (context.PropertyId is not { } propertyId)
        {
            audience = HostNotificationAudience.AdministratorsToTell(db, context.OrgId);
        }
        else
        {
            var responsible = context.ResponsibleUserId;
            var creator = context.CreatorUserId;
            if (creator is null)
            {
                var property = await db.Properties
                    .AsNoTracking()
                    .IgnoreQueryFilters([Tenant])
                    .Where(p => p.Id == propertyId && p.OrgId == context.OrgId)
                    .Select(p => new { p.OwnerId, p.ResponsibleUserId })
                    .FirstOrDefaultAsync(ct);
                if (property is null)
                {
                    logger.LogInformation(
                        "Notification {EventType} of org {OrgId}: the property {PropertyId} is not there, nobody is told",
                        context.EventType,
                        context.OrgId,
                        propertyId);
                    return [];
                }

                creator = property.OwnerId;
                responsible = property.ResponsibleUserId;
            }

            audience = HostNotificationAudience.UsersToTell(db, context.OrgId, propertyId, responsible, creator);
        }

        if (context.OnlyUserId is { } only)
            audience = audience.Where(u => u.Id == only);

        var rows = await audience.Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        return rows
            .DistinctBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new Candidate(r.Id, r.Email))
            .ToList();
    }

    private async Task<List<Candidate>> HoldingAsync(List<Candidate> candidates, NotificationPermission required, CancellationToken ct)
    {
        var holders = (await HostNotificationAudience.HoldingAsync(contextAuthorization, candidates.Select(c => c.UserId), required, ct)).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(candidate => holders.Contains(candidate.UserId)).ToList();
    }

    private async Task<Guid?> HostOrgOfAsync(string userId, CancellationToken ct)
    {
        var caller = await CallerTenantQuery.For(db, userId).FirstOrDefaultAsync(ct);
        return caller is { OrgId: { } orgId, IsActive: true } && caller.MemberStatus != OrgMemberStatus.Deactivated ? orgId : null;
    }

    private IQueryable<UserNotificationPreferences> ReadPreferences(Guid orgId, IReadOnlyCollection<string> userIds, bool tracked = false)
    {
        var rows = db.UserNotificationPreferences.IgnoreQueryFilters([Tenant]);
        return (tracked ? rows : rows.AsNoTracking()).Where(p => p.OrgId == orgId && userIds.Contains(p.UserId));
    }

    private IQueryable<UserNotificationChannelSetting> ReadSettings(
        Guid orgId,
        IReadOnlyCollection<string> userIds,
        NotificationEventType? eventType,
        bool tracked = false)
    {
        var rows = db.UserNotificationChannelSettings.IgnoreQueryFilters([Tenant]);
        var query = (tracked ? rows : rows.AsNoTracking()).Where(s => s.OrgId == orgId && userIds.Contains(s.UserId));
        return eventType is { } type ? query.Where(s => s.EventType == type) : query;
    }

    private static NotificationPreferencesView BuildView(
        UserNotificationPreferences? preferences,
        IReadOnlyList<UserNotificationChannelSetting> rows)
    {
        var quiet = new QuietHoursSettings(
            preferences?.QuietHoursStart ?? UserNotificationPreferences.DefaultQuietHoursStart,
            preferences?.QuietHoursEnd ?? UserNotificationPreferences.DefaultQuietHoursEnd,
            preferences?.LegalAlertsOverrideQuietHours ?? true);

        var events = NotificationPreferenceRules.EventTypes
            .Select(type =>
            {
                var row = rows.FirstOrDefault(r => r.EventType == type);
                return new NotificationEventSetting(
                    type,
                    NotificationPreferenceRules.EffectiveChannelsOf(type, row?.Channels),
                    row is null);
            })
            .ToList();

        var channels = NotificationPreferenceRules.Channels
            .Select(channel => NotificationPreferenceRules.IsAvailable(channel)
                ? new NotificationChannelAvailability(channel, true, null)
                : new NotificationChannelAvailability(channel, false, WebPushNotAvailableReason))
            .ToList();

        return new NotificationPreferencesView(quiet, events, channels);
    }
}
