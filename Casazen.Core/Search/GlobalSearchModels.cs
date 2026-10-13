using System.Globalization;
using Casazen.Core.Authorization;

namespace Casazen.Core.Search;

/// <summary>
/// The kinds of result of the global search (UI-13a), in the order the answer lists its groups. A kind is the stable
/// <c>type</c> of a result and of its group; the clients translate it for the heading of the group.
/// </summary>
public static class SearchTypes
{
    /// <summary>A property of the org (name, city, CIN).</summary>
    public const string Property = "property";

    /// <summary>A booking (its code, or the name of its guest).</summary>
    public const string Booking = "booking";

    /// <summary>A guest of the org (name, e-mail): the e-mail is returned masked, never the phone or the document.</summary>
    public const string Guest = "guest";

    /// <summary>A lease (the name of a tenant).</summary>
    public const string Lease = "lease";

    /// <summary>A request to a supplier made by the host (the name of the service).</summary>
    public const string ServiceRequest = "service-request";

    /// <summary>A supplier the org already works with (it received at least one request of the org).</summary>
    public const string Supplier = "supplier";

    /// <summary>A request in the inbox of the supplier (the caller works as a supplier).</summary>
    public const string SupplierRequest = "supplier-request";

    /// <summary>Every kind, in the order of the groups of the answer.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Property, Booking, Guest, Lease, ServiceRequest, Supplier, SupplierRequest,
    ];
}

/// <summary>
/// Where a result opens, as a key and not as a URL: <c>{area}.{kind}</c>, the area being the context of the caller the object
/// belongs to (<c>short-rent</c>, <c>long-rent</c>, <c>supplier</c>). The client owns the routes and maps each key to its
/// route with the <c>id</c> of the result, so a route can change without the API changing.
/// </summary>
public static class SearchDestinations
{
    public const string ShortRentProperty = "short-rent.property";
    public const string LongRentProperty = "long-rent.property";
    public const string ShortRentBooking = "short-rent.booking";
    public const string ShortRentGuest = "short-rent.guest";
    public const string LongRentLease = "long-rent.lease";
    public const string ShortRentServiceRequest = "short-rent.service-request";
    public const string LongRentServiceRequest = "long-rent.service-request";
    public const string ShortRentSupplier = "short-rent.supplier";
    public const string SupplierRequest = "supplier.request";
}

/// <summary>Limits of the term and of the answer of <c>GET /api/search</c>.</summary>
public static class SearchLimits
{
    /// <summary>Results per group when the client does not say.</summary>
    public const int DefaultLimit = 5;

    /// <summary>The most results per group.</summary>
    public const int MaxLimit = 20;
}

/// <summary>Stable error codes of the search (UI-13a), with the key of their message in the resources.</summary>
public static class SearchErrorCodes
{
    /// <summary>400: the term has fewer than <see cref="SearchText.MinQueryLength"/> characters.</summary>
    public const string QueryTooShort = "search_query_too_short";

    /// <summary>SharedResources key of the message of <see cref="QueryTooShort"/> ({0} = the minimum).</summary>
    public const string QueryTooShortMessageKey = "SearchQueryTooShort";

    /// <summary>400: the term has more than <see cref="SearchText.MaxQueryLength"/> characters.</summary>
    public const string QueryTooLong = "search_query_too_long";

    /// <summary>SharedResources key of the message of <see cref="QueryTooLong"/> ({0} = the maximum).</summary>
    public const string QueryTooLongMessageKey = "SearchQueryTooLong";
}

/// <summary>One result: what the palette shows and where it opens. Nothing sensitive: see <see cref="SearchTypes"/> for what each kind carries.</summary>
/// <param name="Type">The kind of result (<see cref="SearchTypes"/>).</param>
/// <param name="Id">The id of the object, for the route of the destination.</param>
/// <param name="Title">A short title: a name, a code, a service.</param>
/// <param name="Subtitle">A line that tells it apart from the others (the property, the city, a masked e-mail), or null.</param>
/// <param name="Destination">The destination key (<see cref="SearchDestinations"/>).</param>
public sealed record SearchHit(string Type, Guid Id, string Title, string? Subtitle, string Destination);

/// <summary>The results of one kind. <paramref name="HasMore"/> says there are more than the limit asked for.</summary>
public sealed record SearchGroup(string Type, IReadOnlyList<SearchHit> Items, bool HasMore);

/// <summary>The answer of the search: the groups that have at least one result, in the order of <see cref="SearchTypes.All"/>.</summary>
public sealed record GlobalSearchResult(IReadOnlyList<SearchGroup> Groups)
{
    /// <summary>Nothing found (or nothing the caller may search).</summary>
    public static GlobalSearchResult Empty { get; } = new([]);
}

/// <summary>
/// What the caller may search as a host: its scope on the properties of its org (<see cref="HostScope"/>, from
/// <see cref="IHostScopeResolver"/>, never widened) and, for each kind, the permission it holds in the context the kind belongs
/// to. Decided by the web layer with the same policies as the endpoints (<c>CasazenPolicies</c>); the search service only reads it.
/// </summary>
/// <param name="Scope">The org and the properties the caller reaches.</param>
/// <param name="ShortRentProperties">Holds <c>property.read</c> in the short-rent context: properties in short-rent mode, their service requests, the suppliers.</param>
/// <param name="LongRentProperties">Holds <c>property.read</c> in the long-rent context: properties in long-term mode and their service requests.</param>
/// <param name="Bookings">Holds <c>booking.read</c> (short-rent).</param>
/// <param name="Guests">Holds <c>guest.read</c> (short-rent).</param>
/// <param name="Leases">Holds <c>lease.read</c> (long-rent).</param>
public sealed record HostSearchAccess(
    HostScope Scope,
    bool ShortRentProperties,
    bool LongRentProperties,
    bool Bookings,
    bool Guests,
    bool Leases);

/// <summary>A search to run: the term, the most results per group and what the caller may search.</summary>
/// <param name="Query">The term, parsed (<see cref="SearchText.Parse"/>).</param>
/// <param name="Limit">Results per group, 1 to <see cref="SearchLimits.MaxLimit"/>.</param>
/// <param name="Host">What the caller may search of its org; null when it has no host permission or no reach.</param>
/// <param name="SupplierOrgId">The supplier org of the caller when it works as a supplier (its inbox is searched); null otherwise.</param>
/// <param name="Culture">The language of the labels the service writes (the name of a service category).</param>
public sealed record GlobalSearchRequest(
    SearchQuery Query,
    int Limit,
    HostSearchAccess? Host,
    Guid? SupplierOrgId,
    CultureInfo Culture);
