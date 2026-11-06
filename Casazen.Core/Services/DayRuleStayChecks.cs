using System.Globalization;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>Which rule of the nights a stay breaks (E08/F05/US01/T03, NF-36).</summary>
public enum DayRuleViolationKind
{
    /// <summary>The stay arrives on a night closed to arrival.</summary>
    ArrivalClosed,

    /// <summary>The stay leaves on a day closed to departure.</summary>
    DepartureClosed,

    /// <summary>The stay is shorter than the minimum stay of its arrival date.</summary>
    MinNights,

    /// <summary>The stay is longer than the maximum stay of its arrival date.</summary>
    MaxNights,

    /// <summary>The stay is booked closer to its arrival than the minimum advance of its arrival date.</summary>
    AdvanceTooShort,

    /// <summary>The stay is booked further ahead of its arrival than the maximum advance of its arrival date.</summary>
    AdvanceTooLong,
}

/// <summary>The first rule a stay breaks, with the date it is about and the limit it did not respect.</summary>
/// <param name="Kind">The rule.</param>
/// <param name="Date">The date the rule is read on: the arrival date, or the departure date for <see cref="DayRuleViolationKind.DepartureClosed"/>.</param>
/// <param name="Limit">The minimum or maximum of the rule (nights or days); <c>null</c> for a closed arrival or departure.</param>
public sealed record DayRuleViolation(DayRuleViolationKind Kind, DateOnly Date, int? Limit = null);

/// <summary>
/// Whether a stay respects the rules of its nights (E08/F05/US01/T03, NF-36): one pure function that the quote, the checkout and the search of
/// the booking site all ask, so a stay the search offers is a stay the checkout accepts. It reads the rule of the night the stay arrives on (the
/// minimum and the maximum stay, the closed arrival, the advance) and of the day it leaves (the closed departure), and falls back on the minimum stay
/// of the property when the arrival night has none of its own.
/// </summary>
public static class DayRuleStayChecks
{
    /// <summary>No rules at all: a stay is then judged on the minimum stay of the property alone.</summary>
    public static IReadOnlyDictionary<DateOnly, DayRuleValues> NoRules { get; } = new Dictionary<DateOnly, DayRuleValues>();

    /// <summary>
    /// The first rule the stay from <paramref name="arrival"/> to <paramref name="departure"/> (the day it leaves) breaks, or <c>null</c> when it
    /// breaks none. The order: the arrival closed, the departure closed, fewer nights than the minimum of the arrival date (the rule's, else
    /// <paramref name="propertyMinNights"/>), more nights than its maximum, an advance shorter than its minimum, an advance longer than its maximum.
    /// The advance is the number of days from <paramref name="todayOfProperty"/> to the arrival, on the calendar of the property.
    /// </summary>
    /// <param name="rules">The rules of the property by night; a night that is not in it has none.</param>
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

        var advance = arrival.DayNumber - todayOfProperty.DayNumber;
        if (onArrival?.MinAdvanceDays is { } minAdvance && advance < minAdvance)
            return new DayRuleViolation(DayRuleViolationKind.AdvanceTooShort, arrival, minAdvance);

        if (onArrival?.MaxAdvanceDays is { } maxAdvance && advance > maxAdvance)
            return new DayRuleViolation(DayRuleViolationKind.AdvanceTooLong, arrival, maxAdvance);

        return null;
    }

    /// <summary>
    /// The 422 a quote or a checkout answers for <paramref name="violation"/>: the stable code of <see cref="DirectBookingErrorCodes"/>, the sentence
    /// of its key in both languages (a limit is its argument) and, as extra members of the problem, the <c>date</c> the rule is about
    /// (<c>yyyy-MM-dd</c>, the calendar date of the property) and the <c>limit</c> when there is one.
    /// </summary>
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
