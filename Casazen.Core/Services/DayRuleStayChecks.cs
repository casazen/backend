using System.Globalization;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

public enum DayRuleViolationKind
{
    ArrivalClosed,

    DepartureClosed,

    MinNights,

    MaxNights,

    AdvanceTooShort,

    AdvanceTooLong,
}

public sealed record DayRuleViolation(DayRuleViolationKind Kind, DateOnly Date, int? Limit = null);

public static class DayRuleStayChecks
{
    public static IReadOnlyDictionary<DateOnly, DayRuleValues> NoRules { get; } = new Dictionary<DateOnly, DayRuleValues>();

    public static DayRuleViolation? FirstViolation(
        DateOnly arrival,
        DateOnly departure,
        DateOnly todayOfProperty,
        int? propertyMinNights,
        IReadOnlyDictionary<DateOnly, DayRuleValues> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        rules.TryGetValue(arrival, out var onArrival);
        rules.TryGetValue(departure, out var onDeparture);

        if (onArrival is { ClosedToArrival: true })
            return new DayRuleViolation(DayRuleViolationKind.ArrivalClosed, arrival);

        if (onDeparture is { ClosedToDeparture: true })
            return new DayRuleViolation(DayRuleViolationKind.DepartureClosed, departure);

        var nights = departure.DayNumber - arrival.DayNumber;
        if ((onArrival?.MinNights ?? propertyMinNights) is { } minimum && nights < minimum)
            return new DayRuleViolation(DayRuleViolationKind.MinNights, arrival, minimum);

        if (onArrival?.MaxNights is { } maximum && nights > maximum)
            return new DayRuleViolation(DayRuleViolationKind.MaxNights, arrival, maximum);

        return AdvanceViolation(arrival, todayOfProperty, onArrival);
    }

    public static bool CanArriveOn(DateOnly night, DateOnly todayOfProperty, DayRuleValues? rule) =>
        rule is not { ClosedToArrival: true } && AdvanceViolation(night, todayOfProperty, rule) is null;

    private static DayRuleViolation? AdvanceViolation(DateOnly arrival, DateOnly todayOfProperty, DayRuleValues? onArrival)
    {
        var advance = arrival.DayNumber - todayOfProperty.DayNumber;
        if (onArrival?.MinAdvanceDays is { } minAdvance && advance < minAdvance)
            return new DayRuleViolation(DayRuleViolationKind.AdvanceTooShort, arrival, minAdvance);

        if (onArrival?.MaxAdvanceDays is { } maxAdvance && advance > maxAdvance)
            return new DayRuleViolation(DayRuleViolationKind.AdvanceTooLong, arrival, maxAdvance);

        return null;
    }

    public static DomainRuleException ToException(DayRuleViolation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);

        var (code, key) = violation.Kind switch
        {
            DayRuleViolationKind.ArrivalClosed => (DirectBookingErrorCodes.ArrivalClosed, "DirectBookingArrivalClosed"),
            DayRuleViolationKind.DepartureClosed => (DirectBookingErrorCodes.DepartureClosed, "DirectBookingDepartureClosed"),
            DayRuleViolationKind.MinNights => (DirectBookingErrorCodes.MinNightsNotMet, "DirectBookingMinNightsNotMet"),
            DayRuleViolationKind.MaxNights => (DirectBookingErrorCodes.MaxNightsExceeded, "DirectBookingMaxNightsExceeded"),
            DayRuleViolationKind.AdvanceTooShort => (DirectBookingErrorCodes.AdvanceTooShort, "DirectBookingAdvanceTooShort"),
            _ => (DirectBookingErrorCodes.AdvanceTooLong, "DirectBookingAdvanceTooLong"),
        };

        var facts = new Dictionary<string, object?> { ["date"] = violation.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (violation.Limit is { } limit)
            facts["limit"] = limit;

        return violation.Limit is { } number
            ? new DomainRuleException(code, key, number) { Extensions = facts }
            : new DomainRuleException(code, key) { Extensions = facts };
    }
}
