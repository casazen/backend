using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// Portal withhold on supplier jobs (PO 2026-10-08): 3.5% of the amount paid through CasaZen is kept as application
/// fee on the platform account. Direct-booking PaymentIntents do not use this: their application fee stays unset so
/// Stripe processing fees stay on the host's connected account.
/// </summary>
public static class MarketplaceCommission
{
    public const string PercentSetting = "Marketplace:CommissionPercent";

    /// <summary>Product default: 3.5% withheld on the portal for supplier payments.</summary>
    public const decimal DefaultPercent = 3.5m;

    public static decimal GetPercent(IConfiguration configuration)
    {
        var percent = configuration.GetValue(PercentSetting, DefaultPercent);
        return percent < 0 ? 0 : percent;
    }

    /// <summary>Stripe application-fee cents for a charge of <paramref name="amountCents"/> at <paramref name="percent"/>.</summary>
    public static long ApplicationFeeCents(long amountCents, decimal percent)
    {
        if (amountCents <= 0 || percent <= 0)
            return 0;
        return (long)decimal.Round(amountCents * percent / 100m, 0, MidpointRounding.AwayFromZero);
    }
}
