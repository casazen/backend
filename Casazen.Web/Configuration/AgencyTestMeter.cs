using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Stripe;
using Stripe.Billing;

namespace Casazen.Web.Configuration;

/// <summary>Explicit operator command for the CasaZen test catalog. No web host or database is started.</summary>
internal static class AgencyTestMeter
{
    internal static async Task EnsureAsync(string module)
    {
        if (module != AgencyUsagePricing.Base && !CommercialAddon.All.Any(addon => addon.Code == module && !AgencyUsagePricing.Included.Contains(module)))
            throw new ArgumentException("Unknown Agency usage module.", nameof(module));

        var eventName = AgencyUsagePricing.EventName(module);
        var key = Environment.GetEnvironmentVariable("Stripe__SecretKey");
        if (key is null || !(key.StartsWith("sk_test_", StringComparison.Ordinal) || key.StartsWith("rk_test_", StringComparison.Ordinal)))
            throw new InvalidOperationException("A sandbox Stripe key is required.");

        var client = new StripeClient(key);
        var account = await new AccountService(client).GetSelfAsync();
        if (account.Id != "acct_1TH7HQRPwlDgw3NT")
            throw new InvalidOperationException("Expected the CasaZen sandbox account.");

        var meters = new MeterService(client);
        Meter? found = null;
        await foreach (var meter in meters.ListAutoPagingAsync(new MeterListOptions { Status = "active" }))
        {
            if (meter.EventName != eventName) continue;
            found = meter;
            break;
        }
        found ??= await meters.CreateAsync(new MeterCreateOptions
        {
            DisplayName = $"CasaZen TEST – Agency {module} – monthly active sellable units",
            EventName = eventName,
            DefaultAggregation = new() { Formula = "last" },
            CustomerMapping = new() { Type = "by_id", EventPayloadKey = "stripe_customer_id" },
            ValueSettings = new() { EventPayloadKey = "value" }
        }, new RequestOptions { IdempotencyKey = eventName });
        if (found.Livemode || found.Status != "active" || found.DefaultAggregation.Formula != "last" ||
            found.CustomerMapping.Type != "by_id" || found.CustomerMapping.EventPayloadKey != "stripe_customer_id" ||
            found.ValueSettings.EventPayloadKey != "value")
            throw new InvalidOperationException("Existing test meter has incompatible aggregation or event mapping.");

        Console.WriteLine($"CasaZen Agency test meter ({module}): {found.Id}");
    }
}
