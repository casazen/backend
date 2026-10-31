using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The other suppliers offered to the customer of a request nobody answered (SP-10c), as a test decides them: the alternatives it gives, and a record
/// of what was asked. The real selection (the ranking, the showcase, the rules) is tested on its own in <c>ShowcaseAlternativesServiceTests</c>.
/// </summary>
internal sealed class FakeShowcaseAlternatives(params ShowcaseAlternative[] alternatives) : IShowcaseAlternativesService
{
    /// <summary>What the service answers.</summary>
    public IReadOnlyList<ShowcaseAlternative> Answer { get; set; } = alternatives;

    /// <summary>When set, the service fails with it (the mail and the view must not).</summary>
    public Exception? Failure { get; set; }

    /// <summary>The queries made, in order.</summary>
    public List<ShowcaseAlternativesQuery> Queries { get; } = [];

    public Task<IReadOnlyList<ShowcaseAlternative>> FindAsync(ShowcaseAlternativesQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Failure is { } failure ? Task.FromException<IReadOnlyList<ShowcaseAlternative>>(failure) : Task.FromResult(Answer);
    }

    /// <summary>An alternative as a test writes one: a verified supplier that answers in half an hour, with a service from 80 euro.</summary>
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
