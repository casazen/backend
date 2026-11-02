using Casazen.Core.Pricing;
using Xunit;

namespace Casazen.Tests.Unit.Pricing;

/// <summary>The rules of the nightly price of «Calcola con l'AI» (WIZ-AI-PRICE): figures, rounding, what is believed of the AI.</summary>
public class PriceSuggestionRulesTests
{
    private static readonly PriceSuggestionBasis Basis = new("Como", "CO", "Lombardia", 5, 3, [PriceSuggestionFields.Guests]);

    private static MarketPriceStats Market(bool similarSize = true) =>
        new(Count: 5, Min: 90m, P25: 110m, Median: 130m, P75: 160m, Max: 220m, SimilarSize: similarSize);

    private static RawPriceAnswer Answer(
        decimal? rate = 140m,
        decimal? low = null,
        decimal? high = null,
        decimal? cleaning = null,
        decimal? weekend = null,
        decimal? nights = null,
        string? rationale = null) =>
        new(rate, low, high, cleaning, weekend, nights, rationale);

    // ─── Summarize ───────────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_FewerThanTheMinimum_IsNull()
    {
        Assert.Null(PriceSuggestionRules.Summarize([100m, 120m], similarSize: true));
    }

    [Fact]
    public void Summarize_RatesOfZeroDoNotCount()
    {
        Assert.Null(PriceSuggestionRules.Summarize([100m, 120m, 0m, 0m], similarSize: true));
    }

    [Fact]
    public void Summarize_FiveRates_GivesQuartilesMedianAndEnds()
    {
        var stats = PriceSuggestionRules.Summarize([140m, 100m, 120m, 130m, 110m], similarSize: true);

        Assert.NotNull(stats);
        Assert.Equal(5, stats.Count);
        Assert.Equal(100m, stats.Min);
        Assert.Equal(110m, stats.P25);
        Assert.Equal(120m, stats.Median);
        Assert.Equal(130m, stats.P75);
        Assert.Equal(140m, stats.Max);
        Assert.True(stats.SimilarSize);
    }

    [Fact]
    public void Summarize_ThreeRates_InterpolatesTheQuartiles()
    {
        var stats = PriceSuggestionRules.Summarize([200m, 100m, 120m], similarSize: false);

        Assert.NotNull(stats);
        Assert.Equal(110m, stats.P25);
        Assert.Equal(120m, stats.Median);
        Assert.Equal(160m, stats.P75);
        Assert.False(stats.SimilarSize);
    }

    // ─── RoundToStep ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(137.4, 135)]
    [InlineData(137.5, 140)]
    [InlineData(142, 140)]
    [InlineData(143, 145)]
    [InlineData(2.4, 0)]
    [InlineData(2.5, 5)]
    public void RoundToStep_GivesTheNearestMultipleOfFive(double value, double expected)
    {
        Assert.Equal((decimal)expected, PriceSuggestionRules.RoundToStep((decimal)value));
    }

    // ─── FromMarket ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PricePositioning.Lower, 115, 100, 145)]
    [InlineData(PricePositioning.Market, 130, 110, 160)]
    [InlineData(PricePositioning.Higher, 145, 120, 175)]
    public void FromMarket_MovesTheMedianAndTheQuartilesByThePositioning(PricePositioning positioning, double rate, double low, double high)
    {
        var suggestion = PriceSuggestionRules.FromMarket(Market(), positioning, Basis, PriceSuggestionFallbackReasons.AiNotConfigured);

        Assert.Equal((decimal)rate, suggestion.NightlyRate);
        Assert.Equal((decimal)low, suggestion.RangeMin);
        Assert.Equal((decimal)high, suggestion.RangeMax);
    }

    [Fact]
    public void FromMarket_IsNotAnAiAnswer_AndSuggestsNothingElse()
    {
        var suggestion = PriceSuggestionRules.FromMarket(Market(), PricePositioning.Market, Basis, PriceSuggestionFallbackReasons.AiUnavailable);

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiUnavailable, suggestion.Reason);
        Assert.Null(suggestion.CleaningFee);
        Assert.Null(suggestion.WeekendSurchargePercent);
        Assert.Null(suggestion.MinNights);
        Assert.Null(suggestion.Rationale);
        Assert.Same(Basis, suggestion.BasedOn);
    }

    // ─── Accept: the rate ────────────────────────────────────────────────────────

    [Fact]
    public void Accept_AValidAnswer_IsRoundedAndKeepsItsExtras()
    {
        var accepted = PriceSuggestionRules.Accept(
            Answer(137m, low: 110m, high: 170m, cleaning: 62m, weekend: 14.6m, nights: 2.2m, rationale: "  Zona **centrale**,\n molto richiesta. "),
            Market());

        Assert.NotNull(accepted);
        Assert.Equal(135m, accepted.NightlyRate);
        Assert.Equal(110m, accepted.RangeMin);
        Assert.Equal(170m, accepted.RangeMax);
        Assert.Equal(60m, accepted.CleaningFee);
        Assert.Equal(15m, accepted.WeekendSurchargePercent);
        Assert.Equal(2, accepted.MinNights);
        Assert.Equal("Zona centrale, molto richiesta.", accepted.Rationale);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(4.0)]
    [InlineData(20000.0)]
    public void Accept_ARateThatIsNotAPrice_IsRefused(double? rate)
    {
        Assert.Null(PriceSuggestionRules.Accept(Answer(rate is null ? null : (decimal)rate), market: null));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(40)]
    public void Accept_ARateFarOutsideTheSimilarHomes_IsRefused(double rate)
    {
        // The similar homes ask from 90 to 220: under 54 or over 352 is a misreading, not a price.
        Assert.Null(PriceSuggestionRules.Accept(Answer((decimal)rate), Market(similarSize: true)));
    }

    [Theory]
    [InlineData(55)]
    [InlineData(350)]
    public void Accept_ARateAtTheEdgeOfTheBand_IsKept(double rate)
    {
        Assert.NotNull(PriceSuggestionRules.Accept(Answer((decimal)rate), Market(similarSize: true)));
    }

    [Fact]
    public void Accept_HomesOfAnySize_AreNoBand()
    {
        // The figures of homes of every size say little about this one: a villa may ask far more than the flats around it.
        Assert.NotNull(PriceSuggestionRules.Accept(Answer(400m), Market(similarSize: false)));
    }

    // ─── Accept: the range ───────────────────────────────────────────────────────

    [Fact]
    public void Accept_ARangeThatIsMissing_IsWorkedOutAroundTheRate()
    {
        var accepted = PriceSuggestionRules.Accept(Answer(140m), market: null);

        Assert.NotNull(accepted);
        Assert.Equal(120m, accepted.RangeMin);
        Assert.Equal(160m, accepted.RangeMax);
    }

    [Fact]
    public void Accept_ARangeThatDoesNotContainTheRate_IsPulledToIt()
    {
        var accepted = PriceSuggestionRules.Accept(Answer(140m, low: 150m, high: 130m), market: null);

        Assert.NotNull(accepted);
        Assert.Equal(140m, accepted.RangeMin);
        Assert.Equal(140m, accepted.RangeMax);
    }

    [Fact]
    public void Accept_AnExtremeRange_IsKeptBetweenHalfAndDoubleTheRate()
    {
        var accepted = PriceSuggestionRules.Accept(Answer(100m, low: 1m, high: 1000m), market: null);

        Assert.NotNull(accepted);
        Assert.Equal(50m, accepted.RangeMin);
        Assert.Equal(200m, accepted.RangeMax);
    }

    // ─── Accept: the extras ──────────────────────────────────────────────────────

    [Fact]
    public void Accept_ExtrasAboveTheLimits_AreCapped()
    {
        var accepted = PriceSuggestionRules.Accept(Answer(140m, cleaning: 5000m, weekend: 90m, nights: 30m), market: null);

        Assert.NotNull(accepted);
        Assert.Equal(PriceSuggestionLimits.MaxCleaningFee, accepted.CleaningFee);
        Assert.Equal(PriceSuggestionLimits.MaxWeekendSurchargePercent, accepted.WeekendSurchargePercent);
        Assert.Equal(PriceSuggestionLimits.MaxMinNights, accepted.MinNights);
    }

    [Fact]
    public void Accept_ExtrasThatSayNothing_AreLeftOut()
    {
        var accepted = PriceSuggestionRules.Accept(Answer(140m, cleaning: 1m, weekend: 0.4m, nights: 1m), market: null);

        Assert.NotNull(accepted);
        Assert.Null(accepted.CleaningFee);
        Assert.Null(accepted.WeekendSurchargePercent);
        Assert.Null(accepted.MinNights);
    }

    // ─── CleanRationale ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("****")]
    public void CleanRationale_WithNothingToSay_IsNull(string? text)
    {
        Assert.Null(PriceSuggestionRules.CleanRationale(text));
    }

    [Fact]
    public void CleanRationale_ALongText_IsCutAtTheEndOfASentence()
    {
        var text = string.Concat(Enumerable.Repeat("Una frase che spiega il prezzo della casa. ", 20));

        var cleaned = PriceSuggestionRules.CleanRationale(text);

        Assert.NotNull(cleaned);
        Assert.True(cleaned.Length <= PriceSuggestionLimits.RationaleMaxLength);
        Assert.EndsWith(".", cleaned);
    }

    [Fact]
    public void CleanRationale_ALongTextWithoutSentences_IsCutWithinTheLimit()
    {
        var cleaned = PriceSuggestionRules.CleanRationale(new string('a', 700));

        Assert.NotNull(cleaned);
        Assert.True(cleaned.Length <= PriceSuggestionLimits.RationaleMaxLength);
        Assert.EndsWith("…", cleaned);
    }
}
