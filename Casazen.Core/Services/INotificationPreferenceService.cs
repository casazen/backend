using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// <b>The one place</b> that decides who is told what, and how, for the host events of <see cref="NotificationEventType"/> (E05/F03, NF-65,
/// ADR-025): it keeps what each user chose (<c>GET</c> and <c>PUT api/me/notification-preferences</c>) and turns an event into recipients
/// with their channels (<see cref="ResolveAsync"/>). Every path that notifies a host user about one of the eight events asks it first, so a
/// choice or a scope cannot be forgotten by a path written later: <c>HostNotifyingPathsArchitectureTests</c> lists those paths and fails
/// when one does not go through here. It models <c>SupplierNotificationPreferences</c>.
/// </summary>
/// <remarks>
/// <para><b>The audience</b> of an event about a property is the one of <c>HostNotificationAudience</c> (AM-03): the person in charge while they
/// still reach it, and the administrators of the org; a collaborator «Solo alcuni» is told only about the properties it was given. An event
/// with no property (a conversation not mapped to any) goes to the administrators. A caller can narrow it to who holds a permission.</para>
/// <para><b>The channels</b> are the choice of the user, or the default of PO-10 when it made none. The quiet hours defer the mobile push and
/// the email, never the counter of the bell; a legal deadline overrides them and can never be left without a channel. The email goes to the
/// person's own address, never to the one of the organization.</para>
/// <para>Nothing here writes anything but the choices of the caller and its quiet hours: delivering is up to the caller.</para>
/// </remarks>
public interface INotificationPreferenceService
{
    Task<NotificationPreferencesView> GetAsync(string userId, CancellationToken ct);

    Task<NotificationPreferencesView> SaveAsync(string userId, NotificationPreferencesUpdate update, CancellationToken ct);

    Task<IReadOnlyList<NotificationRecipient>> ResolveAsync(NotificationEventContext context, CancellationToken ct);
}

public sealed record QuietHoursSettings(TimeOnly Start, TimeOnly End, bool LegalAlertsOverride);

public sealed record NotificationEventSetting(NotificationEventType EventType, NotificationChannels Channels, bool IsDefault);

public sealed record NotificationChannelAvailability(NotificationChannels Channel, bool Available, string? UnavailableReason);

public sealed record NotificationPreferencesView(
    QuietHoursSettings QuietHours,
    IReadOnlyList<NotificationEventSetting> Events,
    IReadOnlyList<NotificationChannelAvailability> AvailableChannels);

public sealed record QuietHoursUpdate(TimeOnly Start, TimeOnly End);

public sealed record NotificationEventUpdate(NotificationEventType EventType, NotificationChannels Channels);

public sealed record NotificationPreferencesUpdate(QuietHoursUpdate? QuietHours, IReadOnlyList<NotificationEventUpdate>? Events);

public sealed record NotificationPermission(string ContextKey, string PermissionKey);

/// <summary>
/// An event to resolve (<see cref="INotificationPreferenceService.ResolveAsync"/>).
/// </summary>
/// <param name="OrgId">The org the event belongs to; every read is scoped to it.</param>
/// <param name="EventType">The kind of event.</param>
/// <param name="PropertyId">The property the event is about; <c>null</c> for an event of the org (the administrators are told).</param>
/// <param name="ResponsibleUserId">The member in charge of the property (<c>null</c> = nobody named). Read from the property when <paramref name="CreatorUserId"/> is not given.</param>
/// <param name="CreatorUserId">The creator of the property (<c>Property.OwnerId</c>), who stands in while nobody is in charge. When given, the caller says it knows both.</param>
/// <param name="RequiredPermission">When set, only the people who hold this permission are told.</param>
/// <param name="OnlyUserId">When set, the audience is narrowed to this one user (a deferred alert that checks again before it is delivered).</param>
/// <param name="AtUtc">
/// The instant the event happened, which the quiet hours are judged at; <c>null</c> = now. A handler that gets the event from the outbox gives the
/// instant of the event, so a redelivery splits the channels exactly as the first delivery did.
/// </param>
public sealed record NotificationEventContext(
    Guid OrgId,
    NotificationEventType EventType,
    Guid? PropertyId,
    string? ResponsibleUserId = null,
    string? CreatorUserId = null,
    NotificationPermission? RequiredPermission = null,
    string? OnlyUserId = null,
    DateTime? AtUtc = null);

/// <summary>
/// One person to tell and how.
/// </summary>
/// <param name="UserId">The user (<c>User.Id</c>, the Auth0 subject).</param>
/// <param name="Email">The person's own address; empty when the account has none (the email channel is then never in the channels).</param>
/// <param name="NowChannels">The channels that go at once.</param>
/// <param name="DeferredChannels">The channels that wait for <paramref name="DueAtUtc"/>, because the quiet hours of the person are on.</param>
/// <param name="DueAtUtc">When the deferred channels are due; <c>null</c> when none is deferred.</param>
public sealed record NotificationRecipient(
    string UserId,
    string Email,
    NotificationChannels NowChannels,
    NotificationChannels DeferredChannels,
    DateTime? DueAtUtc);
