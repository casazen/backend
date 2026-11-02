using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IShowcaseTextSuggestionService"/>
/// <remarks>
/// <para><b>What goes to the provider.</b> Category names, the names of the comuni the supplier covers, the names it gave its services, what it
/// typed for the AI and the texts already in its draft: all of it scrubbed of e-mails, phone numbers and fiscal codes (<see cref="PiiScrubber"/>,
/// FD-21). Never the legal name, a person, a contact, a price, a time or a count of jobs. The data is read here, from the supplier org of the caller:
/// the request carries only what the supplier typed.</para>
/// <para><b>What comes back.</b> The answer of the model is a draft: <see cref="ShowcaseTextRules"/> keeps only the members that fit their limits,
/// are plain text and make no claim the supplier did not make (the rules of the static starter, SP-13d, VT-D20). A member that fails is
/// reported in <see cref="ShowcaseTextSuggestion.Rejected"/>; when none survives the request is a 422.</para>
/// <para><b>Cost.</b> Behind the platform budget (<see cref="IAiProvider"/>) and the per-user and per-org rate limit of the endpoint; an answer that
/// gave something usable is kept per supplier for the next identical request, and "regenerate" skips it.</para>
/// </remarks>
public sealed partial class ShowcaseTextSuggestionService(
    ISupplierService suppliers,
    ISupplierServiceCatalogService catalog,
    IComuneDirectory comuni,
    ISupplierShowcaseContentService content,
    IAiProvider ai,
    IAiRuntimeSettings settings,
    IAiResponseCache cache,
    ILogger<ShowcaseTextSuggestionService> logger) : IShowcaseTextSuggestionService
{
    /// <summary>The most zones and service names the prompt carries: more would not make a better text.</summary>
    private const int MaxListed = 12;

    private const int MaxZoneLength = 60;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        (await settings.GetEffectiveAsync(cancellationToken)).IsExternalProviderActive();

    public async Task<ShowcaseTextSuggestion> SuggestAsync(
        Guid supplierOrgId,
        ShowcaseTextSuggestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await IsAvailableAsync(cancellationToken))
            throw ShowcaseContentErrors.AiNotAvailable();

        var fields = ShowcaseTextFields.Normalize(request.Fields);
        var answers = ScrubAnswers(request.Answers ?? ShowcaseTextAnswers.None);
        var facts = await GatherFactsAsync(supplierOrgId, cancellationToken);
        if (facts.IsEmpty && answers.IsEmpty)
            throw ShowcaseContentErrors.NotEnoughData();

        var prompt = ShowcaseTextPrompt.Build(facts, answers, fields);
        var cacheKey = ShowcaseTextPrompt.CacheKey(prompt);

        AiGenerationResult? result = null;
        if (!request.Regenerate)
            cache.TryGet(supplierOrgId, cacheKey, out result);

        result ??= await GenerateAsync(prompt, cacheKey, cancellationToken);

        var outcome = ShowcaseTextRules.Check(result.Content, fields, SupplierWritten(facts, answers));
        if (outcome.IsEmpty)
        {
            // Field names only: the texts of a supplier never reach a log.
            logger.LogWarning("Showcase text suggestion dropped by the checks, members: {Fields}", string.Join(",", outcome.Rejected));
            throw ShowcaseContentErrors.UnusableSuggestion();
        }

        // Only an answer that gave something usable is kept for the next identical request.
        cache.Set(supplierOrgId, cacheKey, result);

        return new ShowcaseTextSuggestion(
            outcome.Tagline,
            outcome.Intro,
            outcome.Bio,
            outcome.Prep,
            new ShowcaseTextBasis(facts.Categories, facts.Zones, facts.Services, answers.Keys()),
            outcome.Rejected);
    }

    private async Task<AiGenerationResult> GenerateAsync(string prompt, string cacheKey, CancellationToken cancellationToken)
    {
        AiGenerationResult result;
        try
        {
            result = await ai.GenerateAsync(prompt, AiModelTier.Standard, cacheKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DomainException))
        {
            // A provider that fails is "not available now" for the editor, which keeps the static starter. The budget exception is a
            // DomainException and goes through: it has its own answer (422 ai_budget_exhausted).
            logger.LogWarning(ex, "The AI provider failed on a showcase text suggestion");
            throw ShowcaseContentErrors.AiNotAvailable();
        }

        if (!result.ProviderConfigured || string.IsNullOrWhiteSpace(result.Content))
            throw ShowcaseContentErrors.AiNotAvailable();

        return result;
    }

    /// <summary>The true facts of the supplier and the texts of its draft, read from its own org and scrubbed.</summary>
    private async Task<ShowcaseTextFacts> GatherFactsAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        var profile = await suppliers.GetProfileAsync(supplierOrgId, cancellationToken);
        var listings = await catalog.ListAsync(supplierOrgId, cancellationToken);
        var view = await content.GetAsync(supplierOrgId, cancellationToken);

        var categories = ReadStrings(profile?.CategoriesJson)
            .Concat(listings.Select(listing => listing.Category))
            .Where(code => ServiceCategories.All.Contains(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var services = listings
            .Select(listing => Clean(listing.Name))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListed)
            .ToList();

        var draft = view.Draft;
        var current = new ShowcaseTextCurrent(
            Clean(draft.Tagline),
            Clean(draft.Intro),
            Clean(draft.Bio, paragraphs: true),
            draft.Prep.Select(item => Clean(item)).OfType<string>().ToList());

        return new ShowcaseTextFacts(categories, await ZonesAsync(profile, cancellationToken), services, current);
    }

    /// <summary>The comuni the supplier covers by name: the ones chosen from the official list, then what it wrote as text (names, not old codes).</summary>
    private async Task<List<string>> ZonesAsync(SupplierProfile? profile, CancellationToken cancellationToken)
    {
        var zones = new List<string>();
        if (profile is null)
            return zones;

        var codes = ReadStrings(profile.ComuneIstatCodesJson);
        var listed = codes.Count == 0
            ? new Dictionary<string, Comune>()
            : (await comuni.GetByIstatCodesAsync(codes, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var code in codes)
        {
            if (listed.TryGetValue(code, out var comune))
                zones.Add(comune.Name);
        }

        foreach (var written in ReadStrings(profile.ComuniJson))
        {
            var name = Clean(written);
            if (name is null || name.Length > MaxZoneLength || CodeLike().IsMatch(name) || zones.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            zones.Add(name);
        }

        return zones.Take(MaxListed).ToList();
    }

    /// <summary>Everything the supplier wrote itself: the only place where a claim of a forbidden kind may come from (<see cref="ShowcaseTextRules"/>).</summary>
    private static IEnumerable<string?> SupplierWritten(ShowcaseTextFacts facts, ShowcaseTextAnswers answers) =>
        new[] { answers.About, answers.Years, answers.Specialties, answers.Differentiator, facts.Current.Tagline, facts.Current.Intro, facts.Current.Bio }
            .Concat(facts.Services)
            .Concat(facts.Current.Prep);

    private static ShowcaseTextAnswers ScrubAnswers(ShowcaseTextAnswers answers) =>
        new ShowcaseTextAnswers(
            Scrub(answers.About),
            Scrub(answers.Years),
            Scrub(answers.Specialties),
            Scrub(answers.Differentiator)).Cleaned();

    /// <summary>A text of the supplier as the prompt carries it: contact data taken out, then cleaned; <c>null</c> when nothing is left.</summary>
    private static string? Clean(string? text, bool paragraphs = false)
    {
        var scrubbed = Scrub(text);
        return paragraphs ? ShowcaseText.Paragraphs(scrubbed) : ShowcaseText.Line(scrubbed);
    }

    /// <summary>The text without e-mail addresses, phone numbers and fiscal codes (<see cref="PiiScrubber"/>), and without the placeholders it leaves.</summary>
    private static string? Scrub(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Placeholder().Replace(PiiScrubber.Scrub(text), string.Empty);

    [GeneratedRegex(@"\[(?:tel|email|cf)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static List<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>An old code in the free-text zones («H501»), an ISTAT code or a postal code: not a name a customer reads.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z]\d{3}|\d{5,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeLike();
}
