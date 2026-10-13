using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of <c>POST /api/tourist-tax-rates</c> (create) and <c>PUT /api/tourist-tax-rates/{id}</c> (update).
/// It has no id on purpose: the server generates it on create and takes it from the route on update (A5-06).
/// Error messages are keys of <c>Resources/SharedResources.resx</c>.
/// </summary>
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

    /// <summary>Accommodation category as named by the comune; empty for every accommodation.</summary>
    [MaxLength(100, ErrorMessage = "TaxRateCategoryTooLong")]
    public string? AccommodationCategory { get; set; }

    /// <summary>First day of the yearly season, <c>MM-dd</c>; with <see cref="SeasonEnd"/>, or both empty (all year).</summary>
    public string? SeasonStart { get; set; }

    /// <summary>Last day of the season (included), <c>MM-dd</c>.</summary>
    public string? SeasonEnd { get; set; }

    /// <summary>Fixed amount (default) or percentage of the night price.</summary>
    public TouristTaxCalculationMethod? CalculationMethod { get; set; }

    /// <summary>
    /// Euro per person per night, at least 0,01 for a fixed rate (ignored for a percentage rate). The upper bound is only
    /// a guard against typos, not a legal limit.
    /// </summary>
    [TouristTaxRateAmount(ErrorMessage = "TaxRateAmountOutOfRange")]
    public decimal RatePerPersonPerNight { get; set; }

    /// <summary>Percentage of the night price per person (percentage rate only), e.g. 10.5.</summary>
    [Range(
        typeof(decimal),
        "0.01",
        "100",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "TaxRatePercentOutOfRange")]
    public decimal? PercentOfNightlyPrice { get; set; }

    /// <summary>Cap per person per night of a percentage rate, optional.</summary>
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

    /// <summary>
    /// Guests younger than this are exempt. Required: there is no default age valid for every comune. At most 18:
    /// adults are never exempt by age.
    /// </summary>
    [Required(ErrorMessage = "TaxRateMinimumAgeRequired")]
    [Range(0, 18, ErrorMessage = "TaxRateMinimumAgeOutOfRange")]
    public int? MinimumAge { get; set; }

    /// <summary>
    /// Last age (included) of the reduced band that starts at <see cref="MinimumAge"/>; with
    /// <see cref="ReducedRatePerPersonPerNight"/>, fixed rates only.
    /// </summary>
    [Range(0, 17, ErrorMessage = "TaxRateReducedRateInvalid")]
    public int? ReducedRateMaxAge { get; set; }

    /// <summary>Amount per person per night of the reduced band, as published by the comune.</summary>
    [Range(
        typeof(decimal),
        "0.01",
        "1000",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "TaxRateReducedRateInvalid")]
    public decimal? ReducedRatePerPersonPerNight { get; set; }

    /// <summary>Active unless explicitly false.</summary>
    public bool? IsActive { get; set; }

    /// <summary>Date-only values (<c>2026-01-01</c>) are stored as UTC midnight by the global date handling (FD-06).</summary>
    [Required(ErrorMessage = "TaxRateEffectiveFromRequired")]
    public DateTime? EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    [MaxLength(500, ErrorMessage = "TaxRateNotesTooLong")]
    public string? Notes { get; set; }

    /// <summary>URL of the page or act of the comune the rate comes from (http or https).</summary>
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

/// <summary>
/// Amount of <see cref="TouristTaxRateRequest.RatePerPersonPerNight"/>: 0,01-1000 for a fixed rate, 0-1000 for a
/// percentage rate (which has no fixed amount). A property-level check, so it is reported together with the other
/// field errors.
/// </summary>
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

        // MVC localizes only the built-in attributes: a custom one translates its key itself (SharedResources).
        var key = ErrorMessageString;
        var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
        return new ValidationResult(localizer?[key].Value ?? key, [validationContext.MemberName ?? string.Empty]);
    }
}
