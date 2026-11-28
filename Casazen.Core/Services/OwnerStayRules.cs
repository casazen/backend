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
    public const int DefaultCalendarDays = 90;

    public const int MaxCalendarDays = 366;

    public const int MaxBlackouts = 50;

    public const int MaxRuleDays = 3660;

    public const int MaxYearlyNights = 366;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OwnerStayRuleView DefaultFor(Guid propertyId) =>
        new(null, propertyId, null, null, 0, null, 1, null, true, null, null, [], null);

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

    public static OwnerStayRuleView EffectiveFor(Guid propertyId, Guid propertyOwnerId, IEnumerable<OwnerStayRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var ofProperty = rules.Where(r => r.PropertyId == propertyId).ToList();
        var rule = ofProperty.FirstOrDefault(r => r.PropertyOwnerId == propertyOwnerId) ?? ofProperty.FirstOrDefault(r => r.PropertyOwnerId is null);
        return rule is null ? DefaultFor(propertyId) : ViewOf(rule);
    }

    public static IReadOnlyList<OwnerStayBlackout> ReadBlackouts(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<OwnerStayBlackout>>(json, Json) ?? [];

    public static string WriteBlackouts(IReadOnlyList<OwnerStayBlackout> blackouts)
    {
        ArgumentNullException.ThrowIfNull(blackouts);
        return JsonSerializer.Serialize(blackouts.OrderBy(b => b.From).ThenBy(b => b.To).ToList(), Json);
    }

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

    public static IReadOnlyDictionary<int, int> NightsByYear(DateOnly start, DateOnly end)
    {
        var byYear = new Dictionary<int, int>();
        for (var night = start; night < end; night = night.AddDays(1))
            byYear[night.Year] = byYear.GetValueOrDefault(night.Year) + 1;
        return byYear;
    }

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
