namespace Casazen.Core.Pricing;

/// <summary>
/// Where the host wants the nightly rate to sit against the comparable homes of the area: the one question of «Calcola con l'AI» in
/// the step «Prezzi» of the wizard «Aggiungi immobile».
/// </summary>
public enum PricePositioning
{
    /// <summary>About 10 % under what comparable homes ask: more bookings, less for each night.</summary>
    Lower = 0,

    /// <summary>In line with comparable homes (the default).</summary>
    Market = 1,

    /// <summary>About 10 % over what comparable homes ask: fewer bookings, more for each night; for a home that stands out.</summary>
    Higher = 2,
}

/// <summary>Limits of the nightly price suggestion (WIZ-AI-PRICE): what is believed, how it is rounded, what is never shown.</summary>
public static class PriceSuggestionLimits
{
    /// <summary>
    /// Published listings of the comune needed before their prices are used: with fewer, the prices of single homes would be
    /// readable in the figures, and a median of two says little. Below it the AI answers from its general knowledge of the area.
    /// </summary>
    public const int MinSimilarListings = 3;

    /// <summary>Listings read to build the figures of a comune (the first ones by id: a comune of that size is not a small market).</summary>
    public const int MaxSampleListings = 500;

    /// <summary>A listing is of a similar size when its bedrooms differ from the home's by no more than this.</summary>
    public const int SimilarBedroomsDelta = 1;

    /// <summary>A listing is of a similar size when its guests differ from the home's by no more than this.</summary>
    public const int SimilarGuestsDelta = 2;

    /// <summary>Euros: every price suggested is a multiple of it («140», never «137,40»).</summary>
    public const decimal RoundingStep = 5m;

    /// <summary>Lowest nightly rate suggested; an answer below it is not believed.</summary>
    public const decimal MinNightlyRate = 10m;

    /// <summary>Highest nightly rate suggested; an answer above it is not believed (the API itself takes up to 100,000).</summary>
    public const decimal MaxNightlyRate = 10_000m;

    /// <summary>Highest cleaning fee suggested.</summary>
    public const decimal MaxCleaningFee = 500m;

    /// <summary>Highest weekend surcharge suggested, in percent.</summary>
    public const decimal MaxWeekendSurchargePercent = 50m;

    /// <summary>Longest minimum stay suggested, in nights.</summary>
    public const int MaxMinNights = 7;

    /// <summary>Characters of the explanation kept.</summary>
    public const int RationaleMaxLength = 400;

    /// <summary>The share the positioning moves the price by: 10 % under, or over, the usual one.</summary>
    public const decimal PositioningShift = 0.10m;

    /// <summary>
    /// With real figures of homes of a similar size, an answer under the cheapest one times this, or over the dearest one times
    /// <see cref="BandHighFactor"/>, is not believed: the AI misread the data, and the figures are used instead.
    /// </summary>
    public const decimal BandLowFactor = 0.6m;

    /// <summary>See <see cref="BandLowFactor"/>.</summary>
    public const decimal BandHighFactor = 1.6m;
}

/// <summary>Why the suggestion is not the AI's: the same codes as the text of the listing.</summary>
public static class PriceSuggestionFallbackReasons
{
    /// <summary>No external AI provider is active (<c>Ai:Provider</c> is the stub, or there is no key).</summary>
    public const string AiNotConfigured = "ai_not_configured";

    /// <summary>The platform's monthly AI budget cannot cover the call.</summary>
    public const string AiBudgetExhausted = "ai_budget_exhausted";

    /// <summary>The provider did not answer (error, timeout).</summary>
    public const string AiUnavailable = "ai_unavailable";

    /// <summary>The provider answered with something that is not a believable price.</summary>
    public const string AiInvalidAnswer = "ai_invalid_answer";
}

/// <summary>
/// Codes of what a suggestion was written from (<see cref="PriceSuggestionBasis.UsedFields"/>): the client puts them in words
/// («Basato su: 3 ospiti, 75 m², 8 dotazioni»). Only what the property really has is listed.
/// </summary>
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
    PriceSuggestionBasis BasedOn);
