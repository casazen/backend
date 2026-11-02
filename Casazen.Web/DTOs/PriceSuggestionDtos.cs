using Casazen.Core.Pricing;

namespace Casazen.Web.DTOs;

/// <summary>
/// The question of «Calcola con l'AI» in the step «Prezzi» of the wizard «Aggiungi immobile» (WIZ-AI-PRICE): where the host wants the
/// price to sit against the comparable homes of the area. Everything else the AI uses is read from the saved property.
/// </summary>
public class PriceSuggestionRequest
{
    /// <summary><c>Lower</c>, <c>Market</c> (the default, also when the body is empty) or <c>Higher</c>.</summary>
    public PricePositioning Positioning { get; set; } = PricePositioning.Market;
}

/// <summary>Whether «Calcola con l'AI» can be offered: an AI provider is in effect (not the stub) and has a key.</summary>
public class PriceSuggestionAvailabilityResponse
{
    public bool Available { get; set; }
}

/// <summary>What a nightly price suggestion was written from, for the line «Basato su: …».</summary>
public class PriceSuggestionBasisResponse
{
    /// <summary>The comune of the property, as the official list names it; <c>null</c> when it has none.</summary>
    public string? Comune { get; set; }

    /// <summary>Its province (plate code).</summary>
    public string? Province { get; set; }

    /// <summary>Its region.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// Homes published on CasaZen in the same comune whose nightly rates were compared (summed up, never listed). 0: none, the AI
    /// used what it knows of the area.
    /// </summary>
    public int SimilarListings { get; set; }

    /// <summary>Amenities of the property the AI was told about.</summary>
    public int Amenities { get; set; }

    /// <summary>
    /// The facts of the property the AI was told about: <c>type</c>, <c>guests</c>, <c>bedrooms</c>, <c>beds</c>, <c>bathrooms</c>,
    /// <c>area</c>, <c>amenities</c>, <c>setting</c>. What the host did not say is not listed.
    /// </summary>
    public IReadOnlyList<string> UsedFields { get; set; } = [];
}

/// <summary>
/// A nightly price for a property, with the other prices that go with it; or why the AI is not behind it. Nothing of it is written
/// to the property: the host reads it and decides.
/// </summary>
public class PriceSuggestionResponse
{
    /// <summary>True when the AI worked the prices out. False: the prices, if any, come from the figures of the similar homes alone.</summary>
    public bool AiUsed { get; set; }

    /// <summary>
    /// Why the AI is not behind the price when <see cref="AiUsed"/> is false: <c>ai_not_configured</c>, <c>ai_budget_exhausted</c>,
    /// <c>ai_unavailable</c> or <c>ai_invalid_answer</c>.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Base price for a night, in euros, a multiple of 5; <c>null</c> when there is nothing to suggest.</summary>
    public decimal? NightlyRate { get; set; }

    /// <summary>Low end of what homes like this ask in the area.</summary>
    public decimal? RangeMin { get; set; }

    /// <summary>High end of what homes like this ask in the area.</summary>
    public decimal? RangeMax { get; set; }

    /// <summary>Once for each stay, in euros; <c>null</c> = not suggested.</summary>
    public decimal? CleaningFee { get; set; }

    /// <summary>On the nights of Friday and Saturday, in percent; <c>null</c> = not suggested.</summary>
    public decimal? WeekendSurchargePercent { get; set; }

    /// <summary>Nights at least (2 to 7); <c>null</c> = no minimum suggested.</summary>
    public int? MinNights { get; set; }

    /// <summary>Why this price, a few words in the language of the request; only when the AI wrote it.</summary>
    public string? Rationale { get; set; }

    /// <summary>What the suggestion was written from.</summary>
    public PriceSuggestionBasisResponse BasedOn { get; set; } = new();

    public static PriceSuggestionResponse From(NightlyPriceSuggestion suggestion) => new()
    {
        AiUsed = suggestion.AiUsed,
        Reason = suggestion.Reason,
        NightlyRate = suggestion.NightlyRate,
        RangeMin = suggestion.RangeMin,
        RangeMax = suggestion.RangeMax,
        CleaningFee = suggestion.CleaningFee,
        WeekendSurchargePercent = suggestion.WeekendSurchargePercent,
        MinNights = suggestion.MinNights,
        Rationale = suggestion.Rationale,
        BasedOn = new PriceSuggestionBasisResponse
        {
            Comune = suggestion.BasedOn.Comune,
            Province = suggestion.BasedOn.Province,
            Region = suggestion.BasedOn.Region,
            SimilarListings = suggestion.BasedOn.SimilarListings,
            Amenities = suggestion.BasedOn.Amenities,
            UsedFields = suggestion.BasedOn.UsedFields,
        },
    };
}
