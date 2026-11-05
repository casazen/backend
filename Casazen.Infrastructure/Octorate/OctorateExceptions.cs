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

    private OctorateAuthException(string code, string messageKey, int responseStatusCode, int? octorateStatusCode)
        : base(code, messageKey, responseStatusCode, octorateStatusCode)
    {
    }

    /// <summary>True when the org has no usable connection: the host has to connect (again).</summary>
    public bool IsNotConnected => Code == NotConnectedCode;

    /// <summary>The org has no connection, or it is disconnected: 409, the host connects again.</summary>
    public static OctorateAuthException NotConnected() =>
        new(NotConnectedCode, "OctorateNotConnected", StatusCodes.Status409Conflict, null);

    /// <summary>Octorate refused the credentials or the token with <paramref name="octorateStatusCode"/>: 502.</summary>
    public static OctorateAuthException Refused(int? octorateStatusCode) =>
        new(RefusedCode, "OctorateAuthFailed", StatusCodes.Status502BadGateway, octorateStatusCode);
}

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
    : OctorateException("octorate_request_rejected", "OctorateRequestRejected", StatusCodes.Status502BadGateway, octorateStatusCode);
