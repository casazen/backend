using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class ServiceRequestQuoteRulesTests
{
    [Fact]
    public void Normalize_TheTotalIsTheSumOfTheLines_AndTheLabelsAreTrimmed()
    {
        var (lines, total) = ServiceRequestQuoteRules.Normalize(
            [new ServiceRequestQuoteLine("  Pulizia profonda ", 20000), new ServiceRequestQuoteLine("Materiali", 7550)]);

        Assert.Equal(27550, total);
        Assert.Equal(["Pulizia profonda", "Materiali"], lines.Select(line => line.Label));
        Assert.Equal([20000, 7550], lines.Select(line => line.AmountCents));
    }

    [Fact]
    public void Normalize_TheLimits_AreTheOnesOfARequest()
    {
        var full = Enumerable.Range(1, ServiceRequestLimits.MaxQuoteLines).Select(i => new ServiceRequestQuoteLine($"Voce {i}", 100)).ToList();
        var tooMany = full.Append(new ServiceRequestQuoteLine("Una in più", 100)).ToList();
        var longestLabel = new string('x', ServiceRequestLimits.QuoteLineLabelMaxLength);

        Assert.Equal(2000, ServiceRequestQuoteRules.Normalize(full).TotalCents);
        Assert.Equal(100, ServiceRequestQuoteRules.Normalize([new ServiceRequestQuoteLine(longestLabel, 100)]).TotalCents);
        Assert.Equal(
            ServiceRequestLimits.MaxAmountCents,
            ServiceRequestQuoteRules.Normalize([new ServiceRequestQuoteLine("Tutto", ServiceRequestLimits.MaxAmountCents)]).TotalCents);

        foreach (var invalid in new List<IReadOnlyList<ServiceRequestQuoteLine>?>
                 {
                     null,
                     Array.Empty<ServiceRequestQuoteLine>(),
                     tooMany,
                     new[] { new ServiceRequestQuoteLine("   ", 100) },
                     new[] { new ServiceRequestQuoteLine(longestLabel + "x", 100) },
                     new[] { new ServiceRequestQuoteLine("Voce", 0) },
                     new[] { new ServiceRequestQuoteLine("Voce", -100) },
                     new[] { new ServiceRequestQuoteLine("Voce", ServiceRequestLimits.MaxAmountCents + 1) },

                     new[] { new ServiceRequestQuoteLine("Una", ServiceRequestLimits.MaxAmountCents), new ServiceRequestQuoteLine("Due", 1) },
                 })
        {
            var ex = Assert.Throws<DomainRuleException>(() => ServiceRequestQuoteRules.Normalize(invalid));
            Assert.Equal(ServiceRequestErrorCodes.QuoteInvalid, ex.Code);
        }
    }

    [Fact]
    public void IsSameVersion_IsTheInstantTheSupplierSentItToTheMillisecond()
    {
        var sentAt = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc).AddTicks(1230);

        Assert.True(ServiceRequestQuoteRules.IsSameVersion(sentAt, sentAt));
        Assert.True(ServiceRequestQuoteRules.IsSameVersion(sentAt, new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc)));
        Assert.False(ServiceRequestQuoteRules.IsSameVersion(sentAt, sentAt.AddMilliseconds(1)));
        Assert.False(ServiceRequestQuoteRules.IsSameVersion(null, sentAt));
    }

    [Fact]
    public void ComposeFinalPrice_TheLinesOfTheAcceptedQuote_StayWhileTheyAreThePriceOfTheWork()
    {
        IReadOnlyList<ServiceRequestPriceLine> agreed =
        [
            new(ServiceRequestPriceLineKinds.Quote, "Pulizia profonda", 20000),
            new(ServiceRequestPriceLineKinds.Quote, "Materiali", 10000),
        ];

        var plain = ServiceRequestPricing.ComposeFinalPrice(30000, null, null, "Servizio", agreed);
        Assert.Equal(30000, plain.FinalAmountCents);
        Assert.Equal(agreed, plain.Lines);

        var withExtra = ServiceRequestPricing.ComposeFinalPrice(30000, null, [new ServiceRequestExtra("Finestre", 2000)], "Servizio", agreed);
        Assert.Equal(32000, withExtra.FinalAmountCents);
        Assert.Equal(
            [ServiceRequestPriceLineKinds.Quote, ServiceRequestPriceLineKinds.Quote, ServiceRequestPriceLineKinds.Extra],
            withExtra.Lines.Select(line => line.Kind));

        var declared = ServiceRequestPricing.ComposeFinalPrice(30000, 35000, null, "Servizio", agreed);
        Assert.Equal(35000, declared.FinalAmountCents);
        var line = Assert.Single(declared.Lines);
        Assert.Equal((ServiceRequestPriceLineKinds.Base, 35000), (line.Kind, line.AmountCents));
    }
}
