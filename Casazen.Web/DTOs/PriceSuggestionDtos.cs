using Casazen.Core.Pricing;

namespace Casazen.Web.DTOs;

public class PriceSuggestionRequest
{
    public PricePositioning Positioning { get; set; } = PricePositioning.Market;
}

public class PriceSuggestionAvailabilityResponse
{
    public bool Available { get; set; }
}

public class PriceSuggestionBasisResponse
{
    public string? Comune { get; set; }

    public string? Province { get; set; }

    public string? Region { get; set; }

    public int SimilarListings { get; set; }

    public int Amenities { get; set; }

    public IReadOnlyList<string> UsedFields { get; set; } = [];
}

public class PriceSuggestionResponse
{
    public bool AiUsed { get; set; }

    public string Currency { get; set; } = "EUR";

    public string? Reason { get; set; }

    public decimal? NightlyRate { get; set; }

    public decimal? RangeMin { get; set; }

    public decimal? RangeMax { get; set; }

    public decimal? CleaningFee { get; set; }

    public decimal? WeekendSurchargePercent { get; set; }

    public int? MinNights { get; set; }

    public string? Rationale { get; set; }

    public PriceSuggestionBasisResponse BasedOn { get; set; } = new();

    public static PriceSuggestionResponse From(NightlyPriceSuggestion suggestion) => new()
    {
        AiUsed = suggestion.AiUsed,
        Currency = suggestion.Currency,
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
