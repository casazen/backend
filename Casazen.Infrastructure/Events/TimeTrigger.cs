using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

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

    /// <summary>The largest offset taken, one year in hours: a longer one is a mistake in the routine, not a trigger.</summary>
    public const int MaxOffsetHours = 366 * 24;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The kind the emitter writes for each routine event type of a stay.</summary>
    public static IReadOnlyDictionary<RoutineEventType, string> StayKinds { get; } = new Dictionary<RoutineEventType, string>
    {
        [RoutineEventType.BeforeCheckin] = BeforeCheckIn,
        [RoutineEventType.AfterCheckin] = AfterCheckIn,
        [RoutineEventType.BeforeCheckout] = BeforeCheckOut,
        [RoutineEventType.AfterCheckout] = AfterCheckOut,
    };

    /// <summary>True for the kinds due before their date (the offset is subtracted).</summary>
    public static bool IsBefore(string kind) => kind is BeforeCheckIn or BeforeCheckOut;

    /// <summary>True for the kinds counted from the check-in date, false for those counted from the check-out date.</summary>
    public static bool IsCheckIn(string kind) => kind is BeforeCheckIn or AfterCheckIn;

    /// <summary>
    /// The offset a routine asks for, in whole hours: <c>days * 24 + hours</c> of its <c>EventConfig</c>
    /// (<c>{ "days": N, "hours": N }</c>, the shape the routine editor writes; a missing member counts 0, the names are read in any
    /// case), so <c>{ "days": 2 }</c> and <c>{ "hours": 48 }</c> are the same offset. <c>null</c> when the configuration is not
    /// that: not JSON, not an object, a member that is not a whole number of zero or more, or more than
    /// <see cref="MaxOffsetHours"/> in all.
    /// </summary>
    public static int? OffsetHours(string? eventConfig)
    {
        if (string.IsNullOrWhiteSpace(eventConfig))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(eventConfig);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            long days = 0;
            long hours = 0;
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (string.Equals(member.Name, "days", StringComparison.OrdinalIgnoreCase) && !TryWholeNumber(member.Value, out days))
                    return null;
                if (string.Equals(member.Name, "hours", StringComparison.OrdinalIgnoreCase) && !TryWholeNumber(member.Value, out hours))
                    return null;
            }

            var total = (days * 24) + hours;
            return total <= MaxOffsetHours ? (int)total : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The dedup key of a trigger: <c>{kind}:{offset}:{entityId}:{dueAt}</c>, where <c>dueAt</c> is the UTC hour the trigger is due
    /// at, never the window of the run that found it. A restart, two overlapping runs or another evaluation interval give the
    /// same key, which the unique index of the org refuses the second time (ADR-022-R8).
    /// </summary>
    public static string DedupKey(string kind, int offsetHours, Guid entityId, DateTime dueAtUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}:{offsetHours}:{entityId:D}:{dueAtUtc:yyyy-MM-dd'T'HH}Z");

    /// <summary>
    /// Reads the payload of a time trigger (names in any case). False when it is not one: not JSON, no kind, a negative offset.
    /// </summary>
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

    private static bool TryWholeNumber(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number)
            && number is >= 0 and <= MaxOffsetHours;
    }
}

/// <summary>
/// The payload of a <see cref="DomainEventType.TimeTrigger"/> event: ids and the trigger, no personal data (ADR-022-R21).
/// <see cref="Offset"/> is in whole hours, compared with the routine's <c>EventConfig</c> by <see cref="TimeTrigger.OffsetHours"/>.
/// </summary>
public sealed record TimeTriggerPayload(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("entityId")] Guid EntityId,
    [property: JsonPropertyName("bookingId")] Guid BookingId,
    [property: JsonPropertyName("propertyId")] Guid PropertyId);
