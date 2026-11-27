namespace Casazen.Infrastructure.Payments;

public enum StripeKeyMode
{
    Unknown,

    Test,

    Live,
}

public static class StripeKeyModes
{
    private static readonly string[] KeyTypes = ["sk_", "rk_", "pk_"];

    public static StripeKeyMode Of(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return StripeKeyMode.Unknown;

        var trimmed = key.Trim();
        foreach (var type in KeyTypes)
        {
            if (trimmed.StartsWith(type + "test_", StringComparison.Ordinal))
                return StripeKeyMode.Test;
            if (trimmed.StartsWith(type + "live_", StringComparison.Ordinal))
                return StripeKeyMode.Live;
        }

        return StripeKeyMode.Unknown;
    }
}
