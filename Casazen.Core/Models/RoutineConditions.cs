using System.Collections;
using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;

namespace Casazen.Core.Models;

/// <summary>
/// The facts a routine condition is evaluated on (E01/F04/US01/T01, ADR-014): one value for each of the fields the routines can read
/// (<see cref="RoutineConditions.SupportedFields"/>), taken from the booking an event is about, never from the payload of the event. A field with no
/// fact (no booking, a field the routines do not read) is not known, and a rule on a field that is not known is not met.
/// </summary>
public sealed class RoutineConditionFacts
{
    private readonly Dictionary<string, object?> _values;

    public RoutineConditionFacts(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>No fact at all: the event is about no booking the org knows.</summary>
    public static RoutineConditionFacts None => new(new Dictionary<string, object?>());

    /// <summary>
    /// The facts of a booking: its property (<c>propertyId</c>), where it comes from (<c>channel</c>, the name of its <see cref="BookingSource"/>),
    /// the nights (<c>stayDuration</c>), the guests (<c>guestCount</c>) and the days between the day it was made, in Rome, and its check-in
    /// (<c>bookingLeadTime</c>, never below 0). Dates are the stored ones (midnight UTC of the Rome date). The lead time is a fact only of the bookings
    /// CasaZen takes itself (the booking site, the host): one that comes from a channel (<see cref="FiscalCopy.IsOtaBookingSource"/>) is saved when it is
    /// imported, so its creation is the import and not the day the guest booked. Its lead time is not known, and a rule on it is not met.
    /// </summary>
    public static RoutineConditionFacts OfBooking(
        Guid propertyId,
        BookingSource source,
        DateTime checkInDate,
        DateTime checkOutDate,
        int numberOfGuests,
        DateTime bookedAt)
    {
        var checkIn = DateOnly.FromDateTime(checkInDate);
        var checkOut = DateOnly.FromDateTime(checkOutDate);
        var facts = new Dictionary<string, object?>
        {
            [RoutineConditions.PropertyId] = propertyId.ToString("D"),
            [RoutineConditions.Channel] = source.ToString(),
            [RoutineConditions.StayDuration] = (double)Math.Max(0, checkOut.DayNumber - checkIn.DayNumber),
            [RoutineConditions.GuestCount] = (double)Math.Max(0, numberOfGuests),
        };

        if (!FiscalCopy.IsOtaBookingSource(source))
            facts[RoutineConditions.BookingLeadTime] = (double)Math.Max(0, checkIn.DayNumber - RomeCalendar.DateInRome(bookedAt).DayNumber);

        return new RoutineConditionFacts(facts);
    }

    /// <summary>The fact of a field; false when there is none.</summary>
    public bool TryGet(string fieldId, out object? value)
    {
        value = null;
        return !string.IsNullOrEmpty(fieldId) && _values.TryGetValue(fieldId, out value) && value is not null;
    }
}

/// <summary>What <see cref="RoutineConditions.Parse"/> read in the <c>ConditionJson</c> of a routine.</summary>
/// <param name="Valid">The JSON is a condition (or none).</param>
/// <param name="Condition">The condition; <c>null</c> for "always" (no condition written).</param>
public readonly record struct RoutineConditionParse(bool Valid, Condition? Condition)
{
    /// <summary>No condition: the routine always runs.</summary>
    public static RoutineConditionParse Always => new(true, null);

    /// <summary>Not a condition.</summary>
    public static RoutineConditionParse Invalid => new(false, null);
}

/// <summary>
/// The conditions of the routines (NF-21, ADR-014): which fields a routine can read, how its <c>ConditionJson</c> is read, and whether a rule is met
/// by the facts of a booking. Pure: the engine loads the facts, <c>IConditionEvaluator</c> walks the groups and asks this for each rule.
/// </summary>
public static class RoutineConditions
{
    // The ids of FieldCatalog the routines read.
    public const string PropertyId = "propertyId";
    public const string Channel = "channel";
    public const string StayDuration = "stayDuration";
    public const string GuestCount = "guestCount";
    public const string BookingLeadTime = "bookingLeadTime";

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,

        // A member nobody reads (a misspelled "groups", "conditions" for "rules") is an error: ignoring it would turn the rules that were meant into
        // "no condition", a routine that runs for every booking.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// The fields of the catalog a routine condition can use: the ones whose value is a fact of the booking. The others (the payment, the check-in and
    /// the identity status, the language, the tags, the free nights, and every field of the guest segments) have no value in a routine yet, so a
    /// condition on them could never be met and is refused instead (<see cref="UnsupportedFieldErrors"/>).
    /// </summary>
    public static IReadOnlyList<string> SupportedFields { get; } = [PropertyId, Channel, StayDuration, GuestCount, BookingLeadTime];

    /// <summary>
    /// Reads the <c>ConditionJson</c> of a routine (the names in any case): blank is "always", <c>{}</c> is a condition with no group, which is "always"
    /// too. Not JSON, not an object, a value that is not a condition, or a member nobody reads (<c>conditions</c> for <c>rules</c>, <c>field</c> for
    /// <c>fieldId</c>) is invalid: what was meant is never read as "no condition".
    /// </summary>
    public static RoutineConditionParse Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return RoutineConditionParse.Always;

        try
        {
            var condition = JsonSerializer.Deserialize<Condition>(json, ParseOptions);
            return condition is null ? RoutineConditionParse.Invalid : new RoutineConditionParse(true, condition);
        }
        catch (JsonException)
        {
            return RoutineConditionParse.Invalid;
        }
    }

    /// <summary>One message for each rule on a field the routines do not read (<see cref="SupportedFields"/>).</summary>
    public static IReadOnlyList<string> UnsupportedFieldErrors(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        // Shapes the generic validator reports ("groups cannot be null"…) are not this method's business: it only names the fields.
        var errors = new List<string>();
        foreach (var group in condition.Groups ?? [])
        {
            foreach (var rule in group?.Rules ?? [])
            {
                if (rule is not null
                    && !string.IsNullOrWhiteSpace(rule.FieldId)
                    && FieldCatalog.IsValidField(rule.FieldId)
                    && !SupportedFields.Contains(rule.FieldId, StringComparer.Ordinal))
                {
                    errors.Add($"Field {rule.FieldId} cannot be used in a routine condition");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// One message for each rule on a field the routines read (<see cref="SupportedFields"/>) whose value is not of the kind its operator takes: a
    /// non-empty list of property ids for <c>propertyId</c>, the name of a channel (a <see cref="BookingSource"/>) or a non-empty list of them for
    /// <c>channel</c>, a whole number for <c>equals</c> on a count, a number for <c>greaterThan</c> and <c>lessThan</c>, two numbers in order for
    /// <c>between</c>. <see cref="Satisfies"/> never meets such a rule (it would meet <c>notEquals</c> and <c>notIn</c> for every booking if it did not
    /// check), so a routine that holds one is refused when it is saved and fails when it runs, instead of running in silence on something else than
    /// what was meant. A rule with no value, on another field or with an operator its field does not take is the business of the generic validator.
    /// </summary>
    public static IReadOnlyList<string> ValueErrors(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var errors = new List<string>();
        foreach (var group in condition.Groups ?? [])
        {
            foreach (var rule in group?.Rules ?? [])
            {
                if (rule?.Value is null
                    || string.IsNullOrWhiteSpace(rule.FieldId)
                    || !SupportedFields.Contains(rule.FieldId, StringComparer.Ordinal)
                    || !FieldCatalog.IsValidOperator(rule.FieldId, rule.Operator))
                {
                    continue;
                }

                if (ValueError(rule) is { } error)
                    errors.Add(error);
            }
        }

        return errors;
    }

    private static string? ValueError(ConditionRule rule)
    {
        var value = Plain(rule.Value);
        var field = rule.FieldId;
        var op = rule.Operator;

        switch (field)
        {
            case PropertyId:
                return IsListOf(value, IsPropertyId) ? null : $"Field {field}: {op} needs a list of property ids";

            case Channel when op is "in" or "notIn":
                return IsListOf(value, IsChannelName) ? null : $"Field {field}: {op} needs a list of channel names";

            case Channel:
                return value is string text && IsChannelName(text) ? null : $"Field {field}: {op} needs the name of a channel";

            default:
                // The counts: stayDuration, guestCount and bookingLeadTime.
                return op switch
                {
                    "equals" => value is double whole && double.IsFinite(whole) && whole == Math.Floor(whole) ? null : $"Field {field}: equals needs a whole number",
                    "greaterThan" or "lessThan" => value is double number && double.IsFinite(number) ? null : $"Field {field}: {op} needs a number",
                    "between" => IsRange(value) ? null : $"Field {field}: between needs two numbers, the lower first",
                    _ => null,
                };
        }
    }

    private static bool IsListOf(object? value, Func<string, bool> element) =>
        value is List<object?> { Count: > 0 } items && items.All(item => item is string text && element(text));

    /// <summary>The id of a property as the fact spells it (<c>propertyId.ToString("D")</c>): another spelling would never be equal to it.</summary>
    private static bool IsPropertyId(string text) => Guid.TryParseExact(text.Trim(), "D", out _);

    private static bool IsChannelName(string text) => EnumNames.TryParseDefined<BookingSource>(text, out _);

    private static bool IsRange(object? value) =>
        value is List<object?> { Count: 2 } range
        && range[0] is double low
        && double.IsFinite(low)
        && range[1] is double high
        && double.IsFinite(high)
        && low <= high;

    /// <summary>
    /// Whether <paramref name="rule"/> is met by <paramref name="facts"/>. A field with no fact is not met, whatever the operator (<c>notEquals</c> and
    /// <c>notIn</c> included), and so is a list that is empty, a range that is not two numbers in order and a value of another kind: nothing a routine
    /// cannot prove ever counts as "everything". Texts are compared without regard to case; numbers are the JSON numbers.
    /// </summary>
    public static bool Satisfies(ConditionRule rule, RoutineConditionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.TryGet(rule.FieldId, out var fact) || fact is null)
            return false;

        var value = Plain(rule.Value);
        return rule.Operator switch
        {
            "equals" => value is not null && Same(fact, value),
            "notEquals" => value is not null && SameKind(fact, value) && !Same(fact, value),
            "in" => value is List<object?> { Count: > 0 } options && options.Any(option => Same(fact, option)),
            "notIn" => value is List<object?> { Count: > 0 } excluded && excluded.All(option => SameKind(fact, option)) && !excluded.Any(option => Same(fact, option)),
            "greaterThan" => fact is double above && value is double limit && above > limit,
            "lessThan" => fact is double below && value is double ceiling && below < ceiling,
            "between" => fact is double number
                         && value is List<object?> { Count: 2 } range
                         && range[0] is double low
                         && range[1] is double high
                         && low <= high
                         && number >= low
                         && number <= high,
            "is" => fact is bool flag && (value is null || (value is bool wanted && flag == wanted)),
            _ => false,
        };
    }

    private static bool Same(object fact, object? other) => (fact, other) switch
    {
        (string text, string wanted) => string.Equals(text.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase),
        (double number, double wanted) => number.Equals(wanted),
        (bool flag, bool wanted) => flag == wanted,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="other"/> is of the kind of <paramref name="fact"/> (a text for a text, a number for a number, a switch for a switch). What
    /// cannot be compared is not "different": <c>notEquals</c> and <c>notIn</c> with a value of another kind are not met, instead of met for every booking.
    /// </summary>
    private static bool SameKind(object fact, object? other) => (fact, other) switch
    {
        (string, string) => true,
        (double, double) => true,
        (bool, bool) => true,
        _ => false,
    };

    /// <summary>A value as the rules compare it: text, a number (as <see cref="double"/>), a switch, or a list of them; <c>null</c> for anything else.</summary>
    private static object? Plain(object? raw) => raw switch
    {
        null => null,
        JsonElement element => FromJson(element),
        string text => text,
        bool flag => flag,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
        IEnumerable items => items.Cast<object?>().Select(Plain).ToList(),
        _ => null,
    };

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetDouble(out var number) ? number : null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        _ => null,
    };
}
