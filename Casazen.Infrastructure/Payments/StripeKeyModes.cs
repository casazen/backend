namespace Casazen.Infrastructure.Payments;

/// <summary>Mode of a Stripe API key, read from its prefix (<c>sk_test_…</c>, <c>rk_live_…</c>, <c>pk_test_…</c>).</summary>
public enum StripeKeyMode
{
    /// <summary>Empty, or a value without a known Stripe key prefix.</summary>
    Unknown,

    /// <summary>Test mode: no real charge (<c>sk_test_</c>, <c>rk_test_</c>, <c>pk_test_</c>).</summary>
    Test,

    /// <summary>Live mode: real charges (<c>sk_live_</c>, <c>rk_live_</c>, <c>pk_live_</c>).</summary>
    Live,
}

/// <summary>
/// Reads the mode of the Stripe keys (PL-11, A1-31): Production uses only live keys, every other environment only test
/// keys (<c>docs/runbooks/stripe.md</c> § Environments). Secret, restricted and publishable keys all carry the mode in
/// their prefix.
/// </summary>
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
