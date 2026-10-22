using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-04c, the one rule about money of the urgent requests: the surcharge enters the estimate (the total the booking carries) exactly
/// once, and only for a customer who asked for an urgent slot of a service that offers it, at a supplier that accepts urgent requests.
/// </summary>
public class SupplierQuoteUrgentTests
{
    private const int FromPrice = 4500;

    private static readonly SupplierUrgentSettings SupplierOn = new(Enabled: true, FromMinute: 420, ToMinute: 1320, SurchargeCents: 1500);

    [Fact]
    public void Calculate_TheUrgentSurcharge_IsInTheTotalOnce_AndOnlyForAnUrgentRequestOfAServiceThatOffersIt()
    {
        var offering = Service(urgentEnabled: true).WithUrgent(SupplierOn); // no surcharge of its own: the supplier's 15 euro

        // Not urgent: the estimate it always was, whatever the service offers.
        var normal = Quote(offering, urgent: false);
        Assert.Equal(FromPrice, normal.TotalCents);
        Assert.DoesNotContain(normal.Lines, line => line.Kind == SupplierQuoteLineKind.Urgent);

        // Urgent: one line, the supplier's default, once, inside the total; the base price is untouched and the lines add up to it.
        var urgent = Quote(offering, urgent: true);
        Assert.Equal(FromPrice + 1500, urgent.TotalCents);
        Assert.Equal(
            new SupplierQuoteLine(SupplierQuoteLineKind.Urgent, null, SupplierQuoteLabels.Urgent, null, 1, 1500, 1500),
            Assert.Single(urgent.Lines, line => line.Kind == SupplierQuoteLineKind.Urgent));
        Assert.Equal(urgent.TotalCents, urgent.Lines.Sum(line => line.AmountCents));

        // What the booking keeps on the request is the same once: the snapshot of the choices has the surcharge, one entry.
        var choices = SupplierQuoteCalculator.Validate(offering, Request(urgent: true));
        var kept = Assert.Single(SupplierQuoteCalculator.SnapshotOptions(offering, choices), option => option.Code == ServiceRequestOptionCodes.Urgent);
        Assert.Equal(1500, kept.AmountCents * kept.Quantity);

        // The surcharge of the service wins over the supplier's default, and zero is a surcharge (an urgency for free: no line).
        Assert.Equal(FromPrice + 2000, Quote(Service(urgentEnabled: true, urgentSurchargeCents: 2000).WithUrgent(SupplierOn), urgent: true).TotalCents);
        var free = Quote(Service(urgentEnabled: true, urgentSurchargeCents: 0).WithUrgent(SupplierOn), urgent: true);
        Assert.Equal(FromPrice, free.TotalCents);
        Assert.DoesNotContain(free.Lines, line => line.Kind == SupplierQuoteLineKind.Urgent);

        // A service that does not offer urgent requests, or a supplier that does not accept them, takes no urgent request at all.
        foreach (var refusing in new[]
                 {
                     Service(urgentEnabled: false).WithUrgent(SupplierOn),
                     Service(urgentEnabled: true).WithUrgent(SupplierOn with { Enabled = false }),
                     Service(urgentEnabled: true), // the terms were never worked out: off
                 })
        {
            var refused = Assert.Throws<SupplierQuoteRuleException>(() => SupplierQuoteCalculator.Validate(refusing, Request(urgent: true)));
            Assert.Equal(new[] { SupplierQuoteFields.Urgent }, refused.Fields);
            Assert.Equal(FromPrice, Quote(refusing, urgent: false).TotalCents);
        }
    }

    private static SupplierQuote Quote(SupplierPublicService service, bool urgent)
    {
        var choices = SupplierQuoteCalculator.Validate(service, Request(urgent));
        return SupplierQuoteCalculator.Calculate(service, choices, insideArea: null);
    }

    private static SupplierQuoteRequest Request(bool urgent) => new(null, null, null, null, null, urgent);

    private static SupplierPublicService Service(bool urgentEnabled, int? urgentSurchargeCents = null) =>
        new(
            "pulizia-profonda",
            "Pulizia profonda",
            ServiceCategories.Cleaning,
            "Pulizia a fondo",
            null,
            FromPrice,
            SupplierServicePriceUnit.PerJob,
            false,
            false,
            120,
            [],
            [],
            [],
            [],
            null,
            SupplierServiceWeekdays.AllMask,
            urgentEnabled,
            urgentSurchargeCents);
}
