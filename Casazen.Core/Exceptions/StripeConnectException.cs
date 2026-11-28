namespace Casazen.Core.Exceptions;

public enum StripeConnectFailure
{
    AccountUnavailable,

    /// <summary>
    /// Temporary: 429 (<c>rate_limit</c>, <c>lock_timeout</c>), 409, any 5xx, network error or timeout. Retry later.
    /// </summary>
    Transient,

    Configuration,

    /// <summary>Stripe refused the request for another reason (e.g. 400 <c>invalid_request_error</c>, <c>idempotency_error</c>).</summary>
    Rejected,
}

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

    public string? StripeErrorCode { get; }
}
