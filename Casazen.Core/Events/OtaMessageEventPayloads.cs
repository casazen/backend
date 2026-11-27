using System.Text.Json.Serialization;

namespace Casazen.Core.Events;

/// <summary>
/// The payload of <c>GuestMessageReceived</c> for a message of an OTA conversation (E05/F01/US01/T02, ADR-022-R1, ADR-022-R21): ids and codes
/// only, never the text, the name of the guest or an address. A consumer reads what it needs from the thread, in its own org. The names are
/// written as the brief of the epic spells them.
/// </summary>
/// <param name="ThreadKey">The key of the thread in the hub of messages: <c>ota:{thread id}</c>.</param>
/// <param name="Source">Where the message comes from: <c>Ota</c>, the copy of a channel (a direct conversation is the other source).</param>
/// <param name="PropertyId">The property of the thread; <c>null</c> for an unassigned thread (its listing has no confirmed mapping).</param>
/// <param name="BookingId">The booking the thread is linked to, when it is.</param>
/// <param name="Channel">The channel: <c>Airbnb</c>, <c>Booking</c>, <c>Vrbo</c> or <c>Expedia</c>.</param>
public sealed record GuestMessageReceivedPayload(
    [property: JsonPropertyName("threadKey")] string ThreadKey,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("propertyId")] Guid? PropertyId,
    [property: JsonPropertyName("bookingId")] Guid? BookingId,
    [property: JsonPropertyName("channel")] string Channel)
{
    /// <summary>The <see cref="Source"/> of a message that comes from a channel through the channel manager.</summary>
    public const string OtaSource = "Ota";

    /// <summary>
    /// The <see cref="Source"/> of a message the guest of a direct booking wrote in the guest area (E05/F05/US02/T01): the key is <c>direct:{booking id}</c>, the channel <c>Direct</c>.
    /// The assistant reads it; the alert to the hosts and the routines do not (the guest area alerts by itself, and the routines start on the messages of a channel only).
    /// </summary>
    public const string DirectSource = "Direct";
}

/// <summary>
/// The payload of <c>OtaThreadSynced</c> (E05/F01/US01/T02): a thread of an OTA conversation was read from the channel manager. Ids and the flag of
/// the assistant of Octorate only, so the guard of E05/F05 can verify that assistant is off (OCT-11) without reading the conversation.
/// </summary>
/// <param name="ThreadKey">The key of the thread in the hub of messages: <c>ota:{thread id}</c>.</param>
/// <param name="OctorateThreadId">The thread on Octorate.</param>
/// <param name="AccommodationId">The accommodation on Octorate the thread is scoped to.</param>
/// <param name="PropertyId">The property of the thread; <c>null</c> while it is unassigned.</param>
/// <param name="AiAssistantOn">What Octorate reports for its assistant on this thread: on, off, or <c>null</c> when it does not say.</param>
public sealed record OtaThreadSyncedPayload(
    [property: JsonPropertyName("threadKey")] string ThreadKey,
    [property: JsonPropertyName("octorateThreadId")] string OctorateThreadId,
    [property: JsonPropertyName("accommodationId")] string AccommodationId,
    [property: JsonPropertyName("propertyId")] Guid? PropertyId,
    [property: JsonPropertyName("aiAssistantOn")] bool? AiAssistantOn);
