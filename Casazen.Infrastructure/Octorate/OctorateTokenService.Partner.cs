using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The partner token of <see cref="OctorateTokenService"/> (E09/F01/US01/T02, ADR-023-R5): the access token of the application itself, for
/// the calls that carry no property data and act for no org, such as the catalog of portals.
/// </summary>
/// <remarks>
/// <para><b>Login.</b> <c>POST {Octorate:ApiBaseUrl}/rest/v1/identity/apilogin</c> with the application credentials
/// (<c>Octorate:ClientId</c>, <c>Octorate:ClientSecret</c>: no default in code) through the same <see cref="HttpClient"/> as the refresh. It
/// answers an access token valid about 36 hours (<c>expires_in</c> 129599 in the sandbox) and no refresh token: the application logs in
/// again.</para>
/// <para><b>Cache.</b> One token for the whole process (<see cref="OctoratePartnerTokenCache"/>, a singleton), reused while it is valid and
/// renewed once it expires within <see cref="RefreshWindow"/> (five minutes). A renewal runs under a gate: the callers that wait for it
/// read the token it left and do not log in again, so there are never two renewals at once. When the answer does not say how long the
/// token lives it is renewed after an hour, which costs a login and risks nothing.</para>
/// <para><b>Refusal.</b> Octorate refusing the credentials (400, 401, 403) is thrown as its typed error
/// (<see cref="OctorateApiClient.ErrorOf"/>), a failure of Octorate (5xx, timeout, network, an answer that cannot be read) as
/// <see cref="OctorateServerException"/>; the cached token is left as it was in both cases.</para>
/// <para><b>Secrets.</b> The client secret and the token are never logged, never put in a message and never returned by anything but the
/// method: the log lines say that a token was handed out or renewed, with its expiry, and the status and error type of a refusal.</para>
/// </remarks>
public sealed partial class OctorateTokenService
{
    private const string PartnerLoginPath = "/rest/v1/identity/apilogin";

    /// <summary>The free text <c>my_note</c> of the login, a label Octorate keeps with the token.</summary>
    private const string PartnerNote = "CasaZen";

    /// <summary>How long a partner token is trusted when Octorate does not say when it expires.</summary>
    private static readonly TimeSpan UnknownPartnerTokenLifetime = TimeSpan.FromHours(1);

    private static readonly EventId PartnerTokenAccessedEvent = new(52_002, "OctoratePartnerTokenAccessed");
    private static readonly EventId PartnerTokenRenewedEvent = new(52_003, "OctoratePartnerTokenRenewed");

    /// <summary>The shared cache; one of its own when none was registered (a test, a host that did not call the registration).</summary>
    private readonly OctoratePartnerTokenCache _partnerTokens = partnerTokens ?? new OctoratePartnerTokenCache();

    public async Task<string> GetPartnerAccessTokenAsync(CancellationToken ct)
    {
        if (_partnerTokens.Current is { } current && IsUsable(current))
        {
            LogPartnerAccess();
            return current.Value;
        }

        return await RenewPartnerTokenAsync(rejectedAccessToken: null, ct);
    }

    public Task<string> RefreshRejectedPartnerTokenAsync(string rejectedAccessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(rejectedAccessToken);
        return RenewPartnerTokenAsync(rejectedAccessToken, ct);
    }

    /// <summary>
    /// The renewal, under the gate. <paramref name="rejectedAccessToken"/>: the token Octorate refused (a forced renewal), or <c>null</c> for
    /// one that is missing or expires soon. A usable token that is not the rejected one was left by another caller while this one waited.
    /// </summary>
    private async Task<string> RenewPartnerTokenAsync(string? rejectedAccessToken, CancellationToken ct)
    {
        await _partnerTokens.Gate.WaitAsync(ct);
        try
        {
            if (_partnerTokens.Current is { } current
                && IsUsable(current)
                && (rejectedAccessToken is null || !SameToken(current.Value, rejectedAccessToken)))
            {
                LogPartnerAccess();
                return current.Value;
            }

            var renewed = await CallPartnerLoginAsync(ct);
            _partnerTokens.Current = renewed;
            logger.LogInformation(
                PartnerTokenRenewedEvent, "OctoratePartnerTokenRenewed, valid until {ExpiresAt:O}", renewed.ExpiresAt);
            LogPartnerAccess();
            return renewed.Value;
        }
        finally
        {
            _partnerTokens.Gate.Release();
        }
    }

    /// <summary>The partner login. Throws the typed error of a refusal or of a failure of Octorate; never logs the secret, a body or a token.</summary>
    private async Task<PartnerToken> CallPartnerLoginAsync(CancellationToken ct)
    {
        var clientId = configuration[ClientIdKey];
        var clientSecret = configuration[ClientSecretKey];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                $"{ClientIdKey} and {ClientSecretKey} are not configured (Octorate__ClientId, Octorate__ClientSecret): the application cannot sign in to Octorate.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OctorateHttp.Address(OctorateHttp.ApiBaseUrl(configuration), PartnerLoginPath))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["my_note"] = PartnerNote,
            }),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OctorateServerException(null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The 30-second timeout of the client.
            throw new OctorateServerException(null, ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await OctorateHttp.ReadBodyAsync(response, ct);
            if (response.IsSuccessStatusCode)
            {
                var tokens = OctorateHttp.ReadTokens(body, Now(), status);

                // A success with no access token is an answer that cannot be used, not a refusal: nothing is cached, a later call retries.
                if (tokens.AccessToken is not { Length: > 0 } accessToken)
                    throw new OctorateServerException(status);

                return new PartnerToken(accessToken, tokens.ExpiresAt ?? Now() + UnknownPartnerTokenLifetime);
            }

            if (status is 400 or 401 or 403)
            {
                // The type of the error and the status only: never the body, which could repeat what was sent.
                var (type, suggestion) = OctorateHttp.ReadError(body);
                logger.LogWarning(
                    "Octorate refused the partner login (HTTP {StatusCode}, {ErrorType}): the application credentials are not accepted",
                    status, type ?? suggestion ?? "no type");
                throw OctorateApiClient.ErrorOf(status, type, body);
            }

            throw new OctorateServerException(status);
        }
    }

    /// <summary>A cached token is used until it expires within <see cref="RefreshWindow"/>.</summary>
    private bool IsUsable(PartnerToken token) => token.ExpiresAt > Now() + RefreshWindow;

    private void LogPartnerAccess() =>
        logger.LogInformation(PartnerTokenAccessedEvent, "OctoratePartnerTokenAccessed");
}
