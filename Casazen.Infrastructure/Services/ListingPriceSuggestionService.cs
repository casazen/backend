using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Pricing;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The nightly price of «Calcola con l'AI» (WIZ-AI-PRICE, <see cref="IListingPriceSuggestionService"/>). The prompt carries the facts of
/// the property, its comune and, when at least <see cref="PriceSuggestionLimits.MinSimilarListings"/> homes qualify, the figures of the
/// nightly rates of the other homes published in the same comune: summed up, so no listing and no owner can be read in them. The answer
/// is checked (<see cref="PriceSuggestionRules.Accept"/>) before the host sees it; a wrong answer, a missing provider and an exhausted
/// budget are never errors for the host: the answer says why the AI is not behind the price, and brings the figures' price when it can.
/// </summary>
public class ListingPriceSuggestionService(
    AppDbContext db,
    IAiProvider aiProvider,
    IAiResponseCache aiCache,
    IAiRuntimeSettings aiSettings,
    IComuneDirectory comuneDirectory,
    ILogger<ListingPriceSuggestionService> logger) : IListingPriceSuggestionService
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        (await aiSettings.GetEffectiveAsync(cancellationToken)).IsExternalProviderActive();

    public async Task<NightlyPriceSuggestion> SuggestAsync(
        Property property,
        PricePositioning positioning,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        var comune = await comuneDirectory.FindByIstatCodeAsync(property.ComuneIstatCode, activeOnly: false, cancellationToken);
        var facts = ListingPriceSuggestionPrompt.FactsOf(property, comune);
        var market = await LoadMarketAsync(property, cancellationToken);
        var basis = new PriceSuggestionBasis(
            facts.Comune,
            facts.Province,
            facts.Region,
            market?.Count ?? 0,
            facts.Amenities.Count,
            ListingPriceSuggestionPrompt.UsedFields(facts));

        var prompt = ListingPriceSuggestionPrompt.Build(facts, market, positioning, language);
        var (answer, reason) = await AskAiAsync(property.OrgId, prompt, market, cancellationToken);

        NightlyPriceSuggestion suggestion;
        if (answer is not null)
        {
            suggestion = new NightlyPriceSuggestion(
                true,
                null,
                answer.NightlyRate,
                answer.RangeMin,
                answer.RangeMax,
                answer.CleaningFee,
                answer.WeekendSurchargePercent,
                answer.MinNights,
                answer.Rationale,
                basis);
        }
        else if (market is not null)
        {
            suggestion = PriceSuggestionRules.FromMarket(market, positioning, basis, reason!);
        }
        else
        {
            suggestion = new NightlyPriceSuggestion(false, reason, null, null, null, null, null, null, null, basis);
        }

        logger.LogInformation(
            "Price suggestion for property {PropertyId}: ai={AiUsed} reason={Reason} similarListings={SimilarListings} priced={Priced}",
            property.Id, suggestion.AiUsed, suggestion.Reason, basis.SimilarListings, suggestion.NightlyRate is not null);
        return suggestion;
    }

    /// <summary>
    /// What the AI says, once checked; or why there is none. A provider that is not configured, a budget that is gone, a failure and an
    /// answer that is not a believable price each have their own reason, and none of them is thrown at the caller. A cancelled request is.
    /// Only an answer that passed the checks is kept for the next time: a bad one would otherwise be served again.
    /// </summary>
    private async Task<(AcceptedPriceAnswer? Answer, string? Reason)> AskAiAsync(
        Guid orgId,
        string prompt,
        MarketPriceStats? market,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey(prompt);
        try
        {
            var cached = aiCache.TryGet(orgId, cacheKey, out AiGenerationResult? ai) && ai is not null;
            if (!cached)
                ai = await aiProvider.GenerateAsync(prompt, AiModelTier.Economy, cacheKey, cancellationToken);

            if (!ai!.ProviderConfigured)
                return (null, PriceSuggestionFallbackReasons.AiNotConfigured);

            var raw = PriceSuggestionAnswerParser.Parse(ai.Content);
            var accepted = raw is null ? null : PriceSuggestionRules.Accept(raw, market);
            if (accepted is null)
            {
                logger.LogWarning("The AI answer to the price of a property is not a believable price (parsed: {Parsed})", raw is not null);
                return (null, PriceSuggestionFallbackReasons.AiInvalidAnswer);
            }

            if (!cached)
                aiCache.Set(orgId, cacheKey, ai);

            return (accepted, null);
        }
        catch (AiBudgetExceededException)
        {
            return (null, PriceSuggestionFallbackReasons.AiBudgetExhausted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout of the provider is a TaskCanceledException too: it is the provider that did not answer, not the caller who left.
            logger.LogWarning(ex, "The AI provider failed to price a property");
            return (null, PriceSuggestionFallbackReasons.AiUnavailable);
        }
    }

    /// <summary>
    /// The figures of the other homes of the comune that the public site shows (the one rule of <see cref="PublicListing.IsPublished"/>,
    /// as the search of the site reads them across the orgs): never the host's own homes, never demo data. First the homes of a size like
    /// this one's (bedrooms and guests close), then, when they are too few, the homes of the comune of any size; <c>null</c> when even
    /// those are fewer than <see cref="PriceSuggestionLimits.MinSimilarListings"/>.
    /// </summary>
    private async Task<MarketPriceStats?> LoadMarketAsync(Property property, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(property.ComuneIstatCode))
            return null;

        var rows = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(PublicListing.IsPublished)
            .Where(p => p.OrgId != property.OrgId
                && !p.IsTestData
                && p.Org.IsActive
                && p.ComuneIstatCode == property.ComuneIstatCode
                && p.NightlyRate > 0)
            .OrderBy(p => p.Id)
            .Take(PriceSuggestionLimits.MaxSampleListings)
            .Select(p => new SampleRow(p.NightlyRate, p.Bedrooms, p.MaxGuests))
            .ToListAsync(cancellationToken);

        var similar = rows
            .Where(row => IsSimilarSize(row, property))
            .Select(row => row.NightlyRate)
            .ToList();

        return PriceSuggestionRules.Summarize(similar, similarSize: true)
            ?? PriceSuggestionRules.Summarize(rows.Select(row => row.NightlyRate), similarSize: false);
    }

    private static bool IsSimilarSize(SampleRow row, Property property) =>
        Math.Abs(row.Bedrooms - property.Bedrooms) <= PriceSuggestionLimits.SimilarBedroomsDelta
        && (property.MaxGuests <= 0 || row.MaxGuests <= 0
            || Math.Abs(row.MaxGuests - property.MaxGuests) <= PriceSuggestionLimits.SimilarGuestsDelta);

    /// <summary>The prompt holds every input (and the language), so its hash is the key of the answer: the same data, the same answer.</summary>
    private static string CacheKey(string prompt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return $"price-suggestion:{ListingPriceSuggestionPrompt.PromptVersion}:{Convert.ToHexString(hash)[..24]}";
    }

    private sealed record SampleRow(decimal NightlyRate, int Bedrooms, int MaxGuests);
}
