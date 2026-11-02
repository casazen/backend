namespace Casazen.Core.Services;

/// <summary>
/// Stable code (ProblemDetails <c>code</c>) and message key (<c>SharedResources.resx</c>, Italian and English) of the refusal of the
/// dashboard link of the org's own Stripe account (AM-08b-d, «Gestisci su Stripe»). The Stripe failures reuse the codes of the
/// Connect onboarding (<c>stripe_connect_unavailable</c>, <c>stripe_connect_not_configured</c>, <c>stripe_connect_failed</c>,
/// <c>stripe_connect_account_unavailable</c>). The code is snake_case and never renamed: the frontend branches on it.
/// </summary>
public static class ConnectDashboardErrors
{
    /// <summary>
    /// 422: the org has no Stripe account to open, or Stripe refused its dashboard because the onboarding is not complete. The way
    /// forward is the onboarding link («Collega Stripe» / «Completa la verifica»).
    /// </summary>
    public const string NotReady = "stripe_connect_dashboard_not_ready";

    /// <summary>The key of <see cref="NotReady"/>.</summary>
    public const string NotReadyMessageKey = "StripeConnectDashboardNotReady";
}
