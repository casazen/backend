namespace Casazen.Core.Exceptions;

/// <summary>
/// A business rule stopped the operation. The API turns it into a ProblemDetails response with the
/// stable <see cref="Code"/> and the localized text of <see cref="MessageKey"/> (a key of
/// <c>Casazen.Web/Resources/SharedResources.resx</c>, formatted with <see cref="MessageArgs"/>).
/// Throw one of the concrete types: <see cref="DomainRuleException"/> (HTTP 422) or
/// <see cref="DomainConflictException"/> (HTTP 409).
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

    /// <summary>Stable snake_case identifier the frontend can translate (e.g. <c>duplicate_property_slug</c>).</summary>
    public string Code { get; }

    /// <summary>Resource key of the user-facing message.</summary>
    public string MessageKey { get; }

    /// <summary>Format arguments for the localized message.</summary>
    public IReadOnlyList<object> MessageArgs { get; }
}

/// <summary>
/// The request is well formed but breaks a business rule (wrong state, rule not satisfied). HTTP 422.
/// </summary>
public class DomainRuleException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

/// <summary>
/// The request conflicts with the current state of a resource (duplicate, already taken, overlapping). HTTP 409.
/// </summary>
public class DomainConflictException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);
