namespace Casazen.Core.Exceptions;

/// <summary>
/// A business rule stopped the operation. The API turns it into a ProblemDetails response with the
/// stable <see cref="Code"/> and the localized text of <see cref="MessageKey"/> (a key of
/// <c>Casazen.Web/Resources/SharedResources.resx</c>, formatted with <see cref="MessageArgs"/>).
/// Throw one of the concrete types: <see cref="DomainRuleException"/> (HTTP 422),
/// <see cref="DomainConflictException"/> (HTTP 409), <see cref="DomainForbiddenException"/> (HTTP 403),
/// <see cref="DomainGoneException"/> (HTTP 410), <see cref="DomainPayloadTooLargeException"/> (HTTP 413) or
/// <see cref="DomainUnsupportedMediaTypeException"/> (HTTP 415).
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is only for logs and is never sent to clients: keep it free of
/// personal data (e-mails, names, tax codes). Message arguments are shown to the caller only.
/// </remarks>
public abstract class DomainException : Exception
{
    protected DomainException(string code, string messageKey, object[] messageArgs, Exception? innerException = null)
        : base($"Domain error '{code}'", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        Code = code;
        MessageKey = messageKey;
        MessageArgs = messageArgs;
    }

    public string Code { get; }

    public string MessageKey { get; }

    public IReadOnlyList<object> MessageArgs { get; }

    /// <summary>
    /// Extra members of the problem the API answers with, next to <c>code</c> and <c>detail</c>: the facts a client needs to act on the
    /// error (a range of dates, the id of the record in the way). Camel-case names and values that serialize to JSON, never personal
    /// data. The names <c>code</c> and <c>traceId</c> belong to the API and are ignored here.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; init; } = NoExtensions;

    private static readonly IReadOnlyDictionary<string, object?> NoExtensions = new Dictionary<string, object?>();
}

public class DomainRuleException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

public class DomainConflictException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

public class DomainForbiddenException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

public class DomainGoneException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

public class DomainTooManyRequestsException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

public class DomainPayloadTooLargeException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

public class DomainUnsupportedMediaTypeException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);
