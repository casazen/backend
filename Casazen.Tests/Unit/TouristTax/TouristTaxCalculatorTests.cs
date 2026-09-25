using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data.Seeds;
using Xunit;

namespace Casazen.Tests.Unit.TouristTax;

/// <summary>
/// The only tourist tax engine (BK-03: A3-02, A5-16, A8-23), on the rates seeded from the RS-7 research where they
/// exist (Como, Firenze, Milano, Napoli, Roma, Venezia) and on a Bologna-like percentage rate built here.
/// </summary>
public class TouristTaxCalculatorTests
{
    private static TouristTaxRate Seeded(string city) =>
        TouristTaxRateSeed.BuildRates().Single(r => r.City == city);

    private static IReadOnlyList<TouristTaxRate> Venezia() =>
        TouristTaxRateSeed.BuildCategoryAndSeasonRates().Where(r => r.City == "Venezia").ToList();

    private static IReadOnlyList<TouristTaxRate> Roma() =>
        TouristTaxRateSeed.BuildCategoryAndSeasonRates().Where(r => r.City == "Roma").ToList();

    private const string VeneziaGruppo1 = "Gruppo 1 (categorie catastali A/1, A/8, A/9)";
    private const string VeneziaGruppo3 = "Gruppo 3 (categorie catastali A/4, A/5)";
    private const string RomaCav1 = "Case e appartamenti per vacanze, categoria 1";

    private static TouristTaxStay Stay(
        string checkIn,
        int nights,
        int adults,
        int[]? childrenAges = null,
        string? category = null,
        decimal? nightlyPrice = null,
        int? children = null) =>
        new(
            DateOnly.Parse(checkIn),
            DateOnly.Parse(checkIn).AddDays(nights),
            adults,
            children ?? childrenAges?.Length ?? 0,
            childrenAges,
            category,
            nightlyPrice);

    /// <summary>Bologna in the RS-7 research: 10,5% of the night price per person, max 7,00 €, 5 nights, exempt under 14.</summary>
    private static TouristTaxRate BolognaLikePercentage() => new()
    {
        City = "Bologna",
        IstatCode = "037006",
        CalculationMethod = TouristTaxCalculationMethod.PercentOfNightlyPrice,
        PercentOfNightlyPrice = 10.5m,
        CapPerPersonPerNight = 7.00m,
        MaxNights = 5,
        MinimumAge = 14,
        IsActive = true,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Calculate_FixedRate_TaxesEveryAdultEveryNight()
    {
        // Como: 3,00 € x 2 adults x 3 nights.
        var quote = TouristTaxCalculator.Calculate([Seeded("Como")], Stay("2026-10-10", nights: 3, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(18.00m, quote.Amount);
        Assert.Equal(3, quote.TaxableNights);
        Assert.Equal(18.00m, quote.AmountOrZero);
    }

    [Fact]
    public void Calculate_PercentageBelowCap_RoundsEachPersonNightToTheCent()
    {
        // 95,00 € / 3 guests x 10,5% = 3,325 → 3,33 (half away from zero); 3 x 3,33 x 2 nights = 19,98.
        var quote = TouristTaxCalculator.Calculate(
            [BolognaLikePercentage()],
            Stay("2026-10-10", nights: 2, adults: 3, nightlyPrice: 95m));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(19.98m, quote.Amount);
    }

    [Fact]
    public void Calculate_PercentageAboveCap_ChargesTheCapPerPersonPerNight()
    {
        // 140 € / 2 = 70 € per person, 10,5% = 7,35 → capped at 7,00; 2 x 7,00 x 3 nights = 42,00.
        var quote = TouristTaxCalculator.Calculate(
            [BolognaLikePercentage()],
            Stay("2026-10-10", nights: 3, adults: 2, nightlyPrice: 140m));

        Assert.Equal(42.00m, quote.Amount);
    }

    [Fact]
    public void Calculate_PercentageWithoutNightPrice_AsksForIt()
    {
        var quote = TouristTaxCalculator.Calculate([BolognaLikePercentage()], Stay("2026-10-10", nights: 1, adults: 1));

        Assert.Equal(TouristTaxQuoteStatus.NightlyPriceRequired, quote.Status);
        Assert.Null(quote.Amount);
        Assert.Equal(0m, quote.AmountOrZero);
    }

    [Fact]
    public void Calculate_StayAcrossSeasonChange_TaxesEachNightWithTheRateOfItsSeason()
    {
        // Venezia Gruppo 1: nights 30/01 and 31/01 low season (3,50), 01/02 high season (5,00): 2 x 12,00.
        var quote = TouristTaxCalculator.Calculate(
            Venezia(),
            Stay("2027-01-30", nights: 3, adults: 2, category: VeneziaGruppo1));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(24.00m, quote.Amount);
        Assert.Equal(2, quote.AppliedRates.Count);
    }

    [Fact]
    public void Calculate_SeasonWithoutAnOfficialRate_IsUnavailableNotPartial()
    {
        // Venezia Gruppo 3: the high-season amount is deduced (D) and not seeded: 31/01 has a rate, 01/02 has none.
        var quote = TouristTaxCalculator.Calculate(
            Venezia(),
            Stay("2027-01-31", nights: 2, adults: 1, category: VeneziaGruppo3));

        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, quote.Status);
        Assert.Null(quote.Amount);
    }

    [Fact]
    public void Calculate_StayLongerThanTheCap_TaxesOnlyTheFirstNights()
    {
        // Firenze: max 7 nights. 10 nights x 1 adult x 6,00 = 42,00, not 60,00.
        var quote = TouristTaxCalculator.Calculate([Seeded("Firenze")], Stay("2026-10-01", nights: 10, adults: 1));

        Assert.Equal(42.00m, quote.Amount);
        Assert.Equal(10, quote.Nights);
        Assert.Equal(7, quote.TaxableNights);
    }

    [Theory]
    // Roma: exempt until the 10th birthday. Firenze: until the 12th. 1 night, 1 adult + the child.
    [InlineData("Roma", 9, 6.00)]
    [InlineData("Roma", 10, 12.00)]
    [InlineData("Firenze", 10, 6.00)]
    [InlineData("Firenze", 11, 6.00)]
    [InlineData("Firenze", 12, 12.00)]
    public void Calculate_ChildAtTheExemptionAge_PaysOnlyFromMinimumAge(string city, int age, decimal expected)
    {
        var rates = city == "Roma" ? Roma() : [Seeded("Firenze")];
        var category = city == "Roma" ? RomaCav1 : null;

        var quote = TouristTaxCalculator.Calculate(
            rates,
            Stay("2026-10-10", nights: 1, adults: 1, childrenAges: [age], category: category));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.Equal(expected, quote.Amount);
        Assert.True(quote.AgeRulesApply);
    }

    [Fact]
    public void Calculate_MinorsWithoutAges_WhenTheRateDependsOnAge_AsksForTheAges()
    {
        var quote = TouristTaxCalculator.Calculate(
            [Seeded("Firenze")],
            Stay("2026-10-10", nights: 2, adults: 2, children: 1));

        Assert.Equal(TouristTaxQuoteStatus.ChildAgesRequired, quote.Status);
        Assert.True(quote.AgeRulesApply);
        Assert.Null(quote.Amount);
    }

    [Fact]
    public void Calculate_AgesOutOfRange_AreTreatedAsMissing()
    {
        var quote = TouristTaxCalculator.Calculate(
            [Seeded("Firenze")],
            Stay("2026-10-10", nights: 2, adults: 2, childrenAges: [18]));

        Assert.Equal(TouristTaxQuoteStatus.ChildAgesRequired, quote.Status);
    }

    [Fact]
    public void Calculate_MilanoExemptsEveryMinor_WithoutAskingTheAges()
    {
        // Milano: exempt up to the 18th birthday, so the ages do not matter. 2 adults x 9,50 x 2 nights.
        var quote = TouristTaxCalculator.Calculate(
            [Seeded("Milano")],
            Stay("2026-10-10", nights: 2, adults: 2, children: 2));

        Assert.Equal(TouristTaxQuoteStatus.Calculated, quote.Status);
        Assert.False(quote.AgeRulesApply);
        Assert.Equal(38.00m, quote.Amount);
    }

    [Fact]
    public void Calculate_ReducedBand_UsesTheAmountPublishedByTheComune()
    {
        // Venezia Gruppo 1, January: 3,50 full, 1,70 reduced (published, not 1,75) for ages 10-16; under 10 exempt.
        var quote = TouristTaxCalculator.Calculate(
            Venezia(),
            Stay("2027-01-10", nights: 1, adults: 1, childrenAges: [9, 10, 16, 17], category: VeneziaGruppo1));

        // 3,50 (adult) + 0 (9) + 1,70 (10) + 1,70 (16) + 3,50 (17) = 10,40.
        Assert.Equal(10.40m, quote.Amount);
    }

    [Fact]
    public void Calculate_CategoryRatesWithoutCategory_AsksForTheCategory()
    {
        var quote = TouristTaxCalculator.Calculate(Roma(), Stay("2026-10-10", nights: 2, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.CategoryRequired, quote.Status);
        Assert.Null(quote.Amount);
        Assert.Equal(
            ["Case e appartamenti per vacanze, categoria 1", "Case e appartamenti per vacanze, categoria 2"],
            quote.Categories);
    }

    [Fact]
    public void Calculate_ComuneWithoutRate_IsUnavailableAndAddsNothing()
    {
        var quote = TouristTaxCalculator.Calculate([], Stay("2026-10-10", nights: 3, adults: 2));

        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, quote.Status);
        Assert.Null(quote.Amount);
        Assert.Equal(0m, quote.AmountOrZero);
        Assert.Equal(0, quote.TaxableNights);
    }

    [Fact]
    public void Calculate_StayStartingBeforeTheRate_IsUnavailable()
    {
        // Napoli: 6,00 from 01/05/2026, the rate of January-April is not verified (not seeded).
        var quote = TouristTaxCalculator.Calculate([Seeded("Napoli")], Stay("2026-04-29", nights: 3, adults: 1));

        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, quote.Status);
    }

    [Fact]
    public void Calculate_InactiveOrExpiredRate_IsIgnored()
    {
        var inactive = Seeded("Como");
        inactive.IsActive = false;
        var expired = Seeded("Firenze");
        expired.EffectiveTo = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            TouristTaxQuoteStatus.RateUnavailable,
            TouristTaxCalculator.Calculate([inactive], Stay("2026-10-10", nights: 1, adults: 1)).Status);
        Assert.Equal(
            TouristTaxQuoteStatus.RateUnavailable,
            TouristTaxCalculator.Calculate([expired], Stay("2026-10-10", nights: 1, adults: 1)).Status);
    }

    [Fact]
    public void Calculate_NewerRateOfTheSameComune_WinsFromItsStartDate()
    {
        var old = Seeded("Como");
        var newer = Seeded("Como");
        newer.Id = Guid.NewGuid();
        newer.RatePerPersonPerNight = 4.00m;
        newer.EffectiveFrom = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 31/12/2026 at 3,00, 01/01/2027 at 4,00.
        var quote = TouristTaxCalculator.Calculate([old, newer], Stay("2026-12-31", nights: 2, adults: 1));

        Assert.Equal(7.00m, quote.Amount);
    }

    [Fact]
    public void Calculate_NoNights_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            TouristTaxCalculator.Calculate([Seeded("Como")], Stay("2026-10-10", nights: 0, adults: 1)));
    }

    [Fact]
    public void RateFor_PropertyWithoutCategory_IgnoresCategoryRates()
    {
        var day = new DateOnly(2026, 10, 10);

        Assert.Null(TouristTaxCalculator.RateFor(Roma(), day));
        Assert.Equal(6.00m, TouristTaxCalculator.RateFor(Roma(), day, RomaCav1)!.RatePerPersonPerNight);
    }

    [Theory]
    [InlineData("Roma", " ROMA ")]
    [InlineData("Forlì", "forli")]
    [InlineData("Reggio nell'Emilia", "Reggio nell’Emilia")]
    [InlineData("Sant'Agata de' Goti", "sant agata de goti")]
    public void TouristTaxComune_NameVariants_MatchTheRate(string rateCity, string typedCity)
    {
        var rate = new TouristTaxRate { City = rateCity };

        Assert.True(new TouristTaxComune(null, typedCity).Matches(rate));
    }

    [Fact]
    public void TouristTaxComune_BothWithIstatCode_MatchByCodeOnly()
    {
        var rate = new TouristTaxRate { City = "Samone", IstatCode = "001234" };

        Assert.True(new TouristTaxComune("001234", "Other name").Matches(rate));
        Assert.False(new TouristTaxComune("022165", "Samone").Matches(rate));
        Assert.True(new TouristTaxComune(null, "samone").Matches(rate));
    }

    [Theory]
    [InlineData("02-01", "12-31", "2027-01-31", false)]
    [InlineData("02-01", "12-31", "2027-02-01", true)]
    [InlineData("11-01", "02-28", "2027-01-15", true)]
    [InlineData("11-01", "02-28", "2027-06-15", false)]
    [InlineData(null, null, "2027-06-15", true)]
    [InlineData("13-01", "12-31", "2027-06-15", false)]
    public void TouristTaxSeason_Contains_HandlesWrapAroundAndInvalidBounds(
        string? start, string? end, string date, bool expected)
    {
        Assert.Equal(expected, TouristTaxSeason.Contains(start, end, DateOnly.Parse(date)));
    }

    [Theory]
    // Winter: 23:30 UTC on 31/01 is already 01/02 in Rome, the first day of Venezia's high season.
    [InlineData("2027-01-31T23:30:00Z", "2027-02-01")]
    [InlineData("2027-01-31T00:00:00Z", "2027-01-31")]
    public void DateInRome_InstantOrDateOnlyValue_ReturnsTheRomeCalendarDate(string value, string expected)
    {
        var instant = DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(DateOnly.Parse(expected), RomeCalendar.DateInRome(instant));
    }
}
