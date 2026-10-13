using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.OfficialData;

/// <summary>JSON object the AI extractor must return. Validated before any <c>TouristTaxRates</c> write.</summary>
public sealed class ComuneOfficialExtractDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("actId")]
    public string ActId { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("mefPublishedOn")]
    public string? MefPublishedOn { get; set; }

    [JsonPropertyName("validFrom")]
    public string? ValidFrom { get; set; }

    [JsonPropertyName("rates")]
    public List<ComuneOfficialExtractRateDto> Rates { get; set; } = [];

    [JsonPropertyName("remittance")]
    public ComuneOfficialExtractRemittanceDto? Remittance { get; set; }

    [JsonPropertyName("legalValue")]
    public bool LegalValue { get; set; }

    [JsonPropertyName("disclaimer")]
    public string? Disclaimer { get; set; }
}

public sealed class ComuneOfficialExtractRateDto
{
    [JsonPropertyName("accommodationCategory")]
    public string? AccommodationCategory { get; set; }

    [JsonPropertyName("seasonStart")]
    public string? SeasonStart { get; set; }

    [JsonPropertyName("seasonEnd")]
    public string? SeasonEnd { get; set; }

    [JsonPropertyName("calculationMethod")]
    public string? CalculationMethod { get; set; }

    [JsonPropertyName("ratePerPersonPerNight")]
    public decimal? RatePerPersonPerNight { get; set; }

    [JsonPropertyName("percentOfNightlyPrice")]
    public decimal? PercentOfNightlyPrice { get; set; }

    [JsonPropertyName("capPerPersonPerNight")]
    public decimal? CapPerPersonPerNight { get; set; }

    [JsonPropertyName("maxNights")]
    public int? MaxNights { get; set; }

    [JsonPropertyName("minimumAge")]
    public int? MinimumAge { get; set; }

    [JsonPropertyName("reducedRateMaxAge")]
    public int? ReducedRateMaxAge { get; set; }

    [JsonPropertyName("reducedRatePerPersonPerNight")]
    public decimal? ReducedRatePerPersonPerNight { get; set; }
}

public sealed class ComuneOfficialExtractRemittanceDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("dueDayOfMonth")]
    public int? DueDayOfMonth { get; set; }

    [JsonPropertyName("frequency")]
    public string? Frequency { get; set; }
}

public static class ComuneOfficialExtractStatus
{
    public const string Extracted = "extracted";
    public const string Unreadable = "unreadable";
}

public static class ComuneOfficialCalculationMethods
{
    public const string PerPersonPerNight = nameof(TouristTaxCalculationMethod.PerPersonPerNight);
    public const string PercentOfNightlyPrice = nameof(TouristTaxCalculationMethod.PercentOfNightlyPrice);
}
