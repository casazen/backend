using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Scheduling;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// The time triggers of the stays (ADR-022-R8): the kinds and the payload of a <see cref="DomainEventType.TimeTrigger"/> event,
/// its dedup key, and how a routine's <c>EventConfig</c> gives its offset. <c>TimeEventEmitterJob</c> writes the triggers and the
/// routine handler reads them with the same rules, so "2 days before check-in" means the same thing on both sides.
/// </summary>
public static class TimeTrigger
{
    public const string BeforeCheckIn = "BeforeCheckIn";
    public const string AfterCheckIn = "AfterCheckIn";
    public const string BeforeCheckOut = "BeforeCheckOut";
    public const string AfterCheckOut = "AfterCheckOut";
    public const string Weekly = "Weekly";

    public const int MaxOffsetHours = ScheduledMessagePlanner.MaxOffsetHours;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyDictionary<RoutineEventType, string> StayKinds { get; } = new Dictionary<RoutineEventType, string>
    {
        [RoutineEventType.BeforeCheckin] = BeforeCheckIn,
        [RoutineEventType.AfterCheckin] = AfterCheckIn,
        [RoutineEventType.BeforeCheckout] = BeforeCheckOut,
        [RoutineEventType.AfterCheckout] = AfterCheckOut,
    };

    public static bool IsBefore(string kind) => kind is BeforeCheckIn or BeforeCheckOut;

    public static bool IsCheckIn(string kind) => kind is BeforeCheckIn or AfterCheckIn;

    public static int? OffsetHours(string? eventConfig) => ScheduledMessagePlanner.OffsetHours(eventConfig);

    /// <summary>
    /// The dedup key of a trigger: <c>{kind}:{offset}:{entityId}:{dueAt}</c>, where <c>dueAt</c> is the UTC hour the trigger is due
    /// at, never the window of the run that found it. A restart, two overlapping runs or another evaluation interval give the
    /// same key, which the unique index of the org refuses the second time (ADR-022-R8).
    /// </summary>
    public static string DedupKey(string kind, int offsetHours, Guid entityId, DateTime dueAtUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}:{offsetHours}:{entityId:D}:{dueAtUtc:yyyy-MM-dd'T'HH}Z");

    public static bool TryReadPayload(string? payloadJson, [NotNullWhen(true)] out TimeTriggerPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;

        try
        {
            payload = JsonSerializer.Deserialize<TimeTriggerPayload>(payloadJson, ReadOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null || string.IsNullOrEmpty(payload.Kind) || payload.Offset < 0)
        {
            payload = null;
            return false;
        }

        return true;
    }
}

public sealed record TimeTriggerPayload(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("entityId")] Guid EntityId,
    [property: JsonPropertyName("bookingId")] Guid BookingId,
    [property: JsonPropertyName("propertyId")] Guid PropertyId);
