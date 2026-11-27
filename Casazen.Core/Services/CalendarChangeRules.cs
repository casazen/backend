using Casazen.Core.Exceptions;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>What the evaluation of a change for one property found (<see cref="CalendarChangeRules.Evaluate"/>).</summary>
/// <param name="Days">The nights the change selects: changed, unchanged and over together.</param>
/// <param name="ChangedDays">The nights whose rule would change.</param>
/// <param name="UnchangedDays">The nights the change leaves as they are.</param>
/// <param name="TakenDays">The nights, among the changed and the unchanged, that are taken.</param>
/// <param name="SkippedPastDays">The nights before today of the property.</param>
/// <param name="Samples">The first changed nights, at most <see cref="CalendarChangeRules.SampleCount"/>.</param>
public sealed record CalendarChangeEvaluation(
    int Days,
    int ChangedDays,
    int UnchangedDays,
    int TakenDays,
    int SkippedPastDays,
    IReadOnlyList<CalendarChangeSample> Samples);

/// <summary>
/// The rules of a change of the calendar that need no database and no clock (E08/F05/US02/T03, NF-36, NF-78): what a request must look like, which nights it
/// selects, which of them change, which are over for the property and which are taken, and what it would cost in quota of the channel manager. Pure, so the
/// preview, the apply and the tests agree.
/// </summary>
public static class CalendarChangeRules
{
    /// <summary>How many changed nights the preview of a property shows with their values before and after.</summary>
    public const int SampleCount = 5;

    /// <summary>
    /// Checks what can be refused before anything is read: the properties (1 to <see cref="HostCalendarLimits.MaxProperties"/>, never «all» when none), the range
    /// (at most <see cref="DayRuleLimits.MaxRangeDays"/> days, both ends included), the weekdays (never «all» when none) and that the patch touches a value.
    /// Returns the properties without repeats.
    /// </summary>
    /// <exception cref="DomainRuleException">The first problem, with its stable code.</exception>
    public static IReadOnlyList<Guid> EnsureValid(CalendarChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ids = (request.PropertyIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainRuleException(CalendarPushErrorCodes.NoProperties, CalendarPushErrorCodes.NoPropertiesMessageKey);

        if (ids.Count > HostCalendarLimits.MaxProperties)
        {
            throw new DomainRuleException(
                HostCalendarErrorCodes.TooManyProperties, HostCalendarErrorCodes.TooManyPropertiesMessageKey, HostCalendarLimits.MaxProperties);
        }

        DayRuleRules.EnsureRange(request.From, request.To);
        if (request.Patch is null || request.Patch.IsEmpty || request.DaysOfWeek is { Count: 0 })
            throw DayRuleRules.ProblemOf(DayRuleErrorCodes.NothingToChange);

        return ids;
    }

    /// <summary>
    /// What the change does to the nights of one property whose «today» is <paramref name="today"/>: the nights of the weekdays chosen, those before today
    /// skipped and counted, the values of each night validated as they end up (a value out of bounds is refused, with the code of
    /// <see cref="DayRuleErrorCodes"/>), the nights whose rule does not change counted and left alone, the taken nights counted and not skipped, and the first
    /// changed nights shown with the values the night holds before and after (the effective ones: the price a guest is quoted, the stay and the advance).
    /// </summary>
    /// <param name="today">Today on the calendar of the property.</param>
    /// <param name="request">The request (its days, its weekdays and its patch).</param>
    /// <param name="rulesBefore">The rules of the nights of the range now, by night.</param>
    /// <param name="seasonalPrices">The prices the host confirmed from the seasonal proposals, by night.</param>
    /// <param name="property">The rate, the weekend surcharge and the minimum stay of the property.</param>
    /// <param name="takenNights">The nights a booking, a hold, a request or a block takes.</param>
    /// <exception cref="DomainRuleException">A value out of bounds, or <see cref="DayRuleErrorCodes.InPast"/> when every night the change selects is over.</exception>
    public static CalendarChangeEvaluation Evaluate(
        DateOnly today,
        CalendarChangeRequest request,
        IReadOnlyDictionary<DateOnly, DayRuleValues> rulesBefore,
        IReadOnlyDictionary<DateOnly, decimal> seasonalPrices,
        OctorateCalendarProperty property,
        IReadOnlySet<DateOnly> takenNights)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rulesBefore);
        ArgumentNullException.ThrowIfNull(seasonalPrices);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(takenNights);

        int days = 0, changed = 0, unchanged = 0, taken = 0, skippedPast = 0;
        var samples = new List<CalendarChangeSample>();
        for (var night = request.From; night <= request.To; night = night.AddDays(1))
        {
            if (request.DaysOfWeek is { } weekdays && !weekdays.Contains(night.DayOfWeek))
                continue;

            days++;
            if (night < today)
            {
                skippedPast++;
                continue;
            }

            var before = rulesBefore.TryGetValue(night, out var existing) ? existing : DayRuleValues.None;
            var after = request.Patch.ApplyTo(before);
            DayRuleRules.EnsureValid(after);
            if (takenNights.Contains(night))
                taken++;

            if (after == before)
            {
                unchanged++;
                continue;
            }

            changed++;
            if (samples.Count < SampleCount)
                samples.Add(new CalendarChangeSample(night, EffectiveOf(night, before, seasonalPrices, property), EffectiveOf(night, after, seasonalPrices, property)));
        }

        // Every night the change selects is already over and none is left to change or to leave as it is: said, not «0 changed».
        if (changed == 0 && unchanged == 0 && skippedPast > 0)
            throw DayRuleRules.ProblemOf(DayRuleErrorCodes.InPast);

        return new CalendarChangeEvaluation(days, changed, unchanged, taken, skippedPast, samples);
    }

    /// <summary>
    /// What the night holds with <paramref name="rule"/> on it: the price a guest is quoted (<see cref="StayPricing.NightPrice"/>: the price of the rule, else
    /// the seasonal price the host confirmed, else the weekend surcharge or the nightly rate), the minimum stay of the rule, else of the property, else 1, and the
    /// rest of the rule.
    /// </summary>
    public static CalendarChangeValues EffectiveOf(
        DateOnly night,
        DayRuleValues rule,
        IReadOnlyDictionary<DateOnly, decimal> seasonalPrices,
        OctorateCalendarProperty property)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(seasonalPrices);
        ArgumentNullException.ThrowIfNull(property);

        // The price the host gave the night by its rule outranks the seasonal one, as DayRulePricing.HostPrices has it.
        IReadOnlyDictionary<DateOnly, decimal> hostPrices = rule.Price is { } own
            ? new Dictionary<DateOnly, decimal> { [night] = own }
            : seasonalPrices;
        var price = StayPricing.NightPrice(night, property.NightlyRate, property.WeekendSurchargePercent, hostPrices);

        return new CalendarChangeValues(
            StayPricing.ToCents(price),
            DayRuleDefaults.MinNightsOf(rule, property.MinNights),
            rule.MaxNights,
            rule.ClosedToArrival,
            rule.ClosedToDeparture,
            rule.MinAdvanceDays,
            rule.MaxAdvanceDays);
    }

    /// <summary>
    /// The days of a change that reach the channel manager for a property whose «today» is <paramref name="today"/>: the range cut to start today; <c>null</c> when
    /// it is over. The days before today are never sent.
    /// </summary>
    public static (DateOnly From, DateOnly To)? PushWindow(DateOnly today, DateOnly from, DateOnly to)
    {
        var start = from < today ? today : from;
        return to < start ? null : (start, to);
    }

    /// <summary>
    /// The units of quota of the channel manager a change of <paramref name="from"/> to <paramref name="to"/> would use for one mirrored property (an estimate:
    /// <c>ceil(room-days / RoomDaysPerQuotaUnit)</c> for each call of at most <c>MaxDaysPerCall</c> days; the unit is 15 by decision D7 and comes from the
    /// configuration), counting only the days from today on. 0 when the range is over.
    /// </summary>
    public static int EstimateQuotaUnits(DateOnly today, DateOnly from, DateOnly to, OctorateCalendarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return PushWindow(today, from, to) is { } window
            ? CalendarChunkPlanner.EstimateQuotaUnits(window.From, window.To, options.MaxDays, options.RoomDaysPerUnit)
            : 0;
    }
}
