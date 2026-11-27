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
    /// <summary>
    /// The caller's own settings: its quiet hours, one entry for each kind of event (what it chose, or the default, marked as such) and the
    /// channels that can deliver today. A user with no host org gets the defaults. Never throws for a user the database does not know.
    /// </summary>
    Task<NotificationPreferencesView> GetAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Saves the caller's own settings: the quiet hours and the kinds of event of <paramref name="update"/> (the others stay as they are).
    /// </summary>
    /// <exception cref="DomainRuleException">
    /// 422: <see cref="NotificationPreferenceRules.LegalChannelRequiredCode"/> (no channel for a legal deadline),
    /// <see cref="NotificationPreferenceRules.ChannelUnavailableCode"/> (browser push), an empty update or a kind of event twice.
    /// </exception>
    /// <exception cref="NotFoundException">The caller has no host org: there is nothing of its to save.</exception>
    Task<NotificationPreferencesView> SaveAsync(string userId, NotificationPreferencesUpdate update, CancellationToken ct);

    /// <summary>
    /// Who is told about <paramref name="context"/> and how: the recipients with the channels that go now and the ones that wait, with the
    /// instant they are due. Only people who can be reached by at least one channel are returned, sorted by user id.
    /// </summary>
    Task<IReadOnlyList<NotificationRecipient>> ResolveAsync(NotificationEventContext context, CancellationToken ct);
}

/// <summary>The quiet hours of a user, on the clock of Europe/Rome.</summary>
/// <param name="Start">When they start (default 22:00).</param>
/// <param name="End">When they end (default 08:00); equal to <paramref name="Start"/> there are none.</param>
/// <param name="LegalAlertsOverride">A legal deadline alerts also during them (default on, not editable yet).</param>
public sealed record QuietHoursSettings(TimeOnly Start, TimeOnly End, bool LegalAlertsOverride);

/// <summary>What a user has for one kind of event: <paramref name="IsDefault"/> when it never chose and the default of PO-10 applies.</summary>
public sealed record NotificationEventSetting(NotificationEventType EventType, NotificationChannels Channels, bool IsDefault);

/// <summary>A channel and whether it can deliver today; <paramref name="UnavailableReason"/> is a code the page turns into a sentence.</summary>
public sealed record NotificationChannelAvailability(NotificationChannels Channel, bool Available, string? UnavailableReason);

/// <summary>The settings of a user as the API gives them.</summary>
public sealed record NotificationPreferencesView(
    QuietHoursSettings QuietHours,
    IReadOnlyList<NotificationEventSetting> Events,
    IReadOnlyList<NotificationChannelAvailability> AvailableChannels);

/// <summary>New quiet hours of a user.</summary>
public sealed record QuietHoursUpdate(TimeOnly Start, TimeOnly End);

/// <summary>The channels a user chose for one kind of event (<see cref="NotificationChannels.None"/> = nothing at all).</summary>
public sealed record NotificationEventUpdate(NotificationEventType EventType, NotificationChannels Channels);

/// <summary>A save: the quiet hours and/or the kinds of event to change. An update with neither is a 422.</summary>
public sealed record NotificationPreferencesUpdate(QuietHoursUpdate? QuietHours, IReadOnlyList<NotificationEventUpdate>? Events);

/// <summary>A permission the recipients of an alert must hold, in the context that grants it (<c>short-rent</c>, <c>long-rent</c>, ...).</summary>
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
