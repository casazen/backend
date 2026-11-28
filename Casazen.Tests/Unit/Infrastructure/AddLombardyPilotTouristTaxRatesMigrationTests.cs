using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data.Seeds;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class AddLombardyPilotTouristTaxRatesMigrationTests
{
    [Fact]
    public void NewRates_FitTheColumnsAndCiteAnOfficialHttpsSource()
    {
        var rates = TouristTaxLombardyPilotSeed.NewRates();

        Assert.Equal(
            new[] { "Cernobbio", "Gravedona ed Uniti", "Lezzeno", "Maccagno con Pino e Veddasca", "Rho", "San Siro" },
            rates.Select(r => r.City).Distinct().Order().ToArray());
        Assert.Equal(rates.Count, rates.Select(r => r.Id).Distinct().Count());
        Assert.All(rates, rate =>
        {
            Assert.True(rate.Notes!.Length <= 500, $"{rate.City} notes");
            Assert.True(rate.City.Length <= 100);
            Assert.True((rate.AccommodationCategory?.Length ?? 0) <= 100, $"{rate.City} category");
            Assert.True(rate.SourceUrl!.Length <= 500, $"{rate.City} source");
            Assert.True((rate.SourceAuthority?.Length ?? 0) <= 200);
            Assert.StartsWith("https://www1.finanze.gov.it/", rate.SourceUrl);
            Assert.Equal(TouristTaxRateVerification.Official, rate.VerificationLevel);
            Assert.Equal("LOM", rate.RegionCode);
            Assert.True(rate.RatePerPersonPerNight >= 0.01m);
            Assert.Matches(@"^\d{6}$", rate.IstatCode!);
        });
    }

    [Fact]
    public void NewRates_AreForComuniOfTheSeoPilots()
    {
        var pilots = SeoPilotComuni.All.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(TouristTaxLombardyPilotSeed.NewRates(), rate => Assert.Contains(rate.City, pilots));
    }

    [Fact]
    public void Calculate_Gravedona_TwoAdultsThreeNights_IsOneFiftyPerPersonPerNight()
    {
        var quote = TouristTaxCalculator.Calculate(RatesOf("Gravedona ed Uniti"), Stay("2026-10-10", nights: 3, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(9.00m, quote.Amount);
    }

    [Fact]
    public void Calculate_Rho_CapsTheTaxableNightsAtFive()
    {
        var quote = TouristTaxCalculator.Calculate(RatesOf("Rho"), Stay("2026-10-10", nights: 7, adults: 1));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(5, quote.TaxableNights);
        Assert.Equal(10.00m, quote.Amount);
    }

    [Fact]
    public void Calculate_CernobbioWithoutCategory_UsesTheBaseRateNotTheVillaOne()
    {
        var quote = TouristTaxCalculator.Calculate(RatesOf("Cernobbio"), Stay("2026-10-10", nights: 2, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(12.00m, quote.Amount);
    }

    [Fact]
    public void Calculate_LezzenoOutOfSeason_IsNotAvailableNeverZero()
    {
        var quote = TouristTaxCalculator.Calculate(RatesOf("Lezzeno"), Stay("2026-12-10", nights: 2, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, quote.Status);
        Assert.Null(quote.Amount);
    }

    [Fact]
    public void Calculate_MaccagnoBeforeTheTaxExisted_IsNotAvailable()
    {
        var quote = TouristTaxCalculator.Calculate(RatesOf("Maccagno con Pino e Veddasca"), Stay("2026-02-10", nights: 2, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, quote.Status);
    }

    [Fact]
    public void Calculate_SanSiro_ChildUnderFourteen_IsExempt()
    {
        var quote = TouristTaxCalculator.Calculate(
            RatesOf("San Siro"),
            new TouristTaxStay(DateOnly.Parse("2026-10-10"), DateOnly.Parse("2026-10-12"), Adults: 1, Children: 1, ChildrenAges: [10]));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(3.00m, quote.Amount);
    }

    private static IReadOnlyList<Casazen.Core.Entities.TouristTaxRate> RatesOf(string city) =>
        TouristTaxLombardyPilotSeed.NewRates().Where(r => r.City == city).ToList();

    private static TouristTaxStay Stay(string checkIn, int nights, int adults)
    {
        var from = DateOnly.Parse(checkIn);
        return new TouristTaxStay(from, from.AddDays(nights), adults, Children: 0);
    }
}
