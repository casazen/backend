using System.Globalization;
using System.Resources;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-09: the price estimate of a published service (<see cref="SupplierQuoteCalculator"/>), a pure function. The supplements
/// of every unit (<c>flat</c>, <c>bathroom</c>, <c>sqm30</c>, <c>set</c>, <c>hour</c>), their <c>max</c>, the two roundings (whole
/// hours, started 30 m² blocks), integer cents with no other rounding, the answers without a total (on quote, outside the
/// zones, over a limit) that are answers and not errors, and the values that do not fit the service (422 with the fields).
/// </summary>
public class SupplierQuoteCalculatorTests
{
    private const int FromPrice = 4500;

    // ─── The base price ──────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_APricePerJob_IsTheFromPriceOnce_AsAnEstimate()
    {
        var quote = Quote(Service());

        Assert.Equal(SupplierQuoteOutcome.Estimate, quote.Outcome);
        Assert.Null(quote.Reason);
        Assert.True(quote.IsEstimate);
        Assert.False(quote.RequiresQuote);
        Assert.Equal(4500, quote.TotalCents);
        var line = Assert.Single(quote.Lines);
        Assert.Equal(new SupplierQuoteLine(SupplierQuoteLineKind.Base, null, "Pulizia profonda", null, 1, 4500, 4500), line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Validate_APricePerJob_TakesNoQuantityOrOne(int? quantity)
    {
        var choices = SupplierQuoteCalculator.Validate(Service(), Request(quantity: quantity));

        Assert.Null(choices.Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Validate_APricePerJob_RefusesAnyOtherQuantity(int quantity)
    {
        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(Service(), Request(quantity: quantity)));

        Assert.Equal(new[] { "quantity" }, ex.Fields);
    }

    [Fact]
    public void Calculate_APricePerHour_IsThePriceTimesTheHoursSaid()
    {
        var quote = Quote(Service(unit: SupplierServicePriceUnit.PerHour, price: 2500), Request(quantity: 3));

        Assert.Equal(7500, quote.TotalCents);
        Assert.Equal(new SupplierQuoteLine(SupplierQuoteLineKind.Base, null, "Pulizia profonda", null, 3, 2500, 7500), Assert.Single(quote.Lines));
    }

    [Theory]
    [InlineData(1, 1)] // a minute is an hour started
    [InlineData(59, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)] // rounded UP to whole hours
    [InlineData(120, 2)]
    [InlineData(121, 3)]
    [InlineData(150, 3)]
    [InlineData(1440, 24)]
    public void Calculate_APricePerHourWithNoHoursSaid_UsesTheDurationRoundedUpToWholeHours(int durationMinutes, int hours)
    {
        var quote = Quote(Service(unit: SupplierServicePriceUnit.PerHour, price: 2500, durationMinutes: durationMinutes));

        Assert.Equal(hours, quote.Lines[0].Quantity);
        Assert.Equal(hours * 2500, quote.TotalCents);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(120, 2)]
    [InlineData(100_000, 1000)] // never past the most units one estimate takes
    public void DefaultHours_TheTable(int? durationMinutes, int expected)
    {
        Assert.Equal(expected, SupplierQuoteCalculator.DefaultHours(durationMinutes));
    }

    [Fact]
    public void Calculate_APricePerSet_DefaultsToOneSet_AndTakesTheSetsSaid()
    {
        var service = Service(unit: SupplierServicePriceUnit.PerSet, price: 1200);

        Assert.Equal(1200, Quote(service).TotalCents);
        Assert.Equal(4800, Quote(service, Request(quantity: 4)).TotalCents);
    }

    [Fact]
    public void Calculate_APricePerSquareMeter_IsThePriceTimesTheSquareMeters()
    {
        var quote = Quote(Service(unit: SupplierServicePriceUnit.PerSquareMeter, price: 350), Request(quantity: 85));

        Assert.Equal(29_750, quote.TotalCents);
        Assert.Equal(85, quote.Lines[0].Quantity);
    }

    [Fact]
    public void Validate_APricePerSquareMeter_NeedsTheSquareMeters_EvenForAServiceOnQuote()
    {
        var onQuote = Service(unit: SupplierServicePriceUnit.PerSquareMeter, price: null, requiresQuote: true);

        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(onQuote, Request()));

        Assert.Equal(new[] { "quantity" }, ex.Fields);
    }

    [Theory]
    [InlineData(SupplierServicePriceUnit.PerHour)]
    [InlineData(SupplierServicePriceUnit.PerSet)]
    public void Validate_TheQuantity_IsBetween1AndTheMostOneEstimateTakes(SupplierServicePriceUnit unit)
    {
        var service = Service(unit: unit);

        Assert.Equal(1, SupplierQuoteCalculator.Validate(service, Request(quantity: 1)).Quantity);
        Assert.Equal(1000, SupplierQuoteCalculator.Validate(service, Request(quantity: 1000)).Quantity);
        foreach (var bad in new[] { 0, -3, 1001 })
        {
            var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(service, Request(quantity: bad)));
            Assert.Equal(new[] { "quantity" }, ex.Fields);
        }
    }

    [Fact]
    public void Validate_ThePriceUnitPerSquareMeter_TakesAsManySquareMetersAsASurfaceCanHave()
    {
        var service = Service(unit: SupplierServicePriceUnit.PerSquareMeter, price: 350);

        // A 1,200 m2 villa is an estimate (or "on quote"), not a refusal.
        Assert.Equal(1200, SupplierQuoteCalculator.Validate(service, Request(quantity: 1200)).Quantity);
        Assert.Equal(10_000, SupplierQuoteCalculator.Validate(service, Request(quantity: 10_000)).Quantity);
        Assert.Equal(420_000, Quote(service, Request(quantity: 1200)).TotalCents);
        foreach (var bad in new[] { 0, -3, 10_001 })
        {
            var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(service, Request(quantity: bad)));
            Assert.Equal(new[] { "quantity" }, ex.Fields);
        }
    }

    [Fact]
    public void Calculate_TenThousandSquareMetersAtTheHighestPrice_IsOnQuote_AndDoesNotOverflow()
    {
        var quote = Quote(Service(unit: SupplierServicePriceUnit.PerSquareMeter, price: 10_000_000), Request(quantity: 10_000));

        Assert.Equal(SupplierQuoteOutcome.OnQuote, quote.Outcome);
        Assert.Equal(SupplierQuoteReason.AmountOverLimit, quote.Reason);
    }

    // ─── The supplements, one unit at a time ─────────────────────────────────────

    [Fact]
    public void Calculate_AFlatSupplement_IsAddedOnce()
    {
        var service = ServiceWith(Supplement("ferro", "Ferro da stiro", 500, Flat));

        var quote = Quote(service, Request(options: [Pick("ferro")]));

        Assert.Equal(5000, quote.TotalCents);
        Assert.Equal(new SupplierQuoteLine(SupplierQuoteLineKind.Supplement, "ferro", "Ferro da stiro", "flat", 1, 500, 500), quote.Lines[1]);
    }

    [Theory]
    [InlineData(null)] // the maximum of a flat supplement is the one unit it is, whatever the supplier wrote
    [InlineData(5)]
    public void Validate_AFlatSupplement_IsPickedOrNot_NeverTwice(int? max)
    {
        var service = ServiceWith(Supplement("ferro", "Ferro da stiro", 500, Flat, max));

        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(service, Request(options: [Pick("ferro", 2)])));

        Assert.Equal(new[] { "options[0].quantity" }, ex.Fields);
    }

    [Theory]
    [InlineData("bathroom", 2, 1000, 2000)]
    [InlineData("set", 3, 1200, 3600)]
    [InlineData("hour", 2, 2500, 5000)]
    public void Calculate_ACountedSupplement_IsTheAmountTimesTheUnitsPicked(string per, int units, int amount, int line)
    {
        var service = ServiceWith(Supplement("extra", "Extra", amount, per));

        var quote = Quote(service, Request(options: [Pick("extra", units)]));

        Assert.Equal(FromPrice + line, quote.TotalCents);
        Assert.Equal(new SupplierQuoteLine(SupplierQuoteLineKind.Supplement, "extra", "Extra", per, units, amount, line), quote.Lines[1]);
    }

    [Theory]
    [InlineData("bathroom")]
    [InlineData("set")]
    [InlineData("hour")]
    public void Validate_ACountedSupplement_StopsAtItsMax_AndWithoutOneAtTheMostOneEstimateTakes(string per)
    {
        var limited = ServiceWith(Supplement("extra", "Extra", 500, per, max: 3));
        var unlimited = ServiceWith(Supplement("extra", "Extra", 500, per, max: null));

        Assert.Equal(3, Assert.Single(SupplierQuoteCalculator.Validate(limited, Request(options: [Pick("extra", 3)])).Picks).Quantity);
        Assert.Equal(1000, Assert.Single(SupplierQuoteCalculator.Validate(unlimited, Request(options: [Pick("extra", 1000)])).Picks).Quantity);
        foreach (var (service, quantity) in new[] { (limited, 4), (unlimited, 1001), (limited, 0), (limited, -1) })
        {
            var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(service, Request(options: [Pick("extra", quantity)])));
            Assert.Equal(new[] { "options[0].quantity" }, ex.Fields);
        }
    }

    [Fact]
    public void Validate_ASupplementWithNoQuantity_IsOneUnit()
    {
        var service = ServiceWith(Supplement("bagno", "Bagno in più", 1000, Bathroom));

        var pick = Assert.Single(SupplierQuoteCalculator.Validate(service, Request(options: [new SupplierQuoteOption("bagno", null)])).Picks);

        Assert.Equal(1, pick.Quantity);
    }

    // ─── sqm30: it follows the surface ───────────────────────────────────────────

    [Theory]
    [InlineData(1, 0)]
    [InlineData(60, 0)] // the base price covers up to 60 m²
    [InlineData(61, 1)] // a started block of 30 m² counts whole: rounded UP
    [InlineData(90, 1)]
    [InlineData(91, 2)]
    [InlineData(120, 2)]
    [InlineData(121, 3)]
    [InlineData(150, 3)]
    [InlineData(151, 4)]
    [InlineData(10_000, 332)]
    public void SurfaceBlocks_TheTable(int surface, int blocks)
    {
        Assert.Equal(blocks, SupplierQuoteCalculator.SurfaceBlocks(surface));
    }

    [Theory]
    [InlineData(60, 4500, 0)]
    [InlineData(61, 5500, 1)]
    [InlineData(90, 5500, 1)]
    [InlineData(91, 6500, 2)]
    [InlineData(150, 7500, 3)]
    public void Calculate_ASqm30Supplement_CountsTheBlocksAboveSixtySquareMeters(int surface, int total, int blocks)
    {
        var service = ServiceWith(Supplement("mq", "Oltre 60 m², ogni 30 m²", 1000, Sqm30));

        var quote = Quote(service, Request(surface: surface));

        Assert.Equal(total, quote.TotalCents);
        Assert.Equal(blocks == 0 ? 1 : 2, quote.Lines.Count);
        if (blocks > 0)
            Assert.Equal(new SupplierQuoteLine(SupplierQuoteLineKind.Supplement, "mq", "Oltre 60 m², ogni 30 m²", "sqm30", blocks, 1000, blocks * 1000), quote.Lines[1]);
    }

    [Fact]
    public void Calculate_ASqm30Supplement_WithNoSurface_AddsNothing()
    {
        var service = ServiceWith(Supplement("mq", "Oltre 60 m²", 1000, Sqm30));

        Assert.Equal(4500, Quote(service).TotalCents);
    }

    [Theory]
    [InlineData(120, SupplierQuoteOutcome.Estimate)] // two blocks, the most
    [InlineData(121, SupplierQuoteOutcome.OnQuote)] // three: the supplier prices a bigger home
    public void Calculate_ASqm30Supplement_OverItsMax_IsOnQuote_NotAnError(int surface, SupplierQuoteOutcome outcome)
    {
        var service = ServiceWith(Supplement("mq", "Oltre 60 m²", 1000, Sqm30, max: 2));

        var quote = Quote(service, Request(surface: surface));

        Assert.Equal(outcome, quote.Outcome);
        if (outcome == SupplierQuoteOutcome.OnQuote)
        {
            Assert.Equal(SupplierQuoteReason.SurfaceOverLimit, quote.Reason);
            Assert.Null(quote.TotalCents);
            Assert.Empty(quote.Lines);
        }
    }

    [Fact]
    public void Validate_ASqm30Supplement_IsNotPicked_ItFollowsTheSurface()
    {
        var service = ServiceWith(Supplement("mq", "Oltre 60 m²", 1000, Sqm30));

        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(service, Request(options: [Pick("mq", 1)])));

        Assert.Equal(new[] { "options[0].code" }, ex.Fields);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_001)]
    public void Validate_TheSurface_IsBetween1And10000SquareMeters(int surface)
    {
        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(Service(), Request(surface: surface)));

        Assert.Equal(new[] { "surfaceSqm" }, ex.Fields);
    }

    // ─── Several supplements together ────────────────────────────────────────────

    [Fact]
    public void Calculate_EveryUnitTogether_AddsUpLineByLine_InTheOrderTheSupplierWroteThem()
    {
        var service = Service(
            unit: SupplierServicePriceUnit.PerHour,
            price: 2000,
            supplements:
            [
                Supplement("mq", "Oltre 60 m²", 1000, Sqm30),
                Supplement("bagno", "Bagno in più", 800, Bathroom),
                Supplement("ferro", "Ferro", 300, Flat),
                Supplement("lenzuola", "Set lenzuola", 1200, Set),
                Supplement("ora", "Ora in più", 2000, Hour),
            ]);

        // Asked in another order than the supplier's, and one supplement not picked.
        var quote = Quote(service, Request(quantity: 3, surface: 100, options: [Pick("ora", 1), Pick("lenzuola", 2), Pick("bagno", 2), Pick("ferro")]));

        Assert.Equal(
            new[] { "Pulizia profonda", "Oltre 60 m²", "Bagno in più", "Ferro", "Set lenzuola", "Ora in più" },
            quote.Lines.Select(l => l.Label));
        Assert.Equal(new[] { 6000, 2000, 1600, 300, 2400, 2000 }, quote.Lines.Select(l => l.AmountCents));
        Assert.Equal(14_300, quote.TotalCents);
        Assert.Equal(quote.TotalCents, quote.Lines.Sum(l => l.AmountCents));
        Assert.All(quote.Lines, l => Assert.Equal(l.AmountCents, l.Quantity * l.UnitAmountCents));
    }

    [Fact]
    public void Calculate_ASupplementNotPicked_HasNoLine()
    {
        var service = ServiceWith(Supplement("ferro", "Ferro", 300, Flat), Supplement("bagno", "Bagno", 800, Bathroom));

        var quote = Quote(service, Request(options: [Pick("bagno")]));

        Assert.Equal(new[] { "Pulizia profonda", "Bagno" }, quote.Lines.Select(l => l.Label));
    }

    [Fact]
    public void Validate_TheCodeOfASupplement_IsTrimmedAndCaseInsensitive()
    {
        var service = ServiceWith(Supplement("bagno", "Bagno in più", 800, Bathroom));

        var pick = Assert.Single(SupplierQuoteCalculator.Validate(service, Request(options: [Pick("  BAGNO ", 2)])).Picks);

        Assert.Equal("bagno", pick.Supplement.Code);
        Assert.Equal(2, pick.Quantity);
    }

    [Fact]
    public void Validate_UnknownBlankNullAndRepeatedCodes_AreRefusedTogether_WithTheirPositions()
    {
        var service = ServiceWith(Supplement("bagno", "Bagno", 800, Bathroom));

        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(
            service,
            Request(options: [Pick("bagno"), Pick("bagno"), Pick("non-esiste"), Pick("  "), null])));

        Assert.Equal(new[] { "options[1].code", "options[2].code", "options[3].code", "options[4].code" }, ex.Fields);
    }

    [Fact]
    public void Validate_MoreOptionsThanAServiceCanHave_IsOneRefusal()
    {
        var service = ServiceWith(Supplement("bagno", "Bagno", 800, Bathroom));
        var options = Enumerable.Range(0, 11).Select(_ => Pick("bagno")).ToList();

        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(service, Request(options: options)));

        Assert.Equal(new[] { "options" }, ex.Fields);
    }

    [Fact]
    public void Validate_EveryWrongValue_IsCollectedInOneRefusal_AndAWrongRequestIsWrongEvenForAServiceOnQuote()
    {
        var onQuote = Service(unit: SupplierServicePriceUnit.PerHour, price: null, requiresQuote: true, supplements: [Supplement("bagno", "Bagno", 800, Bathroom, max: 2)]);

        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(
            onQuote,
            new SupplierQuoteRequest(0, -1, [Pick("bagno", 9), Pick("nope")], new string('x', 101), "2012")));

        Assert.Equal(SupplierQuoteErrors.Invalid, ex.Code);
        Assert.Equal(new[] { "quantity", "surfaceSqm", "options[0].quantity", "options[1].code", "comune", "postalCode" }, ex.Fields);
    }

    // ─── The place ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("H501", null, "H501")]
    [InlineData("  Roma  ", null, "Roma")]
    [InlineData("058091", "058091", null)] // six digits are an ISTAT code
    public void Validate_TheComune_IsTrimmed_ASixDigitCodeIsAnIstatCode(string comune, string? istat, string? name)
    {
        var place = SupplierQuoteCalculator.Validate(Service(), Request(comune: comune)).Place;

        Assert.NotNull(place);
        Assert.Equal(istat, place.IstatCode);
        Assert.Equal(istat is null ? name : null, place.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NoComune_IsNoPlace(string? comune)
    {
        Assert.Null(SupplierQuoteCalculator.Validate(Service(), Request(comune: comune)).Place);
    }

    [Theory]
    [InlineData("Ro\u0001ma")]
    public void Validate_AComuneWithControlCharacters_IsRefused(string comune)
    {
        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(Service(), Request(comune: comune)));

        Assert.Equal(new[] { "comune" }, ex.Fields);
    }

    [Theory]
    [InlineData("20121", "20121")]
    [InlineData(" 00184 ", "00184")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void Validate_ThePostalCode_IsFiveDigitsOrNothing(string? postalCode, string? expected)
    {
        Assert.Equal(expected, SupplierQuoteCalculator.Validate(Service(), Request(postalCode: postalCode)).PostalCode);
    }

    [Theory]
    [InlineData("2012")]
    [InlineData("201211")]
    [InlineData("2012a")]
    [InlineData("٢٠١٢١")] // Arabic-Indic digits are not the five digits of a CAP
    public void Validate_APostalCodeThatIsNotFiveDigits_IsRefused(string postalCode)
    {
        var ex = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(Service(), Request(postalCode: postalCode)));

        Assert.Equal(new[] { "postalCode" }, ex.Fields);
    }

    // ─── Answers without a total ─────────────────────────────────────────────────

    [Fact]
    public void Calculate_AServiceOnQuote_IsOnQuote_WithNoTotalAndNoLine()
    {
        var quote = Quote(Service(requiresQuote: true, supplements: [Supplement("bagno", "Bagno", 800, Bathroom)]), Request(options: [Pick("bagno")]));

        Assert.Equal(SupplierQuoteOutcome.OnQuote, quote.Outcome);
        Assert.Equal(SupplierQuoteReason.RequiresQuote, quote.Reason);
        Assert.Null(quote.TotalCents);
        Assert.Empty(quote.Lines);
        Assert.False(quote.IsEstimate);
        Assert.True(quote.RequiresQuote);
    }

    [Fact]
    public void Calculate_AServiceOnQuote_WithAFromPrice_StillHasNoTotal()
    {
        var quote = Quote(Service(price: 3000, requiresQuote: true));

        Assert.Equal(SupplierQuoteOutcome.OnQuote, quote.Outcome);
        Assert.Null(quote.TotalCents);
    }

    [Fact]
    public void Calculate_AServiceWithNoPriceAndNotOnQuote_IsOnQuote_NoPrice()
    {
        var quote = Quote(Service(price: null));

        Assert.Equal(SupplierQuoteOutcome.OnQuote, quote.Outcome);
        Assert.Equal(SupplierQuoteReason.NoPrice, quote.Reason);
    }

    [Fact]
    public void Calculate_OutsideTheZones_IsOutsideArea_AndComesBeforeTheOtherAnswers()
    {
        var quote = SupplierQuoteCalculator.Calculate(
            Service(requiresQuote: true),
            SupplierQuoteCalculator.Validate(Service(requiresQuote: true), Request()),
            insideArea: false);

        Assert.Equal(SupplierQuoteOutcome.OutsideArea, quote.Outcome);
        Assert.Equal(SupplierQuoteReason.OutsideArea, quote.Reason);
        Assert.Null(quote.TotalCents);
        Assert.Empty(quote.Lines);
        Assert.True(quote.RequiresQuote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Calculate_InsideTheZonesOrWithNoPlace_PricesNormally(bool? insideArea)
    {
        var service = Service();

        var quote = SupplierQuoteCalculator.Calculate(service, SupplierQuoteCalculator.Validate(service, Request()), insideArea);

        Assert.Equal(SupplierQuoteOutcome.Estimate, quote.Outcome);
        Assert.Equal(4500, quote.TotalCents);
    }

    [Fact]
    public void Calculate_ATotalAtTheHighestPriceTheCatalogAccepts_IsStillAnEstimate_OneCentMoreIsOnQuote()
    {
        var atTheLimit = Quote(Service(unit: SupplierServicePriceUnit.PerSet, price: 5_000_000), Request(quantity: 2));
        var over = Quote(Service(unit: SupplierServicePriceUnit.PerSet, price: 5_000_001), Request(quantity: 2));

        Assert.Equal(SupplierQuoteOutcome.Estimate, atTheLimit.Outcome);
        Assert.Equal(10_000_000, atTheLimit.TotalCents);
        Assert.Equal(SupplierQuoteOutcome.OnQuote, over.Outcome);
        Assert.Equal(SupplierQuoteReason.AmountOverLimit, over.Reason);
    }

    [Fact]
    public void Calculate_TheLargestPriceTimesTheLargestQuantity_DoesNotOverflow()
    {
        var quote = Quote(
            Service(unit: SupplierServicePriceUnit.PerSquareMeter, price: 10_000_000, supplements: [Supplement("extra", "Extra", 10_000_000, Set)]),
            Request(quantity: 1000, options: [Pick("extra", 1000)]));

        Assert.Equal(SupplierQuoteOutcome.OnQuote, quote.Outcome);
        Assert.Equal(SupplierQuoteReason.AmountOverLimit, quote.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Calculate_VatIsOnlyTheSuppliersDeclaration_InEveryKindOfAnswer(bool includesVat)
    {
        var estimate = Quote(Service(includesVat: includesVat));
        var onQuote = Quote(Service(includesVat: includesVat, requiresQuote: true));
        var service = Service(includesVat: includesVat);
        var outside = SupplierQuoteCalculator.Calculate(service, SupplierQuoteCalculator.Validate(service, Request()), insideArea: false);

        Assert.All(new[] { estimate, onQuote, outside }, quote => Assert.Equal(includesVat, quote.PricesIncludeVat));
        // Nothing is added: the total is the sum of the prices as the supplier wrote them.
        Assert.Equal(4500, estimate.TotalCents);
    }

    [Fact]
    public void Calculate_EveryMoneyAmount_IsAWholeNumberOfCents_ThatAddsUp()
    {
        var service = Service(
            unit: SupplierServicePriceUnit.PerHour,
            price: 1999,
            durationMinutes: 95,
            supplements: [Supplement("mq", "mq", 333, Sqm30), Supplement("bagno", "bagno", 777, Bathroom)]);

        var quote = Quote(service, Request(surface: 77, options: [Pick("bagno", 3)]));

        // 95 minutes -> 2 hours; 77 m² -> 1 block.
        Assert.Equal(new[] { 3998, 333, 2331 }, quote.Lines.Select(l => l.AmountCents));
        Assert.Equal(6662, quote.TotalCents);
    }

    // ─── Messages ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheErrorKeys_ExistInItalianAndEnglish_WithDifferentText()
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        foreach (var key in SupplierQuoteErrors.MessageKeys)
        {
            var italian = manager.GetString(key, CultureInfo.InvariantCulture);
            var english = manager.GetString(key, new CultureInfo("en"));
            Assert.False(string.IsNullOrWhiteSpace(italian), key);
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.NotEqual(italian, english);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private const string Flat = SupplierServiceSupplementUnits.Flat;
    private const string Bathroom = SupplierServiceSupplementUnits.Bathroom;
    private const string Sqm30 = SupplierServiceSupplementUnits.Sqm30;
    private const string Set = SupplierServiceSupplementUnits.Set;
    private const string Hour = SupplierServiceSupplementUnits.Hour;

    private static SupplierQuote Quote(SupplierPublicService service, SupplierQuoteRequest? request = null)
    {
        var choices = SupplierQuoteCalculator.Validate(service, request ?? Request());
        return SupplierQuoteCalculator.Calculate(service, choices, insideArea: null);
    }

    private static SupplierQuoteRequest Request(
        int? quantity = null,
        int? surface = null,
        IReadOnlyList<SupplierQuoteOption?>? options = null,
        string? comune = null,
        string? postalCode = null) =>
        new(quantity, surface, options, comune, postalCode);

    private static SupplierQuoteOption Pick(string code, int? quantity = null) => new(code, quantity);

    private static SupplierServiceSupplement Supplement(string code, string label, int amount, string per, int? max = null) =>
        new(code, label, amount, per, max);

    private static SupplierPublicService ServiceWith(params SupplierServiceSupplement[] supplements) =>
        Service(supplements: supplements);

    private static SupplierPublicService Service(
        SupplierServicePriceUnit unit = SupplierServicePriceUnit.PerJob,
        int? price = FromPrice,
        bool requiresQuote = false,
        bool includesVat = false,
        int? durationMinutes = 120,
        IReadOnlyList<SupplierServiceSupplement>? supplements = null) =>
        new(
            "pulizia-profonda",
            "Pulizia profonda",
            ServiceCategories.Cleaning,
            "Pulizia a fondo",
            null,
            price,
            unit,
            includesVat,
            requiresQuote,
            durationMinutes,
            supplements ?? [],
            [],
            [],
            [],
            null,
            SupplierServiceWeekdays.AllMask);
}
