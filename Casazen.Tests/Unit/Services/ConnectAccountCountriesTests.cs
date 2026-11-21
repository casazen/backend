using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Issue #1001: the country of an org's Stripe Express account. Stripe fixes it at creation and the hosted onboarding asks for the
/// data of that country, so a Swiss org (the pilot's Quokka360) must not get the platform's Italian default.
/// </summary>
public class ConnectAccountCountriesTests
{
    [Theory]
    // The confirmed fiscal profile of the org decides first.
    [InlineData("IT", "CH", null, "CH")]
    [InlineData("IT", "CH", "IT", "CH")]
    [InlineData("IT", "IT", "CH", "IT")]
    // Then its billing country.
    [InlineData("IT", null, "CH", "CH")]
    [InlineData("IT", "", "CH", "CH")]
    [InlineData("IT", "  ", "ch", "CH")]
    // A country the product does not support is not a choice of the org: the next source decides.
    [InlineData("IT", "DE", "CH", "CH")]
    [InlineData("CH", null, "DE", "CH")]
    [InlineData("IT", "FR", "DE", "IT")]
    // Nothing known about the org: the platform default (Stripe__ConnectDefaultCountry), IT when it is not set.
    [InlineData("IT", null, null, "IT")]
    [InlineData("CH", null, null, "CH")]
    [InlineData(" ch ", null, null, "CH")]
    [InlineData("de", null, null, "DE")]
    [InlineData(null, null, null, "IT")]
    [InlineData("", "", "", "IT")]
    // Spaces and case never matter.
    [InlineData("IT", " ch ", null, "CH")]
    public void Resolve_FiscalProfileThenBillingCountryThenPlatformDefault(
        string? platformDefault,
        string? fiscalProfileCountry,
        string? billingCountry,
        string expected)
    {
        Assert.Equal(expected, ConnectAccountCountries.Resolve(platformDefault, fiscalProfileCountry, billingCountry));
    }

    [Theory]
    [InlineData("CH", "CH", true)]
    [InlineData("ch", " CH ", true)]
    [InlineData("IT", "CH", false)]
    [InlineData(null, "CH", false)]
    [InlineData("CH", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void SameCountry_IgnoresCaseAndSpaces_AndNeverMatchesAnUnknownCountry(string? first, string? second, bool expected)
    {
        Assert.Equal(expected, ConnectAccountCountries.SameCountry(first, second));
    }

    [Fact]
    public void OrgCountries_AreTheTwoOfTheProduct()
    {
        // The Italian and Swiss fiscal profiles (EUR and CHF stays). Adding one is a product decision, with its Stripe test data.
        Assert.Equal(new[] { "IT", "CH" }, ConnectAccountCountries.OrgCountries);
    }
}
