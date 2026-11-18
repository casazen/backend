using Microsoft.Extensions.Configuration;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>Monthly Agency Base, LISTINO §5 (annual list prices plus 10% for monthly payment).</summary>
public static class AgencyUsagePricing
{
    public const string Base = "base";
    public const string Version = "agency-monthly-units-v1";
    public const string MetadataKey = "agencyBilling";
    public const int QuoteFrom = 400;
    public const decimal Minimum = 100m;
    public static readonly (int UpTo, decimal UnitPrice)[] Bands = [(49, 11m), (149, 9.90m), (399, 8.80m)];
    public static readonly string[] Included = ["channels", "direct-booking", "compliance", "operations", "fiscal"];
    public static string EventName(string code) => "casazen_agency_units_v1_" + code.Replace('-', '_');
    public static string? PriceId(IConfiguration configuration, string code)
    {
        var price = configuration[$"Billing:AgencyUsage:Prices:{code}"]?.Trim();
        return BillingPrices.IsPriceId(price) ? price : null;
    }

    public static decimal? AddonAmount(IConfiguration configuration, string code) => configuration.GetValue<decimal?>($"Billing:AgencyUsage:Monthly:{code}");
    public static bool MatchesPriceId(IConfiguration configuration, string code, string? id) =>
        BillingPrices.IsPriceId(id) && (id == PriceId(configuration, code) || id == configuration[$"Billing:AgencyUsage:PreviousPrices:{code}"]);

    public static bool Available(IConfiguration configuration, string code) =>
        CommercialPricing.IsEnabled(configuration, code) && (Included.Contains(code) || (PriceId(configuration, code) != null && AddonAmount(configuration, code) > 0));

    // Resolving the configured price on Stripe before Checkout prevents accidentally using a licensed
    // owner/legacy price or collecting a different amount from the estimate.
    public static async Task ValidateAsync(StripeClient client, IConfiguration config, string code, CancellationToken ct)
    {
        var id = PriceId(config, code) ?? throw Unavailable();
        Price price;
        try
        {
            price = await new PriceService(client).GetAsync(id, new PriceGetOptions { Expand = ["tiers"] }, cancellationToken: ct);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw Unavailable();
        }
        var taxBehavior = config.GetValue<bool>("Billing:PricesIncludeVat") ? "inclusive" : "exclusive";
        if (!price.Active || price.Currency != "eur" || price.TaxBehavior != taxBehavior ||
            price.Recurring is not { Interval: "month", IntervalCount: 1, UsageType: "metered", Meter: not null } ||
            !MatchesTariff(price, code, AddonAmount(config, code)))
            throw Unavailable();
        var meter = await new Stripe.Billing.MeterService(client).GetAsync(price.Recurring.Meter, cancellationToken: ct);
        if (meter.Status != "active" || meter.EventName != EventName(code) || meter.DefaultAggregation.Formula != "last" ||
            meter.CustomerMapping.EventPayloadKey != "stripe_customer_id" || meter.ValueSettings.EventPayloadKey != "value")
            throw Unavailable();
    }

    public static bool MatchesTariff(Price price, string code, decimal? addon)
    {
        if (price.BillingScheme != "tiered" || price.TiersMode != "volume" || price.Tiers is null) return false;
        // Stripe bills the flat fee of the first tier even at zero units. The final tier
        // intentionally has no automated charge: 400+ requires a negotiated quote.
        (long? UpTo, decimal Flat, decimal Unit)[] expected = code == Base
            ? [(9, Minimum * 100, 0), .. Bands.Select(b => ((long?)b.UpTo, 0m, b.UnitPrice * 100)), (null, 0, 0)]
            : new (long? UpTo, decimal Flat, decimal Unit)[] { (399, 0, (addon ?? -1m) * 100), (null, 0, 0) };
        return price.Tiers.Count == expected.Length && price.Tiers.Zip(expected).All(pair =>
            pair.First.UpTo == pair.Second.UpTo && (pair.First.FlatAmountDecimal ?? 0) == pair.Second.Flat &&
            (pair.First.UnitAmountDecimal ?? 0) == pair.Second.Unit);
    }

    public static Casazen.Core.Exceptions.DomainRuleException Unavailable() =>
        new(BillingPrices.PlanUnavailableCode, BillingPrices.PlanUnavailableMessageKey);
}
