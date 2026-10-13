namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable code (ProblemDetails <c>code</c>) and message key (<c>SharedResources.resx</c>, Italian and English) of the errors
/// of the supplier's payments account (SP-14). The Stripe failures reuse the codes of the host's Connect onboarding
/// (<c>stripe_connect_unavailable</c>, <c>stripe_connect_not_configured</c>, <c>stripe_connect_failed</c>,
/// <c>stripe_connect_account_unavailable</c>, <c>connect_return_url_not_configured</c>). The code is snake_case and never
/// renamed: the frontend branches on it.
/// </summary>
public static class SupplierPaymentsErrors
{
    /// <summary>
    /// 422: the supplier has no Stripe Connect account to open (or Stripe refused its dashboard because the onboarding is not
    /// complete). The way forward is the onboarding link.
    /// </summary>
    public const string NotReady = "supplier_payments_not_ready";

    /// <summary>The key of <see cref="NotReady"/>.</summary>
    public const string NotReadyMessageKey = "SupplierPaymentsNotReady";

    /// <summary>Every <c>SharedResources</c> key of this feature (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } = [NotReadyMessageKey];
}
