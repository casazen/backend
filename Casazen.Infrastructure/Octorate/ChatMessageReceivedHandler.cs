using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The handler of <c>CHAT_MESSAGE_RECEIVED</c> (E05/F01/US01/T03, ADR-032, ADR-022-R7, OCT-08): a guest wrote on a channel, and Octorate says so to
/// the webhook. The notification is a signal and nothing more, so the handler trusts nothing in it: it takes the accommodation and the thread
/// from it as the names of what to read, and <see cref="IMessageSyncService.SyncThreadAsync"/> re-reads the thread through the API before any
/// effect; a notification without usable names, or whose thread Octorate does not know, makes the recent pass read the org's listings instead.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only its own.</b> Every other kind of notification (the reservation ones have their handlers) returns at once. The event is the org's:
/// the org and the mapping the controller found are the ones of the event, and the service reads an accommodation only if it is a listing of that
/// org, so a name in the body never reaches another org's data.</item>
/// <item><b>Once, in any order.</b> Octorate retries for up to two days, folds identical sends of five minutes and keeps no order. A notification
/// delivered again, late or before the one it follows re-reads the thread, which stores what is new and nothing twice (keys of the message and of
/// the thread in the service, ADR-022-R7): one message and one event however many times it arrives.</item>
/// <item><b>Failures.</b> An error of Octorate, a rate limit and a database error throw, so the dispatcher delivers the event again (ADR-022-R4) and the
/// reconciliation catches whatever is left. The handler never waits for the calls of the org to refill (<see cref="MessageSyncPacing"/>): it runs in the
/// dispatcher, which serves every org within a budget of seconds, so with the calls down to the reserve it throws the rate limit at once. An org that disconnected, and a listing whose license has no chat module (kept
/// as the state of the listing, <c>chat_not_licensed</c>), end the handler quietly: a retry would find the same.</item>
/// <item>Off with <c>Features:OctorateConnector</c>: the connector is switched off, no call is made.</item>
/// <item><b>Privacy.</b> The log line carries the event id, the kind of notification and how it went: never the body of the call.</item>
/// </list>
/// </remarks>
public sealed class ChatMessageReceivedHandler(
    IMessageSyncService sync,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    MessageSyncPacing pacing,
    ILogger<ChatMessageReceivedHandler> logger) : IEventHandler<OctorateWebhookReceivedEvent>
{
    public async Task HandleAsync(OctorateWebhookReceivedEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!string.Equals(domainEvent.WebhookType, OctorateWebhookEvents.ChatMessageReceived, StringComparison.Ordinal))
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Octorate webhook {EventId} ({EventType}): the connector is off, nothing done", domainEvent.Id, domainEvent.WebhookType);
            return;
        }

        // This runs in the outbox dispatcher, one event after the other for every org within a budget of seconds: the sync of this scope never waits for
        // the calls of the org to refill, it ends at once with the rate limit and the dispatcher delivers the event again at its next run.
        pacing.WaitForCapacity = false;

        SyncResult result;
        try
        {
            using (orgScope.Use(domainEvent.OrgId))
            {
                if (IsUsable(domainEvent.AccommodationId, domainEvent.ObjectId))
                {
                    result = await sync.SyncThreadAsync(domainEvent.OrgId, domainEvent.AccommodationId, domainEvent.ObjectId!, null, ct);

                    // What the body names may not be a thread of the chat (what it holds is not recorded yet, and its object id is the first of several
                    // keys): when Octorate does not know it, the notification is still a signal that something was written, so the recent pass reads.
                    if (result.NotFound > 0)
                        result = result.Plus(await sync.ReconcileRecentAsync(domainEvent.OrgId, ct));
                }
                else
                {
                    result = await sync.ReconcileRecentAsync(domainEvent.OrgId, ct);
                }
            }
        }
        catch (OctorateAuthException ex) when (ex.IsNotConnected)
        {
            // The org disconnected after the notification: there is nothing to read, and a retry would find the same.
            logger.LogInformation("Octorate webhook {EventId} ({EventType}): the org is no longer connected, nothing done", domainEvent.Id, domainEvent.WebhookType);
            return;
        }
        catch (OctorateAuthException ex) when (ex.IsChatNotLicensed)
        {
            // The state is on the checkpoints of the listing already; delivering the event again would ask the same question.
            logger.LogInformation("Octorate webhook {EventId} ({EventType}): the chat module is not in the license of the listing, nothing done", domainEvent.Id, domainEvent.WebhookType);
            return;
        }

        logger.LogInformation(
            "Octorate chat webhook handled: event {EventId}, type {EventType}, {Threads} threads, {Added} messages added",
            domainEvent.Id,
            domainEvent.WebhookType,
            result.ThreadsSeen,
            result.MessagesAdded);
    }

    /// <summary>The notification names an accommodation and a thread the client can ask for (a thread is a number); anything else is no name at all.</summary>
    private static bool IsUsable(string accommodationId, string? objectId) =>
        !string.IsNullOrWhiteSpace(accommodationId)
        && accommodationId.Trim().Length <= OctorateApiClient.MaxAccommodationIdLength
        && !string.IsNullOrWhiteSpace(objectId)
        && objectId.Trim().Length <= 19
        && objectId.Trim().All(char.IsAsciiDigit);
}
