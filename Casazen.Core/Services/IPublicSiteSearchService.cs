using Casazen.Core.SiteProfile;

namespace Casazen.Core.Services;

/// <summary>
/// The search of the public booking site of one host (DB-07a): for the dates and the guests chosen, every house of the site with whether it
/// is free (or why not), what the stay costs, when the first free date is and, when nothing is free, nearby dates that are; the calendar
/// of the whole site for the «Quando» field; the places for «Dove»; the houses that look like another. One definition of price (the
/// quote's own: <see cref="StayPricing"/> and <see cref="TouristTax.TouristTaxCalculator"/>) and one of availability
/// (<see cref="PublicOccupancyReader"/>), read for all the houses in a handful of queries, never one call per house.
/// </summary>
public interface IPublicSiteSearchService
{
    Task<PublicStaysResult> SearchStaysAsync(Entities.Org org, PublicStaysCriteria criteria, CancellationToken cancellationToken = default);

    Task<PublicSiteCalendar> GetSiteCalendarAsync(
        Entities.Org org,
        DateOnly? from,
        DateOnly? to,
        int adults,
        int children,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicDestination>> GetDestinationsAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicStayCard>?> GetSimilarAsync(
        Guid orgId,
        string propertySlugOrId,
        int max,
        CancellationToken cancellationToken = default);

    Task<PublicStaysResult> SearchPlatformStaysAsync(PublicStaysCriteria criteria, CancellationToken cancellationToken = default);

    Task<PublicSiteCalendar> GetPlatformCalendarAsync(
        string? where,
        DateOnly? from,
        DateOnly? to,
        int adults,
        int children,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicDestination>> GetPlatformDestinationsAsync(CancellationToken cancellationToken = default);
}

public enum PublicStaysScope
{
    Site = 0,

    Platform = 1,
}

public static class PublicSiteSearch
{
    public const int MaxHouses = 50;

    public const int MaxPlatformHouses = 500;

    public const int DefaultPlatformPageSize = 24;

    public const int MaxPlatformDestinations = 100;

    public const int MaxPageSize = 50;

    public const int FirstFreeHorizonDays = 150;

    public const int FromPriceDays = 30;

    public const int MaxAlternatives = 3;

    public const int DefaultSimilar = 3;

    public const int MaxSimilar = 6;

    public const string Recommended = "recommended";

    public const string PriceAsc = "priceAsc";

    public const string PriceDesc = "priceDesc";

    public const string Rating = "rating";

    public const string Largest = "largest";
}

public static class PublicStayReasons
{
    public const string Busy = "busy";

    public const string MinNights = "min_nights";

    public const string ArrivalClosed = "arrival_closed";

    public const string DepartureClosed = "departure_closed";

    public const string MaxNights = "max_nights";

    public const string Advance = "advance";

    public const string Guests = "guests";

    public const string Pets = "pets";

    public const string Past = "past";

    public const string NotBookable = "not_bookable";
}

public static class PublicStayTaxStates
{
    public const string Exact = "exact";

    /// <summary>The tax of the comune with every child counted as a payer, because the list does not know their ages: the quote of the checkout gives the exact one.</summary>
    public const string Estimated = "estimated";

    public const string NotIncluded = "notIncluded";
}

public static class PublicSiteSearchErrorCodes
{
    public const string StaysDatesInvalid = "public_stays_dates_invalid";

    public const string CalendarRangeInvalid = "public_site_calendar_range_invalid";
}

/// <summary>What the visitor asked of the list (already read from the query, with the values that were not understood left out).</summary>
/// <param name="CheckIn">First night of the stay; <c>null</c> together with <paramref name="CheckOut"/> when no dates were chosen.</param>
/// <param name="CheckOut">Day of departure (excluded from the nights).</param>
/// <param name="Where">The place chosen: the id of a destination (<see cref="PublicDestination.Id"/>) or the name of a comune.</param>
/// <param name="MinPrice">Lowest price per night, in euros.</param>
/// <param name="MaxPrice">Highest price per night, in euros.</param>
/// <param name="Bedrooms">At least this many bedrooms.</param>
/// <param name="Bathrooms">At least this many bathrooms.</param>
/// <param name="MinSqm">At least this many square metres (a house that did not say its size is not shown).</param>
/// <param name="Amenities">Names of <see cref="Enums.PropertyAmenity"/>: the house has all of them.</param>
/// <param name="Rules"><c>pets</c>, <c>children</c>, <c>selfCheckin</c>, <c>smoking</c>, <c>parties</c>: the house allows all of them.</param>
/// <param name="Tags">Labels of the houses (<see cref="SitePropertyProfileLimits.Tags"/>): the house has all of them, the ones the host chose and the ones its data give.</param>
/// <param name="Sort">One of <see cref="PublicSiteSearch"/> (<c>recommended</c> by default).</param>
/// <param name="Page">First page is 1.</param>
/// <param name="PageSize"><c>null</c> for every house (at most <see cref="PublicSiteSearch.MaxPageSize"/>).</param>
/// <param name="IgnoredValues">Values of the filters that no house can match (an unknown amenity, rule or label), declared and not applied.</param>
/// <param name="Scope">
/// <see cref="PublicStaysScope.Platform"/> for the search across all the sites: the place is matched by what the visitor types (the comune or
/// the zone the host wrote contains it), the <c>recommended</c> sort puts the best rated first and the houses that are not free are not listed.
/// </param>
public sealed record PublicStaysCriteria(
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int Adults,
    int Children,
    bool Pets,
    string? Where,
    decimal? MinPrice,
    decimal? MaxPrice,
    int? Bedrooms,
    int? Bathrooms,
    int? MinSqm,
    IReadOnlyList<string> Amenities,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Tags,
    string Sort,
    int Page,
    int? PageSize,
    IReadOnlyList<string> IgnoredValues,
    PublicStaysScope Scope = PublicStaysScope.Site);

/// <summary>The list: the houses, the nearby dates when nothing is free, and what could not be applied.</summary>
/// <param name="CheckIn">The stay asked, <c>null</c> without dates.</param>
/// <param name="Nights">Length of the stay, <c>null</c> without dates.</param>
/// <param name="TotalCount">Houses that pass the filters (before the page).</param>
/// <param name="AvailableCount">Of those, the ones that are free for the stay (all of them when no dates were chosen).</param>
/// <param name="Items">The page of houses: the free ones first, in the sort asked, then the others by name.</param>
/// <param name="Alternatives">Up to three nearby ranges of the same length with free houses; empty when some house is free already.</param>
/// <param name="UnsupportedFilters">Filters that were asked and not applied: a value no house can match (<c>amenities:…</c>, <c>rules:…</c> or <c>tags:…</c> of an unknown name).</param>
/// <param name="Facets">Only on the platform search: what the houses of the place asked allow the controls to offer; <c>null</c> on a site.</param>
public sealed record PublicStaysResult(
    string Currency,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int? Nights,
    int Adults,
    int Children,
    bool Pets,
    int TotalCount,
    int AvailableCount,
    int Page,
    int PageSize,
    IReadOnlyList<PublicStayCard> Items,
    IReadOnlyList<PublicStayAlternative> Alternatives,
    IReadOnlyList<string> UnsupportedFilters,
    DateTime GeneratedAt,
    PublicStaysFacets? Facets = null);

/// <summary>
/// What the controls of the platform search need to know about the houses of the place asked (before the other filters): how far the
/// price and size sliders go, how many guests a house takes, whether any takes pets, and the labels with how many houses carry each.
/// </summary>
/// <param name="MinNightCents">The lowest base night rate among the houses; <c>null</c> without houses. Amounts of two currencies are not converted.</param>
/// <param name="MaxNightCents">The highest base night rate among the houses.</param>
/// <param name="MaxGuests">The most people any of the houses sleeps; 0 without houses.</param>
/// <param name="MinAreaSqm">The smallest size among the houses that said theirs.</param>
/// <param name="MaxAreaSqm">The largest size among the houses that said theirs.</param>
/// <param name="PetsAllowed">Some house accepts pets (welcome or on request).</param>
/// <param name="Tags">The labels of the houses with their counts, in the order of the vocabulary.</param>
/// <param name="Currency">The currency of the houses of the place when they share it (<c>CHF</c> in Ticino), else <c>EUR</c>: what the sliders are written in.</param>
public sealed record PublicStaysFacets(
    long? MinNightCents,
    long? MaxNightCents,
    int MaxGuests,
    int? MinAreaSqm,
    int? MaxAreaSqm,
    bool PetsAllowed,
    IReadOnlyList<PublicStayFacetTag> Tags,
    string Currency = PublicStayHouse.DefaultCurrency);

public sealed record PublicStayFacetTag(string Tag, int Count);

/// <summary>A house of the site as the list shows it. Nothing private: the position is rounded, there is no address.</summary>
/// <param name="Zone">The zone the host wrote (DB-11a), in Italian and, if written, English; <c>null</c> = not said.</param>
/// <param name="Latitude">
/// Rounded to about a kilometre (<see cref="Entities.PropertyAddress.ToPublicCoordinate"/>); for a house whose host gave no position, the
/// centre of its comune (<c>0, 0</c> only when the comune is not known either).
/// </param>
/// <param name="Photos">The first photos, the first is the cover.</param>
/// <param name="Amenities">The main ones, as the names of <see cref="Enums.PropertyAmenity"/>.</param>
/// <param name="Tags">Labels of the house: the ones the host chose and the ones its data give (a pool, pets welcome, the coast...), in the order of the vocabulary.</param>
/// <param name="Rating">The number and the average of the public reviews; <c>null</c> without any.</param>
/// <param name="Cancellation">
/// What cancelling costs for the stay asked, on the policy of the house, with the dates of the quote (last whole day, Europe/Rome); <c>null</c>
/// without dates and for a house with no cancellation policy.
/// </param>
/// <param name="OrgSlug">Only on the platform search: the slug of the booking site of the house (<c>/book/{orgSlug}/property/{slug}</c>); <c>null</c> on a site.</param>
/// <param name="HostName">Only on the platform search: how the host is named on the site (the name the host chose, else the name of the org).</param>
/// <param name="Country">ISO 3166-1 alpha-2 country of the house (<c>IT</c>, <c>CH</c>); its price is in the currency of that house (<see cref="PublicStayPrice.Currency"/>).</param>
public sealed record PublicStayCard(
    Guid Id,
    string? Slug,
    string Name,
    string Comune,
    LocalizedText? Zone,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyList<string> Photos,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    int? AreaSqm,
    IReadOnlyList<string> Amenities,
    IReadOnlyList<string> Tags,
    PublicRatingSummary? Rating,
    PublicStayAvailability Availability,
    PublicStayPrice Price,
    StayCancellation? Cancellation = null,
    string? OrgSlug = null,
    string? HostName = null,
    string? Country = null);

/// <summary>Whether a house is free for the stay asked.</summary>
/// <param name="Available">Free for the stay (and bookable); <c>true</c> when no dates were chosen, since there is nothing to be taken.</param>
/// <param name="Reason">Why not, one of <see cref="PublicStayReasons"/>; <c>null</c> when free.</param>
/// <param name="MinNights">
/// The minimum stay of the night the stay arrives on: the rule of that night (NF-36), else the minimum of the house; <c>null</c> for none. Without
/// dates, the minimum of the house.
/// </param>
/// <param name="FirstFreeCheckIn">
/// The first check-in day, not before the one asked and not before today, for which a stay as long as the one asked (and at least the
/// minimum stay of that night) is free and breaks no rule of its nights (closed arrival or departure, maximum stay, advance) within
/// <see cref="PublicSiteSearch.FirstFreeHorizonDays"/> days; the check-in asked when the house is free;
/// <c>null</c> when the reason is not a matter of dates or nothing is free in that time.
/// </param>
public sealed record PublicStayAvailability(bool Available, string? Reason, int? MinNights, DateOnly? FirstFreeCheckIn);

public sealed record PublicStayPrice(string Currency, long PerNightCents, long? TotalCents, string? IncludesTouristTax);

public sealed record PublicStayAlternative(DateOnly CheckIn, DateOnly CheckOut, int AvailableCount, long FromPerNightCents);

public sealed record PublicSiteCalendar(
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    string Currency,
    int Adults,
    int Children,
    IReadOnlyList<PublicSiteCalendarDay> Days,
    DateTime GeneratedAt);

public sealed record PublicSiteCalendarDay(DateOnly Date, bool AnyAvailable, long? FromPriceCents, int? MinNights);

public sealed record PublicDestination(string Id, string Label, string Comune, int Count);
