using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
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

    /// <summary>Euro per person per night. The upper bound is only a guard against typos, not a legal limit.</summary>
    [Range(
        typeof(decimal),
        "0.01",
        "1000",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "TaxRateAmountOutOfRange")]
    public decimal RatePerPersonPerNight { get; set; }

    [Range(1, 365, ErrorMessage = "TaxRateMaxNightsOutOfRange")]
    public int? MaxNights { get; set; }

    /// <summary>Guests younger than this are exempt. Required: there is no default age valid for every comune.</summary>
    [Required(ErrorMessage = "TaxRateMinimumAgeRequired")]
    [Range(0, 120, ErrorMessage = "TaxRateMinimumAgeOutOfRange")]
    public int? MinimumAge { get; set; }

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
        VerificationLevel);

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
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
