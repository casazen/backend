using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The rules of the owner stays that need no database and no clock (E07/F07/US01/T01, AC-NF111-2): which rule an owner follows, what a rule may hold,
/// what a request must respect, the nights of a stay in each calendar year, how a calendar change reads for a stay and how the days of a calendar are
/// put together. Pure, so the service, the background work and the tests agree.
/// </summary>
public static class OwnerStayRules
{
    /// <summary>The days a calendar shows when the caller does not say: today and the next 90 nights.</summary>
    public const int DefaultCalendarDays = 90;

    /// <summary>The most nights a calendar shows at once.</summary>
    public const int MaxCalendarDays = 366;

    /// <summary>The most blocked ranges a rule holds.</summary>
    public const int MaxBlackouts = 50;

    /// <summary>The largest notice or window of a rule, in days: ten years.</summary>
    public const int MaxRuleDays = 3660;

    /// <summary>The most nights of an owner in a calendar year a rule may allow.</summary>
    public const int MaxYearlyNights = 366;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The rule of a property that has none: every request waits for the agency, with no other limit than the free dates.</summary>
    public static OwnerStayRuleView DefaultFor(Guid propertyId) =>
        new(null, propertyId, null, null, 0, null, 1, null, true, null, null, [], null);

    /// <summary>A stored rule as the API reads it.</summary>
    public static OwnerStayRuleView ViewOf(OwnerStayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new OwnerStayRuleView(
            rule.Id,
            rule.PropertyId,
            rule.PropertyOwnerId,
            rule.YearlyNightAllowance,
            rule.MinAdvanceDays,
            rule.MaxAdvanceDays,
            rule.MinNights,
            rule.MaxNights,
            rule.ApprovalRequired,
            rule.ActiveFrom,
            rule.ActiveUntil,
            ReadBlackouts(rule.BlackoutsJson),
            rule.UpdatedAt);
    }

    /// <summary>The rule <paramref name="propertyOwnerId"/> follows on <paramref name="propertyId"/>: its own row, else the row of the property, else the default.</summary>
    public static OwnerStayRuleView EffectiveFor(Guid propertyId, Guid propertyOwnerId, IEnumerable<OwnerStayRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var ofProperty = rules.Where(r => r.PropertyId == propertyId).ToList();
        var rule = ofProperty.FirstOrDefault(r => r.PropertyOwnerId == propertyOwnerId) ?? ofProperty.FirstOrDefault(r => r.PropertyOwnerId is null);
        return rule is null ? DefaultFor(propertyId) : ViewOf(rule);
    }

    /// <summary>The blocked ranges a rule stores (jsonb written by <see cref="WriteBlackouts"/>).</summary>
    public static IReadOnlyList<OwnerStayBlackout> ReadBlackouts(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<OwnerStayBlackout>>(json, Json) ?? [];

    /// <summary>The blocked ranges as a rule stores them, in order.</summary>
    public static string WriteBlackouts(IReadOnlyList<OwnerStayBlackout> blackouts)
    {
        ArgumentNullException.ThrowIfNull(blackouts);
        return JsonSerializer.Serialize(blackouts.OrderBy(b => b.From).ThenBy(b => b.To).ToList(), Json);
    }

    /// <summary>Refuses a rule with a value out of bounds.</summary>
    /// <exception cref="DomainRuleException"><see cref="OwnerStayErrors.RuleInvalid"/> with the field.</exception>
    public static void EnsureValid(OwnerStayRuleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Check(input.YearlyNightAllowance is null or (>= 0 and <= MaxYearlyNights), "yearlyNightAllowance");
        Check(input.MinAdvanceDays is >= 0 and <= MaxRuleDays, "minAdvanceDays");
        Check(input.MaxAdvanceDays is null || (input.MaxAdvanceDays >= input.MinAdvanceDays && input.MaxAdvanceDays <= MaxRuleDays), "maxAdvanceDays");
        Check(input.MinNights is >= 1 and <= ManualBlocks.MaxNights, "minNights");
        Check(input.MaxNights is null || (input.MaxNights >= input.MinNights && input.MaxNights <= ManualBlocks.MaxNights), "maxNights");
        Check(input.ActiveFrom is null || input.ActiveUntil is null || input.ActiveFrom <= input.ActiveUntil, "activeUntil");
        var blackouts = input.Blackouts ?? [];
        Check(blackouts.Count <= MaxBlackouts, "blackouts");
        Check(blackouts.All(b => b.From <= b.To && b.To.DayNumber - b.From.DayNumber < MaxCalendarDays), "blackouts");
    }

    /// <summary>
    /// Refuses a stay from <paramref name="start"/> (arrival) to <paramref name="end"/> (departure) that breaks <paramref name="rule"/> on a day whose today
    /// is <paramref name="today"/>: the range, the past, the shortest and longest stay, the notice, the window, the active period and the blocked dates.
    /// The allowance and the free dates need the database: <see cref="EnsureAllowance"/> and the service.
    /// </summary>
    /// <exception cref="DomainRuleException">The first rule broken, with its stable code.</exception>
    public static void EnsureObeys(DateOnly today, DateOnly start, DateOnly end, OwnerStayRuleView rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var nights = end.DayNumber - start.DayNumber;
        if (nights <= 0 || nights > ManualBlocks.MaxNights)
            throw new DomainRuleException(OwnerStayErrors.InvalidRange, OwnerStayErrors.InvalidRangeMessageKey);
        if (start < today)
            throw new DomainRuleException(OwnerStayErrors.InPast, OwnerStayErrors.InPastMessageKey);
        if (nights < rule.MinNights)
            throw new DomainRuleException(OwnerStayErrors.TooShort, OwnerStayErrors.TooShortMessageKey, rule.MinNights);
        if (rule.MaxNights is { } longest && nights > longest)
            throw new DomainRuleException(OwnerStayErrors.TooLong, OwnerStayErrors.TooLongMessageKey, longest);

        var ahead = start.DayNumber - today.DayNumber;
        if (ahead < rule.MinAdvanceDays)
            throw new DomainRuleException(OwnerStayErrors.NoticeTooShort, OwnerStayErrors.NoticeTooShortMessageKey, rule.MinAdvanceDays);
        if (rule.MaxAdvanceDays is { } window && ahead > window)
            throw new DomainRuleException(OwnerStayErrors.BeyondWindow, OwnerStayErrors.BeyondWindowMessageKey, window);

        var lastNight = end.AddDays(-1);
        if ((rule.ActiveFrom is { } first && start < first) || (rule.ActiveUntil is { } last && lastNight > last))
            throw new DomainRuleException(OwnerStayErrors.OutsidePeriod, OwnerStayErrors.OutsidePeriodMessageKey);
        if (rule.Blackouts.Any(b => start <= b.To && lastNight >= b.From))
            throw new DomainRuleException(OwnerStayErrors.Blackout, OwnerStayErrors.BlackoutMessageKey);
    }

    /// <summary>The nights from <paramref name="start"/> to <paramref name="end"/> (excluded) in each calendar year.</summary>
    public static IReadOnlyDictionary<int, int> NightsByYear(DateOnly start, DateOnly end)
    {
        var byYear = new Dictionary<int, int>();
        for (var night = start; night < end; night = night.AddDays(1))
            byYear[night.Year] = byYear.GetValueOrDefault(night.Year) + 1;
        return byYear;
    }

    /// <summary>
    /// Refuses <paramref name="asked"/> nights that, added to the <paramref name="held"/> ones of the same calendar year (requested and approved stays of the
    /// owner profile on the property), pass <paramref name="allowance"/>. No allowance, no limit.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="OwnerStayErrors.AllowanceExceeded"/> with the allowance and the year.</exception>
    public static void EnsureAllowance(int? allowance, IReadOnlyDictionary<int, int> held, IReadOnlyDictionary<int, int> asked)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(asked);
        if (allowance is not { } limit)
            return;

        foreach (var (year, nights) in asked.OrderBy(pair => pair.Key))
        {
            if (held.GetValueOrDefault(year) + nights > limit)
                throw new DomainRuleException(OwnerStayErrors.AllowanceExceeded, OwnerStayErrors.AllowanceExceededMessageKey, limit, year);
        }
    }

    /// <summary>
    /// How the calendar change of a block (or of its release) reads for the stay. <paramref name="recorded"/> is false when no change was recorded at all
    /// (nothing records the changes): the block lives in CasaZen only and is acknowledged. A change in CasaZen only (<c>Applied</c>) or accepted by the
    /// channel manager (<c>Sent</c>, <c>Confirmed</c>) is acknowledged; one to send or being sent is pending, and so is a change that cannot be read; one
    /// refused in whole or in part has failed.
    /// </summary>
    public static OwnerStaySyncState SyncOf(bool recorded, CalendarChangeState? state)
    {
        if (!recorded)
            return OwnerStaySyncState.Acknowledged;

        return state switch
        {
            CalendarChangeState.Applied or CalendarChangeState.Sent or CalendarChangeState.Confirmed => OwnerStaySyncState.Acknowledged,
            CalendarChangeState.PartiallySent or CalendarChangeState.Failed => OwnerStaySyncState.Failed,
            _ => OwnerStaySyncState.Pending,
        };
    }

    /// <summary>How a stay reads (<see cref="OwnerStayStates"/>): an approved stay is «confirmed» only once its block is acknowledged.</summary>
    public static string StateOf(OwnerStayStatus status, OwnerStaySyncState liveSync) => status switch
    {
        OwnerStayStatus.Requested => OwnerStayStates.Requested,
        OwnerStayStatus.Approved => liveSync switch
        {
            OwnerStaySyncState.Acknowledged => OwnerStayStates.Confirmed,
            OwnerStaySyncState.Failed => OwnerStayStates.SyncFailed,
            _ => OwnerStayStates.ApprovedSyncing,
        },
        OwnerStayStatus.Rejected => OwnerStayStates.Rejected,
        _ => OwnerStayStates.Cancelled,
    };

    /// <summary>The nights of a calendar, one per day in order, put together into runs of the same kind (and of the same stay).</summary>
    public static IReadOnlyList<OwnerCalendarRange> Compress(IEnumerable<OwnerCalendarRange> nights)
    {
        ArgumentNullException.ThrowIfNull(nights);
        var ranges = new List<OwnerCalendarRange>();
        foreach (var night in nights)
        {
            if (ranges.Count > 0
                && ranges[^1] is var last
                && last.End == night.Start
                && last.Kind == night.Kind
                && last.StayId == night.StayId
                && last.StayState == night.StayState)
            {
                ranges[^1] = last with { End = night.End };
                continue;
            }

            ranges.Add(night);
        }

        return ranges;
    }

    private static void Check(bool valid, string field)
    {
        if (!valid)
            throw new DomainRuleException(OwnerStayErrors.RuleInvalid, OwnerStayErrors.RuleInvalidMessageKey, field);
    }
}
