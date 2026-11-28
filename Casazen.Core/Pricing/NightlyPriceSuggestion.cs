namespace Casazen.Core.Pricing;

public enum PricePositioning
{
    Lower = 0,

    Market = 1,

    Higher = 2,
}

public static class PriceSuggestionLimits
{
    public const int MinSimilarListings = 3;

    public const int MaxSampleListings = 500;

    public const int SimilarBedroomsDelta = 1;

    public const int SimilarGuestsDelta = 2;

    public const decimal RoundingStep = 5m;

    public const decimal MinNightlyRate = 10m;

    public const decimal MaxNightlyRate = 10_000m;

    public const decimal MaxCleaningFee = 500m;

    public const decimal MaxWeekendSurchargePercent = 50m;

    public const int MaxMinNights = 7;

    public const int RationaleMaxLength = 400;

    public const decimal PositioningShift = 0.10m;

    public const decimal BandLowFactor = 0.6m;

    public const decimal BandHighFactor = 1.6m;
}

public static class PriceSuggestionFallbackReasons
{
    public const string AiNotConfigured = "ai_not_configured";

    public const string AiBudgetExhausted = "ai_budget_exhausted";

    public const string AiUnavailable = "ai_unavailable";

    public const string AiInvalidAnswer = "ai_invalid_answer";
}

public static class PriceSuggestionFields
{
    public const string Type = "type";
    public const string Guests = "guests";
    public const string Bedrooms = "bedrooms";
    public const string Beds = "beds";
    public const string Bathrooms = "bathrooms";
    public const string Area = "area";
    public const string Amenities = "amenities";
    public const string Setting = "setting";
}

/// <summary>
/// The nightly rates of the other homes of the comune that are published on CasaZen, summarised: never a listing, never an owner,
/// and never fewer than <see cref="PriceSuggestionLimits.MinSimilarListings"/> homes.
/// </summary>
/// <param name="Count">Homes the figures come from.</param>
/// <param name="Min">Cheapest nightly rate.</param>
/// <param name="P25">First quartile.</param>
/// <param name="Median">Median.</param>
/// <param name="P75">Third quartile.</param>
/// <param name="Max">Dearest nightly rate.</param>
/// <param name="SimilarSize">True when they are homes of a size like this one's; false when they are the homes of the comune of any size.</param>
public sealed record MarketPriceStats(int Count, decimal Min, decimal P25, decimal Median, decimal P75, decimal Max, bool SimilarSize);

/// <summary>What a suggestion was written from, for the line the host reads under it («Basato su: Como, 3 ospiti, 5 case simili»).</summary>
/// <param name="Comune">The comune of the property, as the official list names it; <c>null</c> when it has none.</param>
/// <param name="Province">Its province (plate code, <c>CO</c>).</param>
/// <param name="Region">Its region.</param>
/// <param name="SimilarListings">Homes published on CasaZen in the same comune whose prices were compared (0 = none, the AI used what it knows of the area).</param>
/// <param name="Amenities">Amenities of the property the AI was told about.</param>
/// <param name="UsedFields">Codes of <see cref="PriceSuggestionFields"/>: the facts of the property the AI was told about.</param>
public sealed record PriceSuggestionBasis(
    string? Comune,
    string? Province,
    string? Region,
    int SimilarListings,
    int Amenities,
    IReadOnlyList<string> UsedFields);

/// <summary>
/// A nightly rate for a property and the other prices that go with it, or why there is none.
/// </summary>
/// <param name="AiUsed">True when the AI worked the prices out. False: the prices, if any, come from the figures of the similar homes alone.</param>
/// <param name="Reason">One of <see cref="PriceSuggestionFallbackReasons"/> when <paramref name="AiUsed"/> is false.</param>
/// <param name="NightlyRate">The base price for a night, a multiple of 5; <c>null</c> when there is nothing to suggest.</param>
/// <param name="RangeMin">Low end of what homes like this ask in the area.</param>
/// <param name="RangeMax">High end of what homes like this ask in the area.</param>
/// <param name="CleaningFee">Once for each stay; <c>null</c> = not suggested.</param>
/// <param name="WeekendSurchargePercent">On the nights of Friday and Saturday; <c>null</c> = not suggested.</param>
/// <param name="MinNights">Nights at least, 2 to 7; <c>null</c> = no minimum suggested.</param>
/// <param name="Rationale">Why this price, a few words in the language of the host; only when the AI wrote it.</param>
/// <param name="BasedOn">What the suggestion was (or would have been) written from.</param>
public sealed record NightlyPriceSuggestion(
    bool AiUsed,
    string? Reason,
    decimal? NightlyRate,
    decimal? RangeMin,
    decimal? RangeMax,
    decimal? CleaningFee,
    decimal? WeekendSurchargePercent,
    int? MinNights,
    string? Rationale,
    PriceSuggestionBasis BasedOn)
{
    public string Currency { get; init; } = "EUR";
}
