using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The price and the rules of the nights of the properties (E08/F05/US01/T02, NF-36, NF-78): the store of the <see cref="Entities.PropertyDayRule"/>
/// rows, and the one read the calendar screens use for the price and the rules of every day. It adds no occupancy rule of its own: a night is taken as
/// the public calendar says (<c>PublicOccupancyReader</c>), and the price of a night is <see cref="StayPricing.NightPrice"/> fed with the prices the host
/// set for dates (<see cref="DayRulePricing.HostPrices"/>), so the calendar, the quote and the checkout agree to the cent.
/// </summary>
/// <remarks>
/// <para><b>Reach.</b> The services never decide on roles, but every read of the host and every write takes the <see cref="HostScope"/> of the caller and narrows the
/// properties by it, in SQL, like every list of the host (<c>InScope</c>): <see cref="GetDaysAsync"/> leaves out the properties the caller does not reach,
/// <see cref="GetRulesAsync"/> and <see cref="ApplyAsync"/> answer a property the caller does not reach (another org's, or one a collaborator limited to some
/// properties was not given) as one that does not exist. A caller cannot forget the check: there is no way in without a scope.</para>
/// <para><b>No endpoint writes rules here.</b> The host writes them through the changes API (E08/F05/US02/T03), which calls <see cref="ApplyAsync"/>.</para>
/// </remarks>
public interface IPropertyDayRuleService
{
    /// <summary>
    /// For each short-rent property the caller reaches (the same <see cref="HostScope"/> narrowing, the same limits and the same 422 codes as
    /// <see cref="IHostCalendarService"/>: at most <see cref="HostCalendarLimits.MaxWindowDays"/> days and
    /// <see cref="HostCalendarLimits.MaxProperties"/> properties) and for each night from <see cref="CalendarDaysQuery.From"/> to
    /// <see cref="CalendarDaysQuery.To"/>, both included: its price and where it comes from, the stay and advance rules in force, whether the night is
    /// taken. One query for each kind of data for all the properties, never one per property. A property the caller does not reach is left out, as if
    /// it did not exist.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="HostCalendarErrorCodes.WindowTooLarge"/> or <see cref="HostCalendarErrorCodes.TooManyProperties"/> (422).
    /// </exception>
    Task<CalendarDays> GetDaysAsync(HostScope scope, CalendarDaysQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules of one property from <paramref name="from"/> (included) to <paramref name="toExclusive"/> (excluded), by night: only the nights that
    /// have a row. For the host: the property is looked up through the tenant filter and the <paramref name="scope"/> of the caller.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist for the caller (another org, outside its reach, or deleted).</exception>
    Task<IReadOnlyDictionary<DateOnly, DayRuleValues>> GetRulesAsync(
        HostScope scope,
        Guid propertyId,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules of the houses <paramref name="propertyIds"/> from <paramref name="from"/> (included) to <paramref name="toExclusive"/> (excluded), in
    /// one read: for each house that has any, its nights. A public read that ignores the tenant filter, as
    /// <see cref="IPricingAdapterService.GetAppliedNightlyPricesAsync"/> does: the rules of a property are public data of its booking site (the price,
    /// the minimum stay, the closed arrival are what the calendar and the checkout show a guest). Nothing but the values is returned; the callers
    /// name properties of the site they serve (published houses).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>> GetRulesForSiteAsync(
        IReadOnlyCollection<Guid> propertyIds,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages in the context the upsert of one row for each night of <paramref name="from"/> to <paramref name="toInclusive"/> that the change touches:
    /// every night, or only the weekdays of <paramref name="days"/> (<c>null</c> = every day; an empty set is a 422, never «all»). Nights before today
    /// of the property are skipped and counted; a night whose values do not change is counted and left alone; a row left with no value is deleted. The
    /// values are validated night by night against the merged values (the patch over what the night has), and everything is checked before anything is
    /// staged, so a refused change stages nothing. When every night the change touches is already over (and none is left to change or to leave
    /// as it is) the change is refused with <see cref="DayRuleErrorCodes.InPast"/>.
    /// </summary>
    /// <remarks>
    /// It never calls <c>SaveChanges</c>: the caller owns the transaction and saves the staged rows with its own. When that save fails because a
    /// concurrent change inserted the same night first (<see cref="IsDateConflict"/>), the caller forgets what was staged (<see cref="DiscardStaged"/>)
    /// and stages again once: the rows exist then and are updated.
    /// </remarks>
    /// <param name="userId">The Auth0 subject of the account that changes the rules; <c>null</c> for a change of the system.</param>
    /// <exception cref="Exceptions.DomainRuleException">
    /// A code of <see cref="DayRuleErrorCodes"/>: the range, the weekdays, the patch or a merged value is not allowed.
    /// </exception>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist for the caller (another org, outside its reach, or deleted).</exception>
    Task<DayRuleApplyResult> ApplyAsync(
        HostScope scope,
        Guid propertyId,
        DateOnly from,
        DateOnly toInclusive,
        IReadOnlySet<DayOfWeek>? days,
        DayRulePatch patch,
        string? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets the rules <see cref="ApplyAsync"/> staged and the caller did not save, so the change can be staged again.</summary>
    void DiscardStaged();

    /// <summary>
    /// True when <paramref name="exception"/>, raised by the save of the rules staged by <see cref="ApplyAsync"/>, is the database refusing a second row
    /// for a night that has one: another change inserted it first.
    /// </summary>
    bool IsDateConflict(Exception exception);
}

/// <param name="From">First day of the range.</param>
/// <param name="To">Last day of the range, included.</param>
/// <param name="PropertyIds">
/// Only these properties of the caller's reach (the ones it does not reach are left out, as if they did not exist); null or empty = the first
/// <see cref="HostCalendarLimits.MaxProperties"/> by name.
/// </param>
public sealed record CalendarDaysQuery(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? PropertyIds = null);

/// <summary>The days of the properties a caller reaches, as the calendar screens read them.</summary>
/// <param name="Today">Today in Europe/Rome, as in <see cref="HostCalendar.Today"/>; each property carries its own time zone.</param>
/// <param name="Properties">The properties, by name.</param>
public sealed record CalendarDays(DateOnly Today, IReadOnlyList<CalendarDaysProperty> Properties);

/// <summary>One property of <see cref="CalendarDays"/> with its nights.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Timezone">The IANA time zone of the property: the clock its «today» is read on. A stay date is a calendar date of the property and is never converted.</param>
/// <param name="Currency">ISO 4217 code of the property (<see cref="StayCurrency"/>): the price of every day is in cents of it.</param>
/// <param name="Mirrored">The property books into the channel manager (<see cref="IOctorateBookingTargets.LookupAsync"/> finds a target): its changes can be sent there.</param>
/// <param name="Days">One entry for each night from the first to the last day asked, in order.</param>
public sealed record CalendarDaysProperty(
    Guid PropertyId,
    string Timezone,
    string Currency,
    bool Mirrored,
    IReadOnlyList<CalendarDay> Days);

/// <summary>One night of a property: the night that starts on <paramref name="Date"/>.</summary>
/// <param name="Date">The calendar date of the property the night starts on, as written.</param>
/// <param name="PriceCents">What the night costs, in cents: <see cref="StayPricing.NightPrice"/> of the night, the number of the quote of a one-night stay.</param>
/// <param name="PriceSource">Where the price comes from: a day rule, a confirmed seasonal price, the weekend surcharge or the nightly rate.</param>
/// <param name="MinNights">The fewest nights of a stay that starts on this night: the rule, else the minimum of the property, else 1.</param>
/// <param name="MaxNights">The most nights of a stay that starts on this night; <c>null</c> = no maximum.</param>
/// <param name="ClosedToArrival">A stay cannot start on this night.</param>
/// <param name="ClosedToDeparture">A stay cannot end on this day.</param>
/// <param name="MinAdvanceDays">The fewest days ahead a stay may be booked; <c>null</c> = no minimum.</param>
/// <param name="MaxAdvanceDays">The most days ahead a stay may be booked; <c>null</c> = no maximum.</param>
/// <param name="HasRule">The night has a rule of its own (a row).</param>
/// <param name="Taken">A booking, a hold, a request or a block takes the night (what the public calendar calls not available).</param>
public sealed record CalendarDay(
    DateOnly Date,
    long PriceCents,
    DayPriceSource PriceSource,
    int MinNights,
    int? MaxNights,
    bool ClosedToArrival,
    bool ClosedToDeparture,
    int? MinAdvanceDays,
    int? MaxAdvanceDays,
    bool HasRule,
    bool Taken);

/// <summary>What <see cref="IPropertyDayRuleService.ApplyAsync"/> staged.</summary>
/// <param name="Changed">The nights whose row was added, changed or deleted.</param>
/// <param name="Unchanged">The nights the patch leaves as they are.</param>
/// <param name="SkippedPast">The nights before today of the property, left alone.</param>
public sealed record DayRuleApplyResult(int Changed, int Unchanged, int SkippedPast);
