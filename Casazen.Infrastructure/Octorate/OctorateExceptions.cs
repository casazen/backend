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
