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

    /// <summary>Stable snake_case identifier the frontend can translate (e.g. <c>duplicate_property_slug</c>).</summary>
    public string Code { get; }

    /// <summary>Resource key of the user-facing message.</summary>
    public string MessageKey { get; }

    /// <summary>Format arguments for the localized message.</summary>
    public IReadOnlyList<object> MessageArgs { get; }

    /// <summary>
    /// Extra members of the problem the API answers with, next to <c>code</c> and <c>detail</c>: the facts a client needs to act on the
    /// error (a range of dates, the id of the record in the way). Camel-case names and values that serialize to JSON, never personal
    /// data. The names <c>code</c> and <c>traceId</c> belong to the API and are ignored here.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; init; } = NoExtensions;

    private static readonly IReadOnlyDictionary<string, object?> NoExtensions = new Dictionary<string, object?>();
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

/// <summary>
/// The caller is signed in but a rule of the domain does not let it do this, with a code of its own that the client
/// translates (e.g. <c>invitation_email_mismatch</c>: the account is not the one the invitation was written for). HTTP 403.
/// Not for a missing permission: that is the job of the authorization policies (<c>CasazenPolicies</c>).
/// </summary>
public class DomainForbiddenException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

/// <summary>
/// What the request names existed and cannot be used any more, for good (an invitation link that expired, was used or was
/// revoked). HTTP 410. Unlike a 404 it says "it was here", so use it only where that tells nothing a stranger could use.
/// </summary>
public class DomainGoneException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

/// <summary>
/// A limit of the domain is used up (a daily quota, a cap of pending items) and the caller must wait or free something. HTTP
/// 429 with the stable <see cref="DomainException.Code"/>. Not for the rate limiting middleware, which answers by itself.
/// </summary>
public class DomainTooManyRequestsException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

/// <summary>
/// A file or a body is larger than the rule allows, with a code of its own (<c>showcase_media_too_large</c>: a logo over 200 KB). HTTP 413.
/// For a request the server refuses before it reads it, the framework answers 413 by itself.
/// </summary>
public class DomainPayloadTooLargeException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);

/// <summary>
/// A file is not of a type the rule accepts, judged on its content and not on its name (<c>showcase_media_unsupported_type</c>: an SVG or a text
/// renamed to <c>.jpg</c>). HTTP 415.
/// </summary>
public class DomainUnsupportedMediaTypeException(string code, string messageKey, params object[] messageArgs)
    : DomainException(code, messageKey, messageArgs);
