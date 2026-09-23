namespace Casazen.Core.Exceptions;

/// <summary>
/// The requested resource does not exist (or is not visible to the caller). HTTP 404.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is only for logs and is never sent to clients. Set
/// <see cref="Code"/> and <see cref="MessageKey"/> to give the caller a specific code and localized
/// message; otherwise the API answers with the generic <c>not_found</c> code and message.
/// </remarks>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Stable snake_case identifier (e.g. <c>guest_not_found</c>); <c>not_found</c> when null.</summary>
    public string? Code { get; init; }

    /// <summary>Resource key of the user-facing message; the generic not-found message when null.</summary>
    public string? MessageKey { get; init; }
}
