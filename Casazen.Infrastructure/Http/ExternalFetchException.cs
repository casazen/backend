namespace Casazen.Infrastructure.Http;

public enum ExternalFetchFailure
{
    InvalidUrl,

    BlockedDestination,

    RedirectRejected,

    Unreachable,

    Timeout,

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
