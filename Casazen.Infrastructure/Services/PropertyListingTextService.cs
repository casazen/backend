using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// The listings of the same comune are read across orgs, as the public search reads them: only published listings
/// (<see cref="PublicListing.IsPublished"/>) of active orgs, and only what their public page shows (the headline, the kind, the spaces, the
/// amenities). Up to <see cref="SimilarListingsMax"/>, the same kind of place first, then the closest number of guests. The answers of the AI
/// are kept by <see cref="IAiResponseCache"/> in the scope of the org, under a key made of the prompt and the photos.
/// </remarks>
public sealed class PropertyListingTextService(
    AppDbContext db,
    IComuneDirectory comuni,
    IAiProvider aiProvider,
    IAiResponseCache cache,
    ILogger<PropertyListingTextService> logger) : IPropertyListingTextService
{
    /// <summary>Listings of the same comune the AI compares the property with.</summary>
    public const int SimilarListingsMax = 5;

    public async Task<ListingTextSuggestion> SuggestAsync(
        Property property,
        IReadOnlyList<AiImage> photos,
        bool regenerate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(photos);

        var comune = await comuni.FindByIstatCodeAsync(property.ComuneIstatCode, activeOnly: false, cancellationToken);
        var similar = await ReadSimilarListingsAsync(property, cancellationToken);
        var sent = photos.Take(ListingTextLimits.MaxPhotos).ToList();
        var facts = new ListingTextFacts(
            property.AccommodationType,
            property.MaxGuests,
            property.Bedrooms,
            property.Beds,
            property.Bathrooms,
            property.AreaSqm,
            property.Amenities,
            comune?.Name ?? NullIfBlank(property.City),
            comune?.ProvinceCode,
            comune?.RegionName,
            property.AreaKind,
            property.FloorLevel,
            property.PetsPolicy,
            property.SmokingPolicy,
            property.PartiesPolicy,
            property.CheckInFrom,
            property.CheckOutBy,
            property.ChildrenWelcome,
            sent.Count,
            similar);

        var request = new AiRequest(
            ListingTextPrompt.BuildPrompt(facts),
            sent,
            ListingTextPrompt.SystemPrompt,
            ListingTextPrompt.Temperature,
            ListingTextPrompt.MaxCompletionTokens);
        var cacheKey = CacheKey(request);
        if (!regenerate && cache.TryGet<ListingTextSuggestion>(property.OrgId, cacheKey, out var cached) && cached is not null)
            return cached;

        AiGenerationResult result;
        try
        {
            result = await aiProvider.GenerateAsync(request, AiModelTier.Standard, cacheKey, cancellationToken);
        }
        catch (AiBudgetExceededException)
        {
            logger.LogWarning("Listing text of property {PropertyId}: the platform AI budget is used up", property.Id);
            return Fallback(AiSuggestionFallbackReasons.AiBudgetExhausted, facts);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Listing text of property {PropertyId}: the AI provider failed ({Status})", property.Id, (int?)ex.StatusCode);
            return Fallback(AiSuggestionFallbackReasons.AiUnavailable, facts);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Listing text of property {PropertyId}: the AI provider did not answer in time", property.Id);
            return Fallback(AiSuggestionFallbackReasons.AiUnavailable, facts);
        }

        if (!result.ProviderConfigured)
            return Fallback(AiSuggestionFallbackReasons.AiNotConfigured, facts);

        if (!ListingTextPrompt.TryParseAnswer(result.Content, out var title, out var description))
        {
            logger.LogWarning(
                "Listing text of property {PropertyId}: the AI answer is not a headline and a description ({Length} characters)",
                property.Id, result.Content?.Length ?? 0);
            return Fallback(AiSuggestionFallbackReasons.AiInvalidAnswer, facts);
        }

        var suggestion = new ListingTextSuggestion(true, null, title, description, Basis(facts, result.ImagesRead));
        cache.Set(property.OrgId, cacheKey, suggestion);
        logger.LogInformation(
            "Listing text suggested for property {PropertyId}: {Images} photos read, {Similar} similar listings, {Tokens} tokens",
            property.Id, result.ImagesRead, similar.Count, result.PromptTokens + result.CompletionTokens);
        return suggestion;
    }

    private async Task<IReadOnlyList<SimilarListingFacts>> ReadSimilarListingsAsync(Property property, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(property.ComuneIstatCode))
            return [];

        var code = property.ComuneIstatCode;
        var id = property.Id;
        var type = property.AccommodationType;
        var guests = property.MaxGuests;
        var rows = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(PublicListing.IsPublished)
            .Where(p => p.Org.IsActive && p.ComuneIstatCode == code && p.Id != id)
            .OrderBy(p => p.AccommodationType == type ? 0 : 1)
            .ThenBy(p => Math.Abs(p.MaxGuests - guests))
            .ThenBy(p => p.Id)
            .Take(SimilarListingsMax)
            .Select(p => new
            {
                p.ListingTitle,
                p.AccommodationType,
                p.MaxGuests,
                p.Bedrooms,
                p.Bathrooms,
                p.AreaSqm,
                p.Amenities,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(p => new SimilarListingFacts(
                NullIfBlank(p.ListingTitle),
                p.AccommodationType,
                p.MaxGuests,
                p.Bedrooms,
                p.Bathrooms,
                p.AreaSqm,
                p.Amenities))
            .ToList();
    }

    /// <summary>The prompt version, the texts and the bytes of each photo: the same data and photos give the same key.</summary>
    private static string CacheKey(AiRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(ListingTextPrompt.Version));
        hash.AppendData(Encoding.UTF8.GetBytes(request.SystemPrompt ?? string.Empty));
        hash.AppendData(Encoding.UTF8.GetBytes(request.Prompt));
        foreach (var image in request.Images)
            hash.AppendData(SHA256.HashData(image.Data));
        return $"listing-text:{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private static ListingTextSuggestion Fallback(string reason, ListingTextFacts facts) =>
        new(false, reason, null, null, Basis(facts, 0));

    private static ListingTextBasis Basis(ListingTextFacts facts, int photos) =>
        new(photos, facts.Comune, facts.Province, facts.Region, facts.SimilarListings.Count, facts.Amenities.Count);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
