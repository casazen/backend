using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

public interface IShowcaseAlternativesService
{
    Task<IReadOnlyList<ShowcaseAlternative>> FindAsync(ShowcaseAlternativesQuery query, CancellationToken cancellationToken = default);
}
