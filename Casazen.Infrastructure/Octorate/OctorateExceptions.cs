using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// An Octorate call that did not give what was asked (ADR-023-R6), as a typed error: the API answers it with
/// <see cref="ResponseStatusCode"/>, the stable <see cref="Code"/> and the localized text of <see cref="MessageKey"/> (a key of
/// <c>SharedResources.resx</c>, Italian and English), through <c>ErrorHandlingMiddleware</c>.
/// </summary>
/// <remarks>
/// The message names the code and Octorate's HTTP status only: never the body of the answer (it may hold personal data), a token
/// or a code.
/// </remarks>
public abstract class OctorateException : Exception
{
    protected OctorateException(
        string code,
        string messageKey,
        int responseStatusCode,
        int? octorateStatusCode,
        Exception? innerException = null)
        : base(
            octorateStatusCode is { } status ? $"Octorate error '{code}' (HTTP {status})" : $"Octorate error '{code}'",
            innerException)
    {
        Code = code;
        MessageKey = messageKey;
        ResponseStatusCode = responseStatusCode;
        OctorateStatusCode = octorateStatusCode;
    }

    /// <summary>Stable snake_case code the clients translate.</summary>
    public string Code { get; }

    /// <summary>Key of the user-facing message in <c>SharedResources.resx</c>.</summary>
    public string MessageKey { get; }

    /// <summary>The status CasaZen answers its caller with.</summary>
    public int ResponseStatusCode { get; }

    /// <summary>What Octorate answered; <c>null</c> when no answer arrived or the error is CasaZen's own.</summary>
    public int? OctorateStatusCode { get; }
}

/// <summary>
/// Octorate refused the credentials or the token (401, 403 that is not a wrong environment, a refusal of the guest data before the
/// outbound IPs are whitelisted), or the org has no usable connection. Never mapped to an empty result.
/// </summary>
public sealed class OctorateAuthException : OctorateException
{
    /// <summary>Code of an org with no usable connection (none, or disconnected).</summary>
    public const string NotConnectedCode = "octorate_not_connected";

    /// <summary>Code of a refusal by Octorate.</summary>
    public const string RefusedCode = "octorate_auth_failed";

    /// <summary>Code of a chat call refused because the <c>chat</c> module is not in the license of the accommodation (E05/F01/US01/T02).</summary>
    public const string ChatNotLicensedCode = OtaMessageSyncErrorCodes.ChatNotLicensed;

    private OctorateAuthException(string code, string messageKey, int responseStatusCode, int? octorateStatusCode)
        : base(code, messageKey, responseStatusCode, octorateStatusCode)
    {
    }

    /// <summary>True when the org has no usable connection: the host has to connect (again).</summary>
    public bool IsNotConnected => Code == NotConnectedCode;

    /// <summary>True when the chat of the accommodation is not in its license: its messages cannot be read, and that is a state, not a failure to retry.</summary>
    public bool IsChatNotLicensed => Code == ChatNotLicensedCode;

    /// <summary>
    /// Octorate refused a chat call with 403 (or, for <c>recentMessages</c>, 500) and no error of its own API (the page of its application server, which
    /// is what a license without the <c>chat</c> module gets in the sandbox): 409, the host asks for the module. The decision is the code and the shape
    /// of the answer, never its text.
    /// </summary>
    public static OctorateAuthException ChatNotLicensed(int? octorateStatusCode) =>
        new(ChatNotLicensedCode, "OctorateChatNotLicensed", StatusCodes.Status409Conflict, octorateStatusCode);

    /// <summary>The org has no connection, or it is disconnected: 409, the host connects again.</summary>
    public static OctorateAuthException NotConnected() =>
        new(NotConnectedCode, "OctorateNotConnected", StatusCodes.Status409Conflict, null);

    /// <summary>Octorate refused the credentials or the token with <paramref name="octorateStatusCode"/>: 502.</summary>
    public static OctorateAuthException Refused(int? octorateStatusCode) =>
        new(RefusedCode, "OctorateAuthFailed", StatusCodes.Status502BadGateway, octorateStatusCode);
}

/// <summary>
/// Octorate refused the sensitive data of the guests (E07/F06/US02/T02): the read of a guest's contacts answered 401 or 403, because the static
/// IP addresses of CasaZen are not registered with Octorate yet (S7, S18, D24) or the application lacks the <c>contactInfo</c> permission. Its own
/// type, so that no caller turns a refusal into "the guest has no contact": 409, the product owner has to complete the registration, a retry
/// does not help.
/// </summary>
public sealed class OctorateSensitiveDataNotAllowedException(int? octorateStatusCode)
    : OctorateException(
        "octorate_sensitive_data_not_allowed",
        "OctorateSensitiveDataNotAllowed",
        StatusCodes.Status409Conflict,
        octorateStatusCode);

/// <summary>
/// A conversation CasaZen cannot write to (E05/F02/US02/T01): the channel manager's description of the thread names no person of the host side to write
/// as, or no id of the thread on the channel, so the message it needs cannot be built. Raised before anything is sent: the message did not leave. 409,
/// the host answers on the channel manager.
/// </summary>
public sealed class OctorateChatNotSendableException()
    : OctorateException("octorate_chat_not_sendable", "OctorateChatNotSendable", StatusCodes.Status409Conflict, null);

/// <summary>
/// The read of the thread that comes before a message is sent (E05/F02/US02/T01) failed: no answer, a 5xx, or an answer that cannot be read. <b>Nothing was
/// sent</b>: the message is built from that read, so it was never posted, and the caller may send it again with the same key. 502, like any failure of
/// Octorate. Not an <see cref="OctorateServerException"/> on purpose: that one, when it comes from the POST itself, is the outcome nobody knows.
/// </summary>
public sealed class OctorateChatNotReadException(int? octorateStatusCode, Exception? innerException = null)
    : OctorateException("octorate_unavailable", "OctorateUnavailable", StatusCodes.Status502BadGateway, octorateStatusCode, innerException);

/// <summary>Octorate failed (5xx), did not answer in time, or answered something that cannot be read: 502, worth a later retry.</summary>
public sealed class OctorateServerException(int? octorateStatusCode, Exception? innerException = null)
    : OctorateException("octorate_unavailable", "OctorateUnavailable", StatusCodes.Status502BadGateway, octorateStatusCode, innerException);

/// <summary>Octorate does not know what the call names (404): the accommodation, the room, the reservation.</summary>
public sealed class OctorateNotFoundException()
    : OctorateException("octorate_not_found", "OctorateNotFound", StatusCodes.Status404NotFound, StatusCodes.Status404NotFound);

/// <summary>Octorate refused a change that conflicts with its data (409).</summary>
public sealed class OctorateConflictException()
    : OctorateException("octorate_conflict", "OctorateConflict", StatusCodes.Status409Conflict, StatusCodes.Status409Conflict);

/// <summary>
/// The credentials belong to the other environment (NF-28, ADR-023-R6): Octorate answered 403 <c>WrongEnviroment</c> (Octorate's
/// spelling), or its configuration reports an environment that is not the connection's. The connection is left disconnected: it never
/// silently goes on in the wrong environment.
/// </summary>
public sealed class OctorateWrongEnvironmentException(int? octorateStatusCode)
    : OctorateException("octorate_wrong_environment", "OctorateWrongEnvironment", StatusCodes.Status409Conflict, octorateStatusCode)
{
    /// <summary>The <c>type</c> of Octorate's 403 for credentials of the other environment, spelled as Octorate spells it.</summary>
    public const string OctorateErrorType = "WrongEnviroment";
}

/// <summary>
/// Too many calls (ADR-023-R8, R9): CasaZen's own bucket of the org is empty (no call left Octorate), or Octorate answered 429 again after
/// the three retries. 429, try again later.
/// </summary>
public sealed class OctorateRateLimitException(int? octorateStatusCode)
    : OctorateException("octorate_rate_limited", "OctorateRateLimited", StatusCodes.Status429TooManyRequests, octorateStatusCode);

/// <summary>Octorate refused the request itself (400, 405, 406, 415, 422 and the other 4xx not mapped above): 502.</summary>
public sealed class OctorateRequestRejectedException(int octorateStatusCode)
    : OctorateException(RejectedCode, "OctorateRequestRejected", StatusCodes.Status502BadGateway, octorateStatusCode)
{
    /// <summary>The stable code of a request Octorate refused (also the code of a message that was refused before it left CasaZen).</summary>
    public const string RejectedCode = "octorate_request_rejected";
}

/// <summary>
/// The channel or tool behind a connection failed (Octorate's 550, «the partner fails», E09/F02/US01/T01, ADR-023-R6): Octorate took the request and
/// the portal did not (wrong access data, the portal down, the portal refusing the connection). Not a failure of CasaZen and not one of Octorate's
/// API: 502, and the host checks the access data of the channel and tries again later. Raised by the operations on a connection only; the other
/// calls keep their mapping of 5xx.
/// </summary>
public sealed class OctoratePartnerFailureException()
    : OctorateException("octorate_partner_failed", "OctoratePartnerFailed", StatusCodes.Status502BadGateway, OctorateStatus)
{
    /// <summary>The status Octorate answers with when the partner (the channel or tool) fails.</summary>
    public const int OctorateStatus = 550;
}

/// <summary>
/// The <c>state</c> of a consent is not valid (E02/F01/US02/T01): unknown, already used, expired, or bound to another org, user or
/// redirect address. Always the same 400, whatever failed, so the answer tells nothing about which check refused it.
/// </summary>
public sealed class OctorateOAuthStateInvalidException()
    : OctorateException("octorate_state_invalid", "OctorateStateInvalid", StatusCodes.Status400BadRequest, null);

/// <summary>
/// Octorate did not exchange the consent code for tokens (E02/F01/US02/T01): a code that is not valid or expired (403
/// <c>OAuthGrant</c>), no answer, or an answer without the tokens. 502: the host starts the consent again.
/// </summary>
public sealed class OctorateExchangeFailedException(int? octorateStatusCode, Exception? innerException = null)
    : OctorateException("octorate_exchange_failed", "OctorateExchangeFailed", StatusCodes.Status502BadGateway, octorateStatusCode, innerException);

/// <summary>
/// Octorate refused the data of an accommodation CasaZen asked it to create (E02/F03/US01/T06): 400 <c>ApiParamsExemption</c>, with the fields it
/// names (<c>machine_name</c> and message). The creator turns it into 422 <c>octorate_property_invalid</c> with the fields, so the wizard shows them
/// on the step that owns each one. The message of the exception names the code only: the fields carry the names Octorate gave, never a value.
/// </summary>
public sealed class OctoratePropertyRejectedException(IReadOnlyList<OctorateFieldError> fields)
    : OctorateException("octorate_property_invalid", "OctoratePropertyInvalid", StatusCodes.Status422UnprocessableEntity, StatusCodes.Status400BadRequest)
{
    /// <summary>The fields Octorate refused.</summary>
    public IReadOnlyList<OctorateFieldError> Fields { get; } = fields;
}

/// <summary>
/// The network named for a new accommodation is not one Octorate knows (E02/F03/US01/T06): 412. The new accommodation was not created; the host
/// connects the account again.
/// </summary>
public sealed class OctorateNetworkUnknownException()
    : OctorateException("octorate_network_unknown", "OctorateNetworkUnknown", StatusCodes.Status409Conflict, StatusCodes.Status412PreconditionFailed);
