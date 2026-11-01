using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The AI help of the showcase editor (<c>POST api/supplier/showcase/texts/suggestion</c>): drafts for the sentence in large type, the
/// introduction, "chi siamo" and the tips "prima che arriviamo" of the supplier's own showcase, written from what is true of the supplier
/// (its categories, its zones, the names of its services, what it typed about itself) and checked against the rules of the static starter
/// (<see cref="ShowcaseTextRules"/>). The guarantee, the people, the strengths, the certifications and the questions are never drafted.
/// A suggestion changes nothing by itself: the editor shows it, the supplier applies it to the draft, and nothing is public before the
/// publication.
/// </summary>
public interface IShowcaseTextSuggestionService
{
    /// <summary>
    /// True when an external AI provider can answer now (not the stub, a key and an address configured): when it is not, the editor keeps the
    /// static starter and shows no AI help.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the drafts of <paramref name="request"/> for the supplier org (the caller's own supplier link, never a value of the request).
    /// The supplier's data is read here: the editor sends only what the supplier typed for the AI.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseContentErrors.AiUnavailable"/> (no provider, or it failed), <see cref="ShowcaseContentErrors.SuggestionNotEnoughData"/>
    /// (nothing true to write from) or <see cref="ShowcaseContentErrors.SuggestionUnusable"/> (every member was dropped by the checks).
    /// </exception>
    /// <exception cref="Exceptions.AiBudgetExceededException">The monthly platform AI budget is spent (422 <c>ai_budget_exhausted</c>).</exception>
    Task<ShowcaseTextSuggestion> SuggestAsync(
        Guid supplierOrgId,
        ShowcaseTextSuggestionRequest request,
        CancellationToken cancellationToken = default);
}
