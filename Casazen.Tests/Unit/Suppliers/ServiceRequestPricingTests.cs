using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-04: the price of a completed request (the agreed price plus the extras), and decision D7 (a final amount far above the quote).</summary>
public class ServiceRequestPricingTests
{
    // ─── Decision D7: more than the tolerance above the quote ───

    [Theory]
    [InlineData(10000, 12000, 20, false)] // exactly +20 %: "oltre il 20 %" is not reached
    [InlineData(10000, 12001, 20, true)] // one cent over
    [InlineData(10000, 10000, 20, false)]
    [InlineData(10000, 9000, 20, false)] // cheaper than the quote
    [InlineData(10000, 15000, 50, false)] // another tolerance
    [InlineData(10000, 15001, 50, true)]
    [InlineData(10000, 10001, 0, true)] // no tolerance at all
    [InlineData(333, 400, 20, true)] // 333 * 1.2 = 399.6
    [InlineData(333, 399, 20, false)]
    public void ExceedsQuote_FinalAgainstReference_IsStrictlyAboveTheTolerance(int reference, int final, int tolerance, bool expected)
    {
        Assert.Equal(expected, ServiceRequestPricing.ExceedsQuote(reference, final, tolerance));
    }

    [Theory]
    [InlineData(null, 5000)] // nobody priced the work: nothing to be above
    [InlineData(0, 5000)]
    [InlineData(5000, null)] // no final amount
    [InlineData(null, null)]
    public void ExceedsQuote_NoReferenceOrNoFinalAmount_NeverNeedsConfirmation(int? reference, int? final)
    {
        Assert.False(ServiceRequestPricing.ExceedsQuote(reference, final, 20));
    }

    [Fact]
    public void ExceedsQuote_VeryLargeAmounts_DoNotOverflow()
    {
        Assert.True(ServiceRequestPricing.ExceedsQuote(ServiceRequestLimits.MaxAmountCents / 2 + 1, ServiceRequestLimits.MaxAmountCents, 20));
        Assert.False(ServiceRequestPricing.ExceedsQuote(ServiceRequestLimits.MaxAmountCents, ServiceRequestLimits.MaxAmountCents, 20));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(6000, null, 6000)]
    [InlineData(null, 5000, 5000)]
    [InlineData(6000, 5000, 6000)] // the quote wins over the estimate
    public void ReferenceAmount_TheQuoteThenTheEstimate(int? quoted, int? estimated, int? expected)
    {
        Assert.Equal(expected, ServiceRequestPricing.ReferenceAmount(quoted, estimated));
    }

    // ─── Composing the final price ───

    [Fact]
    public void ComposeFinalPrice_NothingDeclared_IsTheAgreedPriceAsOneBaseLine()
    {
        var price = ServiceRequestPricing.ComposeFinalPrice(6000, null, null, "Pulizia");

        Assert.Equal(6000, price.FinalAmountCents);
        Assert.Equal(new[] { new ServiceRequestPriceLine("base", "Pulizia", 6000) }, price.Lines);
    }

    [Fact]
    public void ComposeFinalPrice_Extras_AreAddedToTheAgreedPriceInOrder()
    {
        var price = ServiceRequestPricing.ComposeFinalPrice(
            6000,
            null,
            [new ServiceRequestExtra("  Bagno in più ", 1000), new ServiceRequestExtra("Set lenzuola", 500)],
            "Pulizia");

        Assert.Equal(7500, price.FinalAmountCents);
        Assert.Equal(
            new[]
            {
                new ServiceRequestPriceLine("base", "Pulizia", 6000),
                new ServiceRequestPriceLine("extra", "Bagno in più", 1000),
                new ServiceRequestPriceLine("extra", "Set lenzuola", 500),
            },
            price.Lines);
    }

    [Fact]
    public void ComposeFinalPrice_DeclaredTotal_IsTheTotalAndTheBaseIsWhatTheExtrasLeave()
    {
        var price = ServiceRequestPricing.ComposeFinalPrice(
            6000, 9000, [new ServiceRequestExtra("Bagno in più", 1000)], "Pulizia");

        Assert.Equal(9000, price.FinalAmountCents);
        Assert.Equal(
            new[] { new ServiceRequestPriceLine("base", "Pulizia", 8000), new ServiceRequestPriceLine("extra", "Bagno in più", 1000) },
            price.Lines);
    }

    [Fact]
    public void ComposeFinalPrice_DeclaredTotalEqualToTheExtras_HasNoBaseLine()
    {
        var price = ServiceRequestPricing.ComposeFinalPrice(null, 1000, [new ServiceRequestExtra("Solo extra", 1000)], "Pulizia");

        Assert.Equal(1000, price.FinalAmountCents);
        Assert.Equal(new[] { new ServiceRequestPriceLine("extra", "Solo extra", 1000) }, price.Lines);
    }

    [Fact]
    public void ComposeFinalPrice_NeverPricedAndNoExtras_HasNoPriceAtAll()
    {
        var price = ServiceRequestPricing.ComposeFinalPrice(null, null, null, "Pulizia");

        Assert.Null(price.FinalAmountCents);
        Assert.Empty(price.Lines);
    }

    [Fact]
    public void ComposeFinalPrice_NotPricedButWithExtras_IsTheExtras()
    {
        var price = ServiceRequestPricing.ComposeFinalPrice(null, null, [new ServiceRequestExtra("Trasferta", 2500)], "Pulizia");

        Assert.Equal(2500, price.FinalAmountCents);
        Assert.Equal(new[] { new ServiceRequestPriceLine("extra", "Trasferta", 2500) }, price.Lines);
    }

    [Theory]
    [InlineData(null, 6000)]
    [InlineData(6000, null)]
    [InlineData(6000, 9000)]
    [InlineData(null, 2500)]
    public void ComposeFinalPrice_TheLinesAlwaysAddUpToTheTotal(int? reference, int? declared)
    {
        IReadOnlyList<ServiceRequestExtra> extras = declared is null && reference is null ? [] : [new ServiceRequestExtra("Extra", 1500)];

        var price = ServiceRequestPricing.ComposeFinalPrice(reference, declared, extras, "Pulizia");

        Assert.Equal(price.FinalAmountCents ?? 0, price.Lines.Sum(line => line.AmountCents));
    }

    [Fact]
    public void ComposeFinalPrice_DeclaredTotalBelowTheExtras_Is422FinalAmountInvalid()
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            ServiceRequestPricing.ComposeFinalPrice(6000, 900, [new ServiceRequestExtra("Bagno", 1000)], "Pulizia"));

        Assert.Equal(ServiceRequestErrorCodes.FinalAmountInvalid, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.FinalAmountInvalidMessageKey, ex.MessageKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ServiceRequestLimits.MaxAmountCents + 1)]
    public void ComposeFinalPrice_DeclaredTotalOutsideTheLimits_Is422AmountInvalid(int declared)
    {
        var ex = Assert.Throws<DomainRuleException>(() => ServiceRequestPricing.ComposeFinalPrice(6000, declared, null, "Pulizia"));

        Assert.Equal(ServiceRequestErrorCodes.AmountInvalid, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.AmountInvalidMessageKey, ex.MessageKey);
    }

    [Fact]
    public void ComposeFinalPrice_ReferencePlusExtrasAboveTheLimit_Is422AmountInvalid()
    {
        var ex = Assert.Throws<DomainRuleException>(() => ServiceRequestPricing.ComposeFinalPrice(
            ServiceRequestLimits.MaxAmountCents, null, [new ServiceRequestExtra("Extra", 1)], "Pulizia"));

        Assert.Equal(ServiceRequestErrorCodes.AmountInvalid, ex.Code);
    }

    [Theory]
    [InlineData("", 100)]
    [InlineData("   ", 100)]
    [InlineData("Extra", 0)]
    [InlineData("Extra", -5)]
    [InlineData("Extra", ServiceRequestLimits.MaxAmountCents + 1)]
    public void ComposeFinalPrice_AnExtraWithoutLabelOrWithABadAmount_Is422AmountInvalid(string label, int amount)
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            ServiceRequestPricing.ComposeFinalPrice(6000, null, [new ServiceRequestExtra(label, amount)], "Pulizia"));

        Assert.Equal(ServiceRequestErrorCodes.AmountInvalid, ex.Code);
    }

    [Fact]
    public void ComposeFinalPrice_AnExtraLabelOfTheMaximumLength_IsAcceptedAndOneMoreIsNot()
    {
        var ok = ServiceRequestPricing.ComposeFinalPrice(
            null, null, [new ServiceRequestExtra(new string('e', ServiceRequestLimits.ExtraLabelMaxLength), 100)], "x");

        Assert.Equal(100, ok.FinalAmountCents);
        Assert.Throws<DomainRuleException>(() => ServiceRequestPricing.ComposeFinalPrice(
            null, null, [new ServiceRequestExtra(new string('e', ServiceRequestLimits.ExtraLabelMaxLength + 1), 100)], "x"));
    }

    [Fact]
    public void ComposeFinalPrice_MoreExtrasThanTheLimit_Is422AmountInvalid()
    {
        var extras = Enumerable.Range(1, ServiceRequestLimits.MaxExtras + 1).Select(i => new ServiceRequestExtra($"Extra {i}", 100)).ToList();

        var ex = Assert.Throws<DomainRuleException>(() => ServiceRequestPricing.ComposeFinalPrice(null, null, extras, "x"));

        Assert.Equal(ServiceRequestErrorCodes.AmountInvalid, ex.Code);
        Assert.Equal(ServiceRequestLimits.MaxExtras, Assert.IsType<int>(ex.MessageArgs[1]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(ServiceRequestLimits.MaxAmountCents, true)]
    [InlineData(ServiceRequestLimits.MaxAmountCents + 1, false)]
    [InlineData(-100, false)]
    public void IsValidAmount_FromOneCentToTheBoundOfTheCatalog(long cents, bool expected)
    {
        Assert.Equal(expected, ServiceRequestPricing.IsValidAmount(cents));
    }

    // ─── JSON of the columns ───

    [Fact]
    public void ServiceRequestJson_PriceLinesAndPhotos_RoundTripInCamelCase()
    {
        var lines = new[] { new ServiceRequestPriceLine("base", "Pulizia", 6000), new ServiceRequestPriceLine("extra", "Bagno", 1000) };
        var photoId = Guid.NewGuid();
        var uploadedAt = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
        var photos = new[] { new ServiceRequestPhoto(photoId, "service-requests/x/photos/y.jpg", uploadedAt) };

        var linesJson = ServiceRequestJson.Serialize(lines);
        var photosJson = ServiceRequestJson.Serialize(photos);

        Assert.Contains("\"amountCents\":6000", linesJson);
        Assert.Equal(lines, ServiceRequestJson.ReadPriceLines(linesJson));
        Assert.Contains("\"uploadedAt\"", photosJson);
        Assert.Equal(photos, ServiceRequestJson.ReadPhotos(photosJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"base\"}")]
    public void ServiceRequestJson_MissingOrUnreadableValue_IsAnEmptyList(string? json)
    {
        Assert.Empty(ServiceRequestJson.ReadPriceLines(json));
        Assert.Empty(ServiceRequestJson.ReadPhotos(json));
        Assert.Empty(ServiceRequestJson.ReadOptions(json));
    }
}
