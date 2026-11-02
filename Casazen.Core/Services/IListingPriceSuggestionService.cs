using Casazen.Core.Entities;
using Casazen.Core.Pricing;

namespace Casazen.Core.Services;

/// <summary>
/// «Calcola con l'AI» of the step «Prezzi» of the wizard «Aggiungi immobile» (WIZ-AI-PRICE): the AI proposes the nightly rate of a
/// property, and the cleaning fee, the weekend surcharge and the minimum stay that go with it, from the data of the property, its
/// comune (never the address) and the nightly rates of the other homes published on CasaZen in the same comune, only summed up. The
/// host reads it and decides: nothing is written to the property here. Without an AI, the answer says why, and carries a price
/// worked out from the figures of the similar homes when there are enough of them.
/// </summary>
public interface IListingPriceSuggestionService
{
    /// <summary>
    /// True when an AI provider could answer now: the one in effect is not the stub and has a key. The step shows the question
    /// «Vuoi che calcoli io il prezzo con l'AI?» only then, so no host is offered something that cannot work. A budget that runs out
    /// later, or a provider that does not answer, are told by the suggestion itself.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The suggestion for <paramref name="property"/> (already authorized by the caller). <paramref name="language"/> is the
    /// language of the explanation, <c>it</c> or <c>en</c>. An answer of the AI for the same data is reused for a while.
    /// </summary>
    Task<NightlyPriceSuggestion> SuggestAsync(
        Property property,
        PricePositioning positioning,
        string language,
        CancellationToken cancellationToken = default);
}
