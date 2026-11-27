using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A guest wrote in a conversation of a channel: alerts the hosts the preferences select (E05/F03/US01/T03, NF-65, ADR-032-R12). The handler of
/// <see cref="DomainEventType.GuestMessageReceived"/> whose payload says the message comes from a channel (<see cref="GuestMessageReceivedPayload.OtaSource"/>,
/// recorded by the sync of the messages); the message of a direct booking is alerted by <c>GuestMessageService</c> through the same
/// <see cref="HostMessageAlertDelivery"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Who and how</b> is not decided here: <see cref="HostMessageAlertDelivery"/> asks <see cref="INotificationPreferenceService"/> (the audience of
/// the property or the administrators of the org, the permission that opens the thread, the choice of each person, the quiet hours) and delivers
/// the counter, the push and the email of each one. A conversation not mapped to a property goes to the administrators.</item>
/// <item><b>Once, in any order.</b> The event is delivered at least once and out of order (ADR-022-R4, R7): the notification and the push of a
/// person carry the key of the message and the person, the email has its slot, so a redelivery tells nobody twice. A person who could not be
/// reached makes the handler throw after the others were told, and the bus delivers the event again.</item>
/// <item><b>Only its own.</b> Every other event returns at once, and so does a message that is not from a channel. The event is the org's: the thread
/// is read in the org of the event (ADR-022-R6), so an id in the payload never reaches another org's data.</item>
/// <item><b>Privacy.</b> The payload holds ids and codes (ADR-022-R21); the delivery reads the property and the channel and nothing of the text, the
/// name of the guest or an address, and none of them is in a push, an email or a notification. The log carries the event id and the thread.</item>
/// </list>
/// The urgent message (<see cref="NotificationEventType.UrgentGuestMessage"/>) is alerted by the assistant that flags it (E05/F05) through the same
/// delivery: the event of a received message carries no urgency, so this handler always alerts <see cref="NotificationEventType.NewGuestMessage"/>.
/// </remarks>
public sealed class MessageReceivedNotifier(HostMessageAlertDelivery delivery, ILogger<MessageReceivedNotifier> logger)
    : IEventHandler<DomainEvent>
{
    private const string ThreadKeyPrefix = "ota:";

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.GuestMessageReceived || domainEvent.EntityId == Guid.Empty)
            return;

        GuestMessageReceivedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GuestMessageReceivedPayload>(domainEvent.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not a message of a guest: no alert",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        if (!string.Equals(payload.Source, GuestMessageReceivedPayload.OtaSource, StringComparison.Ordinal))
            return;

        if (!TryThreadIdOf(payload.ThreadKey, out var threadId))
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} names a thread key that is not a conversation of a channel: no alert",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        // The message is the entity of the event (OtaMessage.Id): the identity of the alert.
        await delivery.DeliverAsync(
            new HostMessageAlert(
                domainEvent.OrgId,
                HostMessageSource.Ota,
                NotificationEventType.NewGuestMessage,
                threadId,
                domainEvent.EntityId,
                domainEvent.OccurredAt),
            ct);
    }

    /// <summary>The id of the thread in <c>ota:{guid}</c>; false for any other form.</summary>
    private static bool TryThreadIdOf(string? threadKey, out Guid threadId)
    {
        threadId = Guid.Empty;
        return threadKey is not null
               && threadKey.StartsWith(ThreadKeyPrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(threadKey.AsSpan(ThreadKeyPrefix.Length), "D", out threadId)
               && threadId != Guid.Empty;
    }
}
