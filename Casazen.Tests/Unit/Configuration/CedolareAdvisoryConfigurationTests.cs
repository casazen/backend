using Casazen.Core.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// LT-08: the advisory parameters committed in appsettings.json are the values verified by RS-5 in fiscale.md (L5-L12,
/// C11-C12), each group with its source, and a missing or impossible value stops the startup.
/// </summary>
public class CedolareAdvisoryConfigurationTests
{
    [Fact]
    public void Appsettings_AdvisoryParameters_AreTheVerifiedValuesWithTheirSources()
    {
        var options = FromAppsettings();

        Assert.True(new CedolareAdvisoryOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(0.21m, options.Cedolare.StandardRate);
        Assert.Equal(0.10m, options.Cedolare.ConcordatoAtaRate);
        Assert.Equal(0.02m, options.Registro.Rate);
        Assert.Equal(67.00m, options.Registro.FirstYearMinimumEur);
        Assert.Equal(0.70m, options.Registro.ConcordatoAtaBaseShare);
        Assert.Equal(16.00m, options.Bollo.EurPerUnit);
        Assert.Equal(4, options.Bollo.PagesPerUnit);
        Assert.Equal(100, options.Bollo.LinesPerUnit);
        Assert.Equal(2026, options.Irpef.TaxYear);
        Assert.Equal(0.05m, options.Irpef.RentFlatReduction);
        Assert.Equal(200_000m, options.Irpef.ComputationIncomeLimitEur);
        Assert.Collection(
            options.Irpef.Brackets,
            b => Assert.Equal((28_000m, 0.23m), (b.UpToEur!.Value, b.Rate)),
            b => Assert.Equal((50_000m, 0.33m), (b.UpToEur!.Value, b.Rate)),
            b => Assert.Equal((null, 0.43m), (b.UpToEur, b.Rate)));
        Assert.Contains("agenziaentrate.gov.it", options.Cedolare.Source, StringComparison.Ordinal);
        Assert.Contains("agenziaentrate.gov.it", options.Registro.Source, StringComparison.Ordinal);
        Assert.Contains("agenziaentrate.gov.it", options.Bollo.Source, StringComparison.Ordinal);
        Assert.Contains("L. 199/2025", options.Irpef.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithoutAnyConfiguration_FailsNamingEveryRequiredParameter()
    {
        var result = new CedolareAdvisoryOptionsValidator().Validate(null, new CedolareAdvisoryOptions());

        Assert.True(result.Failed);
        foreach (var key in new[]
                 {
                     "CedolareAdvisory__Cedolare__StandardRate", "CedolareAdvisory__Cedolare__ConcordatoAtaRate",
                     "CedolareAdvisory__Registro__Rate", "CedolareAdvisory__Registro__FirstYearMinimumEur",
                     "CedolareAdvisory__Registro__ConcordatoAtaBaseShare", "CedolareAdvisory__Bollo__EurPerUnit",
                     "CedolareAdvisory__Bollo__PagesPerUnit", "CedolareAdvisory__Bollo__LinesPerUnit",
                     "CedolareAdvisory__Registro__Source",
                 })
        {
            Assert.Contains(result.Failures!, f => f.StartsWith(key + " ", StringComparison.Ordinal));
        }

        // Without brackets the IRPEF comparison is simply not computed: not a startup failure.
        Assert.DoesNotContain(result.Failures!, f => f.Contains("Irpef", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReducedRateAboveStandardRate_Fails()
    {
        var options = FromAppsettings();
        options.Cedolare.ConcordatoAtaRate = 0.26m;

        var result = new CedolareAdvisoryOptionsValidator().Validate(null, options);

        Assert.Contains(result.Failures!, f => f.Contains("ConcordatoAtaRate is higher", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_IrpefBracketsNotAscendingOrLastOneClosed_Fails()
    {
        var options = FromAppsettings();
        options.Irpef.Brackets =
        [
            new IrpefBracketOptions { UpToEur = 50_000m, Rate = 0.23m },
            new IrpefBracketOptions { UpToEur = 28_000m, Rate = 0.33m },
            new IrpefBracketOptions { UpToEur = 90_000m, Rate = 0.43m },
        ];

        var failures = new CedolareAdvisoryOptionsValidator().Validate(null, options).Failures!;

        Assert.Contains(failures, f => f.StartsWith("CedolareAdvisory__Irpef__Brackets__1__UpToEur", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("CedolareAdvisory__Irpef__Brackets__2__UpToEur must be empty", StringComparison.Ordinal));
    }

    /// <summary>The <c>CedolareAdvisory</c> section of the committed appsettings.json.</summary>
    internal static CedolareAdvisoryOptions FromAppsettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();
        var options = new CedolareAdvisoryOptions();
        configuration.GetSection(CedolareAdvisoryOptions.SectionName).Bind(options);
        return options;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
