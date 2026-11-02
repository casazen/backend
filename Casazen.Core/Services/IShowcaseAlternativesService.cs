using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The other suppliers offered to the customer of a request on measure, or of a booking, that nobody answered in time (SP-10c): at most three,
/// that cover the comune and have the category of the request, whose showcase is published, not paused and takes bookings, best first by the one
/// ranking of the platform (<see cref="ISupplierMatchService.RankActiveAsync"/>, the same score that recommends a supplier to a host). They are
/// only ever read in the context of a request: there is no list of suppliers for the public.
/// </summary>
public interface IShowcaseAlternativesService
{
    /// <summary>
    /// The alternatives for <paramref name="query"/>, best first, never the supplier of the request. Empty when there is nothing to offer (and
    /// then the customer is told so, honestly): no place or category, no other supplier, none that takes bookings. Read-only.
    /// </summary>
    Task<IReadOnlyList<ShowcaseAlternative>> FindAsync(ShowcaseAlternativesQuery query, CancellationToken cancellationToken = default);
}
