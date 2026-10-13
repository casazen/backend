using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

/// <summary>
/// Admin tourist tax rate body (A5-06): validation keys and the decimal range, which must not depend on the request
/// culture (Italian uses a decimal comma: parsing "0.01" with it made every create/update a 500).
/// </summary>
public class TouristTaxRateRequestTests
{
    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void Validate_ValidRequestInAnyCulture_HasNoErrors(string culture)
    {
        var errors = WithCulture(culture, () => Validate(ValidRequest()));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ZeroRateInItalianCulture_ReturnsAmountKey()
    {
        var request = ValidRequest();
        request.RatePerPersonPerNight = 0m;

        var errors = WithCulture("it-IT", () => Validate(request));

        Assert.Contains(errors, e => e.ErrorMessage == "TaxRateAmountOutOfRange");
    }

    [Fact]
    public void Validate_EndBeforeStartAndNonHttpSource_ReturnsBothKeys()
    {
        var request = ValidRequest();
        request.EffectiveTo = request.EffectiveFrom!.Value.AddDays(-1);
        request.SourceUrl = "ftp://comune.example.it/delibera.pdf";

        var errors = Validate(request);

        Assert.Contains(errors, e => e.ErrorMessage == "TaxRateEffectiveToBeforeFrom" && e.MemberNames.Contains("EffectiveTo"));
        Assert.Contains(errors, e => e.ErrorMessage == "TaxRateSourceUrlInvalid" && e.MemberNames.Contains("SourceUrl"));
    }

    [Fact]
    public void ToInput_OmittedActiveAndRegion_DefaultsToActiveAndEmptyRegion()
    {
        var request = ValidRequest();
        request.IsActive = null;
        request.RegionCode = null;

        var input = request.ToInput();

        Assert.True(input.IsActive);
        Assert.Equal(string.Empty, input.RegionCode);
        Assert.Equal(TouristTaxRateVerification.Official, input.VerificationLevel);
    }

    private static TouristTaxRateRequest ValidRequest() => new()
    {
        City = "Como",
        RegionCode = "LOM",
        RatePerPersonPerNight = 2.50m,
        MaxNights = 4,
        MinimumAge = 14,
        EffectiveFrom = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        SourceUrl = "https://www.comune.como.it/imposta-di-soggiorno",
        VerificationLevel = TouristTaxRateVerification.Official,
    };

    private static List<ValidationResult> Validate(TouristTaxRateRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static T WithCulture<T>(string culture, Func<T> action)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }
}
