using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Tests.Unit.Services;

internal sealed class FakeShowcaseAlternatives(params ShowcaseAlternative[] alternatives) : IShowcaseAlternativesService
{
    public IReadOnlyList<ShowcaseAlternative> Answer { get; set; } = alternatives;

    public Exception? Failure { get; set; }

    public List<ShowcaseAlternativesQuery> Queries { get; } = [];

    public Task<IReadOnlyList<ShowcaseAlternative>> FindAsync(ShowcaseAlternativesQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Failure is { } failure ? Task.FromException<IReadOnlyList<ShowcaseAlternative>>(failure) : Task.FromResult(Answer);
    }

    public static ShowcaseAlternative Supplier(string name, string slug) =>
        new(
            name,
            slug,
            $"https://storage.test/{slug}/cover.jpg",
            "Il nostro furgone",
            Verified: true,
            MedianResponseMinutes: 30,
            new ShowcaseAlternativeService("pulizia-casa", "Pulizia casa", 8000, Casazen.Core.Entities.Enums.SupplierServicePriceUnit.PerJob, RequiresQuote: false));
}
