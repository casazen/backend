using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What every HTTP call to Octorate shares (ADR-023-R5): the base address from configuration (<c>Octorate:ApiBaseUrl</c>, https only,
/// one value for sandbox and production; each call carries its own <c>/rest/v1/...</c> or <c>/rest/v2/...</c> path), a 30-second
/// timeout, the <c>CasaZen/{version}</c> User-Agent, JSON in and out, and the reading of Octorate's answers and errors. Nothing here
/// logs a body, a token or a code.
/// </summary>
public static class OctorateHttp
{
    public const string ApiBaseUrlKey = "Octorate:ApiBaseUrl";

    private const int MaxBodyChars = 4 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly ProductInfoHeaderValue UserAgent = new("CasaZen", Version());

    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string ApiBaseUrl(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[ApiBaseUrlKey]?.Trim();
        if (string.IsNullOrEmpty(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{ApiBaseUrlKey} must be the absolute https address of the Octorate API (Octorate__ApiBaseUrl, docs/runbooks/deploy-checklist.md).");
        }

        return value.TrimEnd('/');
    }

    public static Uri Address(string apiBaseUrl, string pathAndQuery) => new(apiBaseUrl + pathAndQuery, UriKind.Absolute);

    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length <= MaxBodyChars ? body : body[..MaxBodyChars];
    }

    /// <summary>
    /// The <c>type</c> and the <c>suggestion</c> of an Octorate error (<c>{"type": "ApiLoginInvalid", ...}</c>,
    /// <c>{"type": "ApiSecurityException", "suggestion": "errRefreshTokenInvalid"}</c>); both <c>null</c> for an answer that is
    /// not a JSON object, such as the HTML page of the application server.
    /// </summary>
    public static (string? Type, string? Suggestion) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (StringOf(document.RootElement, "type"), StringOf(document.RootElement, "suggestion"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public static OctorateTokenResponse ReadTokens(string body, DateTime nowUtc, int? octorateStatusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new OctorateServerException(octorateStatusCode);

            var access = StringOf(root, "access_token") ?? StringOf(root, "accessToken");
            var refresh = StringOf(root, "refresh_token");
            DateTime? expiresAt = null;
            if (root.TryGetProperty("expires_in", out var expiresIn) && TryReadLong(expiresIn, out var seconds) && seconds > 0)
            {
                expiresAt = nowUtc.AddSeconds(seconds);
            }
            else if (StringOf(root, "expireDate") is { } expireDate
                     && DateTimeOffset.TryParse(expireDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                expiresAt = parsed.UtcDateTime;
            }

            return new OctorateTokenResponse(access, refresh, expiresAt);
        }
        catch (JsonException ex)
        {
            throw new OctorateServerException(octorateStatusCode, ex);
        }
    }

    public static string? StringOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static bool TryReadLong(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out number),
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number),
            _ => false,
        };
    }

    private static string Version()
    {
        var assembly = typeof(OctorateHttp).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = (informational ?? assembly.GetName().Version?.ToString() ?? "1.0.0").Split('+')[0].Trim();
        return version.Length > 0 && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? version : "1.0.0";
    }
}
