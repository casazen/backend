using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

public class TouristTaxRateRequest : IValidatableObject
{
    [Required(ErrorMessage = "TaxRateCityRequired")]
    [MaxLength(100, ErrorMessage = "TaxRateCityTooLong")]
    public string City { get; set; } = string.Empty;

    [MaxLength(10, ErrorMessage = "TaxRateRegionCodeTooLong")]
    public string? RegionCode { get; set; }

    /// <summary>ISTAT code of the comune (6 digits), optional: when set, the rate is matched by code (BK-03).</summary>
    [RegularExpression("^[0-9]{6}$", ErrorMessage = "TaxRateIstatCodeInvalid")]
    public string? IstatCode { get; set; }

    [MaxLength(100, ErrorMessage = "TaxRateCategoryTooLong")]
    public string? AccommodationCategory { get; set; }

    public string? SeasonStart { get; set; }

    public string? SeasonEnd { get; set; }

    public TouristTaxCalculationMethod? CalculationMethod { get; set; }

    [TouristTaxRateAmount(ErrorMessage = "TaxRateAmountOutOfRange")]
    public decimal RatePerPersonPerNight { get; set; }

    [Range(
        typeof(decimal),
        "0.01",
        "100",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "TaxRatePercentOutOfRange")]
    public decimal? PercentOfNightlyPrice { get; set; }

    [Range(
        typeof(decimal),
        "0.01",
        "1000",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "TaxRateCapOutOfRange")]
    public decimal? CapPerPersonPerNight { get; set; }

    [Range(1, 365, ErrorMessage = "TaxRateMaxNightsOutOfRange")]
    public int? MaxNights { get; set; }

    [Required(ErrorMessage = "TaxRateMinimumAgeRequired")]
    [Range(0, 18, ErrorMessage = "TaxRateMinimumAgeOutOfRange")]
    public int? MinimumAge { get; set; }

    [Range(0, 17, ErrorMessage = "TaxRateReducedRateInvalid")]
    public int? ReducedRateMaxAge { get; set; }

    [Range(
        typeof(decimal),
        "0.01",
        "1000",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "TaxRateReducedRateInvalid")]
    public decimal? ReducedRatePerPersonPerNight { get; set; }

    public bool? IsActive { get; set; }

    [Required(ErrorMessage = "TaxRateEffectiveFromRequired")]
    public DateTime? EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    [MaxLength(500, ErrorMessage = "TaxRateNotesTooLong")]
    public string? Notes { get; set; }

    [MaxLength(500, ErrorMessage = "TaxRateSourceUrlInvalid")]
    public string? SourceUrl { get; set; }

    public TouristTaxRateVerification? VerificationLevel { get; set; }

    public TouristTaxRateInput ToInput() => new(
        City,
        RegionCode ?? string.Empty,
        RatePerPersonPerNight,
        MaxNights,
        MinimumAge!.Value,
        IsActive ?? true,
        EffectiveFrom!.Value,
        EffectiveTo,
        Notes,
        SourceUrl,
        VerificationLevel)
    {
        IstatCode = IstatCode,
        AccommodationCategory = AccommodationCategory,
        SeasonStart = SeasonStart,
        SeasonEnd = SeasonEnd,
        CalculationMethod = CalculationMethod ?? TouristTaxCalculationMethod.PerPersonPerNight,
        PercentOfNightlyPrice = PercentOfNightlyPrice,
        CapPerPersonPerNight = CapPerPersonPerNight,
        ReducedRateMaxAge = ReducedRateMaxAge,
        ReducedRatePerPersonPerNight = ReducedRatePerPersonPerNight,
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
        string Message(string key) => localizer?[key].Value ?? key;

        if (EffectiveFrom.HasValue && EffectiveTo.HasValue && EffectiveTo.Value < EffectiveFrom.Value)
            yield return new ValidationResult(Message("TaxRateEffectiveToBeforeFrom"), [nameof(EffectiveTo)]);

        if (!string.IsNullOrWhiteSpace(SourceUrl) && !IsHttpUrl(SourceUrl.Trim()))
            yield return new ValidationResult(Message("TaxRateSourceUrlInvalid"), [nameof(SourceUrl)]);

        if (VerificationLevel.HasValue && !Enum.IsDefined(VerificationLevel.Value))
            yield return new ValidationResult(Message("TaxRateVerificationInvalid"), [nameof(VerificationLevel)]);

        var method = CalculationMethod ?? TouristTaxCalculationMethod.PerPersonPerNight;
        if (!Enum.IsDefined(method))
        {
            yield return new ValidationResult(Message("TaxRateCalculationMethodInvalid"), [nameof(CalculationMethod)]);
        }
        else if (method == TouristTaxCalculationMethod.PercentOfNightlyPrice && PercentOfNightlyPrice is null)
        {
            yield return new ValidationResult(Message("TaxRatePercentOutOfRange"), [nameof(PercentOfNightlyPrice)]);
        }

        if (!TouristTaxSeason.IsValidPair(SeasonStart, SeasonEnd))
            yield return new ValidationResult(Message("TaxRateSeasonInvalid"), [nameof(SeasonStart), nameof(SeasonEnd)]);

        if (ReducedRateMaxAge.HasValue != ReducedRatePerPersonPerNight.HasValue
            || (ReducedRateMaxAge.HasValue && MinimumAge.HasValue && ReducedRateMaxAge.Value < MinimumAge.Value)
            || (ReducedRateMaxAge.HasValue && method == TouristTaxCalculationMethod.PercentOfNightlyPrice))
        {
            yield return new ValidationResult(
                Message("TaxRateReducedRateInvalid"),
                [nameof(ReducedRateMaxAge), nameof(ReducedRatePerPersonPerNight)]);
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class TouristTaxRateAmountAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var amount = value as decimal? ?? 0m;
        var percentage = validationContext.ObjectInstance is TouristTaxRateRequest
        {
            CalculationMethod: TouristTaxCalculationMethod.PercentOfNightlyPrice,
        };
        var minimum = percentage ? 0m : 0.01m;

        if (amount >= minimum && amount <= 1000m)
            return ValidationResult.Success;

        var key = ErrorMessageString;
        var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
        return new ValidationResult(localizer?[key].Value ?? key, [validationContext.MemberName ?? string.Empty]);
    }
}
