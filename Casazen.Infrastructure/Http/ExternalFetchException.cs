namespace Casazen.Infrastructure.Http;

/// <summary>Why <see cref="ISafeExternalHttpClient"/> did not return a response body.</summary>
public enum ExternalFetchFailure
{
    /// <summary>The requested URL itself is not allowed (<see cref="ExternalUrlPolicy.TryParse"/>).</summary>
    InvalidUrl,

    /// <summary>The host resolved to, or connected to, an address that <see cref="ExternalUrlPolicy.IsBlockedAddress"/> refuses.</summary>
    BlockedDestination,

    /// <summary>A redirect pointed to a URL that is not allowed, had no location, or there were too many.</summary>
    RedirectRejected,

    /// <summary>DNS or connection failure, TLS error or non-success HTTP status.</summary>
    Unreachable,

    /// <summary>The time budget ran out.</summary>
    Timeout,

    /// <summary>The body is larger than the configured limit.</summary>
    TooLarge,
}

/// <summary>
/// A download refused or failed by <see cref="ISafeExternalHttpClient"/>. <see cref="Exception.Message"/> is for logs
/// only (it never contains the URL, which can carry secret tokens) and must not be shown to users: map
/// <see cref="Failure"/> to a stable error code instead.
/// </summary>
public sealed class ExternalFetchException(ExternalFetchFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ExternalFetchFailure Failure { get; } = failure;
}
