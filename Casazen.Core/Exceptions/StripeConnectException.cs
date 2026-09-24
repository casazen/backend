namespace Casazen.Core.Exceptions;

/// <summary>
/// Kind of failure of a Stripe Connect call (BK-09, A3-19). Only <see cref="AccountUnavailable"/> lets the onboarding
/// replace the org's connected account: every other failure leaves it untouched.
/// </summary>
public enum StripeConnectFailure
{
    /// <summary>
    /// Stripe says the connected account does not exist or the platform no longer has access to it: error code
    /// <c>resource_missing</c> or <c>account_invalid</c>.
    /// </summary>
    AccountUnavailable,

    /// <summary>
    /// Temporary: 429 (<c>rate_limit</c>, <c>lock_timeout</c>), 409, any 5xx, network error or timeout. Retry later.
    /// </summary>
    Transient,

    /// <summary>
    /// The platform key is missing, a placeholder, invalid, expired or without permission (401/403,
    /// <c>api_key_expired</c>, <c>platform_api_key_expired</c>, <c>secret_key_required</c>): an operator fixes Railway.
    /// </summary>
    Configuration,

    /// <summary>Stripe refused the request for another reason (e.g. 400 <c>invalid_request_error</c>, <c>idempotency_error</c>).</summary>
    Rejected,
}

/// <summary>
/// A Stripe Connect call failed (<see cref="Failure"/> says how). A <see cref="PaymentProcessingException"/>, so a caller
/// that does not handle it still answers 503 <c>payment_provider_error</c>. <see cref="Exception.Message"/> is for logs only.
/// </summary>
public sealed class StripeConnectException : PaymentProcessingException
{
    public StripeConnectException(StripeConnectFailure failure, string message, string? stripeErrorCode = null)
        : base(message)
    {
        Failure = failure;
        StripeErrorCode = stripeErrorCode;
    }

    public StripeConnectException(
        StripeConnectFailure failure,
        string message,
        Exception innerException,
        string? stripeErrorCode = null)
        : base(message, innerException)
    {
        Failure = failure;
        StripeErrorCode = stripeErrorCode;
    }

    public StripeConnectFailure Failure { get; }

    /// <summary>The <c>error.code</c> Stripe answered with, when there was one (logs and tests).</summary>
    public string? StripeErrorCode { get; }
}
