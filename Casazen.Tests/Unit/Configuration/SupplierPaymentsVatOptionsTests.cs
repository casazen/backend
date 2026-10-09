using Casazen.Core.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// SP-15b, decision D4 (<b>[CONSULENTE FISCALE]</b>): the VAT rate of CasaZen's commission is configuration only and is <b>empty</b>
/// until the tax consultant decides whether the commission percentage includes VAT or VAT is added to it. Nothing in the code or in
/// the committed <c>appsettings.json</c> carries a rate; when one is configured it is validated (0 to 100, two decimals) and only
/// shown in the monthly commission export.
/// </summary>
public class SupplierPaymentsVatOptionsTests
{
    private static SupplierPaymentsOptions Valid(decimal? vat) => new() { CommissionPercent = 10m, CommissionVatPercent = vat };

    [Fact]
    public void TheVatRate_HasNoDefault_AndTheCommittedConfigurationDoesNotSetOne()
    {
        var options = new SupplierPaymentsOptions { CommissionPercent = 10m };
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        var committed = configuration.GetSection(SupplierPaymentsOptions.SectionName).Get<SupplierPaymentsOptions>()!;

        Assert.Null(options.CommissionVatPercent);
        Assert.Null(committed.CommissionVatPercent);
        Assert.Empty(options.Validate());
        Assert.Empty(committed.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(22)]
    [InlineData(22.5)]
    [InlineData(100)]
    public void TheVatRate_InsideTheRange_IsValid(double percent)
    {
        Assert.Empty(Valid((decimal)percent).Validate());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-22)]
    [InlineData(100.01)]
    [InlineData(2200)]
    [InlineData(22.123)]
    public void TheVatRate_OutsideTheRangeOrWithTooManyDecimals_IsRefusedAtStartup(double percent)
    {
        var options = Valid((decimal)percent);

        var failures = options.Validate();

        var failure = Assert.Single(failures);
        Assert.Contains("SupplierPayments__CommissionVatPercent", failure, StringComparison.Ordinal);
        Assert.False(new SupplierPaymentsOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void ARailwayVariable_SetsTheVatRate_WhenTheConsultantHasDecided()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SupplierPayments:CommissionVatPercent"] = "22" })
            .Build();

        var options = configuration.GetSection(SupplierPaymentsOptions.SectionName).Get<SupplierPaymentsOptions>()!;

        Assert.Equal(22m, options.CommissionVatPercent);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheMaximumVatRate_IsAHundredPercent()
    {
        Assert.Equal(100m, SupplierPaymentsOptions.MaxVatPercent);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
