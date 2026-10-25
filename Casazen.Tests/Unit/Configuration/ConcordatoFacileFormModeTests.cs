using Casazen.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// CF-SIM, the proof of the rule that matters: the simulator of ConcordatoFacile stands in for a service that production must always
/// reach, so <c>ConcordatoFacile:FormMode=Simulated</c> stops the startup when the environment is Production, and is accepted in the test
/// environment (<c>Staging</c> on Railway) and in development. The real mode, the default, is accepted everywhere.
/// </summary>
public class ConcordatoFacileFormModeTests
{
    private static ConcordatoFacileOptions Configured(ConcordatoFacileFormMode mode, string? baseUrl = "https://casazen-test-app.example") => new()
    {
        FormMode = mode,
        StripePriceId = "price_123",
        BiennialPriceIds = ["price_abc"],
        LinkSecret = "test_link_secret_0123456789abcdef0123456789abcdef",
        BaseUrl = baseUrl,
    };

    private static ValidateOptionsResult Validate(string environmentName, ConcordatoFacileOptions options) =>
        new ConcordatoFacileOptionsValidator(Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName)).Validate(null, options);

    [Fact]
    public void Validate_SimulatedInProduction_StopsTheStartup()
    {
        var result = Validate("Production", Configured(ConcordatoFacileFormMode.Simulated));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("FormMode=Simulated", StringComparison.Ordinal)
            && failure.Contains("Production", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_SimulatedInATestEnvironment_Passes(string environmentName)
    {
        Assert.True(Validate(environmentName, Configured(ConcordatoFacileFormMode.Simulated)).Succeeded);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_RealMode_PassesAnywhere(string environmentName)
    {
        Assert.True(Validate(environmentName, Configured(ConcordatoFacileFormMode.Real)).Succeeded);
    }

    [Fact]
    public void Validate_FormUrlInProduction_StopsTheStartup()
    {
        var options = Configured(ConcordatoFacileFormMode.Real);
        options.FormUrl = "https://test.example/";

        var result = Validate("Production", options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("FormUrl", StringComparison.Ordinal)
            && failure.Contains("Production", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void Validate_FormUrlInATestEnvironment_Passes(string environmentName)
    {
        var options = Configured(ConcordatoFacileFormMode.Real);
        options.FormUrl = "https://test.example/";

        Assert.True(Validate(environmentName, options).Succeeded);
    }

    [Theory]
    [InlineData("http://test.example/")]
    [InlineData("test.example")]
    [InlineData("https://test.example/?a=b")]
    [InlineData("https://test.example/#top")]
    public void Validate_FormUrlNotAnAbsoluteHttpsAddressWithoutQuery_StopsTheStartup(string formUrl)
    {
        var options = Configured(ConcordatoFacileFormMode.Real);
        options.FormUrl = formUrl;

        var result = Validate("Staging", options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("FormUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SimulatedWithoutTheAddressOfTheWebApp_StopsTheStartup()
    {
        var result = Validate("Staging", Configured(ConcordatoFacileFormMode.Simulated, baseUrl: null));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("BaseUrl", StringComparison.Ordinal));
    }
}
