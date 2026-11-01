using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Seo;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>The summary of the tourist tax a public page shows: only the simple, real case, never a choice among several rates.</summary>
public class SeoTouristTaxFactsTests
{
    private static PublicTouristTaxRateSummaryDto Rate(
        TouristTaxCalculationMethod method = TouristTaxCalculationMethod.PerPersonPerNight,
        decimal perNight = 3.00m,
        decimal? percent = null,
        decimal? cap = null,
        int? maxNights = 4,
        int minimumAge = 14,
        string? category = null,
        string? seasonStart = null,
        int? reducedMaxAge = null,
        decimal? reducedRate = null,
        DateTime? effectiveTo = null,
        string? sourceUrl = "https://comune.example.test/tariffe.pdf") => new(
        "Como", category, seasonStart, null, method, perNight, percent, cap, maxNights, minimumAge, reducedMaxAge, reducedRate,
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), effectiveTo, sourceUrl);

    [Fact]
    public void From_OneFixedRate_GivesTheFigureTheNightsTheExemptionAndTheSource()
    {
        var facts = SeoTouristTaxFacts.From([Rate()]);

        Assert.NotNull(facts);
        Assert.Equal(TouristTaxCalculationMethod.PerPersonPerNight, facts.Method);
        Assert.Equal(3.00m, facts.Rate);
        Assert.Equal(4, facts.MaxNights);
        Assert.Equal(14, facts.ExemptUnderAge);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), facts.EffectiveFrom);
        Assert.Equal("https://comune.example.test/tariffe.pdf", facts.SourceUrl);
    }

    [Fact]
    public void From_PercentageRateWithACap_KeepsThePercentAndTheCap()
    {
        var facts = SeoTouristTaxFacts.From(
        [
            Rate(TouristTaxCalculationMethod.PercentOfNightlyPrice, perNight: 0m, percent: 10.5m, cap: 7.00m, maxNights: null, minimumAge: 0),
        ]);

        Assert.NotNull(facts);
        Assert.Equal(TouristTaxCalculationMethod.PercentOfNightlyPrice, facts.Method);
        Assert.Equal(10.5m, facts.Rate);
        Assert.Equal(7.00m, facts.CapPerPersonPerNight);
    }

    [Fact]
    public void From_NoAgeExemptionNoNightsCap_LeavesThemOut()
    {
        var facts = SeoTouristTaxFacts.From([Rate(maxNights: null, minimumAge: 0)]);

        Assert.NotNull(facts);
        Assert.Null(facts.MaxNights);
        Assert.Equal(0, facts.ExemptUnderAge);
    }

    [Fact]
    public void From_SeveralRates_IsNullBecauseTheOneLineWouldBeAChoice()
    {
        Assert.Null(SeoTouristTaxFacts.From([Rate(category: "Case e appartamenti, categoria 1"), Rate(category: "Case e appartamenti, categoria 2", perNight: 5m)]));
    }

    [Theory]
    [InlineData("category")]
    [InlineData("season")]
    [InlineData("reduced")]
    [InlineData("ended")]
    public void From_RateWithACategoryASeasonAReducedAmountOrAnEnd_IsNull(string variant)
    {
        var rate = variant switch
        {
            "category" => Rate(category: "Hotel 3 stelle"),
            "season" => Rate(seasonStart: "04-01"),
            "reduced" => Rate(reducedMaxAge: 16, reducedRate: 1.7m),
            _ => Rate(effectiveTo: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
        };

        Assert.Null(SeoTouristTaxFacts.From([rate]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void From_NoPositiveAmount_IsNull(int amount)
    {
        Assert.Null(SeoTouristTaxFacts.From([Rate(perNight: amount)]));
    }

    [Fact]
    public void From_NoRates_IsNull()
    {
        Assert.Null(SeoTouristTaxFacts.From([]));
    }

    [Theory]
    [InlineData("http://comune.example.test/tariffe.pdf")]
    [InlineData("/relative/tariffe.pdf")]
    [InlineData(null)]
    public void From_SourceThatIsNotAnHttpsUrl_IsLeftOut(string? source)
    {
        var facts = SeoTouristTaxFacts.From([Rate(sourceUrl: source)]);

        Assert.NotNull(facts);
        Assert.Null(facts.SourceUrl);
    }
}
