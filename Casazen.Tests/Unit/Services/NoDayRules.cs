using Casazen.Core.Authorization;
using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The day rules of a test that is not about them (E08/F05/US01): no night of any property has a rule, so nothing is refused and no price changes.
/// <c>BookingService</c>, <c>PublicCalendarService</c> and <c>PublicSiteSearchService</c> need one: they read the rules of the nights they judge.
/// </summary>
internal sealed class NoDayRules : IPropertyDayRuleService
{
    public static NoDayRules Instance { get; } = new();

    public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>> GetRulesForSiteAsync(
        IReadOnlyCollection<Guid> propertyIds,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>>(
            new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>());

    public Task<CalendarDays> GetDaysAsync(HostScope scope, CalendarDaysQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A test that reads the days of the calendar builds the real service.");

    public Task<IReadOnlyDictionary<DateOnly, DayRuleValues>> GetRulesAsync(
        HostScope scope,
        Guid propertyId,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<DateOnly, DayRuleValues>>(new Dictionary<DateOnly, DayRuleValues>());

    public Task<DayRuleApplyResult> ApplyAsync(
        HostScope scope,
        Guid propertyId,
        DateOnly from,
        DateOnly toInclusive,
        IReadOnlySet<DayOfWeek>? days,
        DayRulePatch patch,
        string? userId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A test that writes day rules builds the real service.");

    public void DiscardStaged()
    {
    }

    public bool IsDateConflict(Exception exception) => false;
}
