using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <inheritdoc cref="IVercelDomainsClient"/>
/// <remarks>
/// Endpoints and fields are those of the Vercel REST API as documented for projects (checked against the public reference;
/// the runbook says to confirm them against the current reference before the first activation, because the provider
/// versions its paths): <c>POST /v10/projects/{idOrName}/domains</c> (body <c>{"name": …}</c>),
/// <c>GET /v9/projects/{idOrName}/domains/{domain}</c>, <c>POST /v9/projects/{idOrName}/domains/{domain}/verify</c> and
/// <c>DELETE /v9/projects/{idOrName}/domains/{domain}</c>, all with <c>Authorization: Bearer {token}</c> and the optional
/// <c>teamId</c> query parameter; a domain answers <c>name</c>, <c>verified</c> and <c>verification[]</c>
/// (<c>type</c>, <c>domain</c>, <c>value</c>, <c>reason</c>). The token never leaves the Authorization header: it is not
/// logged, and neither are provider messages (only the status and the error code).
/// </remarks>
public sealed class VercelDomainsClient(
    HttpClient httpClient,
    IOptions<VercelDomainsOptions> options,
    ILogger<VercelDomainsClient> logger) : IVercelDomainsClient
{
    private const int MaxErrorCodeLength = 64;

    public bool IsConfigured => options.Value.IsConfigured;

    public Task<VercelCallResult<VercelDomain>> GetDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"v9/projects/{Project}/domains/{Encode(domain)}", null, ReadDomainAsync, cancellationToken);

    public Task<VercelCallResult<VercelDomain>> AddDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"v10/projects/{Project}/domains", JsonContent.Create(new { name = domain }), ReadDomainAsync, cancellationToken);

    public Task<VercelCallResult<VercelDomain>> VerifyDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"v9/projects/{Project}/domains/{Encode(domain)}/verify", JsonContent.Create(new { }), ReadDomainAsync, cancellationToken);

    public Task<VercelCallResult<bool>> RemoveDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        SendAsync<bool>(HttpMethod.Delete, $"v9/projects/{Project}/domains/{Encode(domain)}", null, (_, _) => Task.FromResult(true), cancellationToken);

    private string Project => Uri.EscapeDataString(options.Value.ProjectId!.Trim());

    private static string Encode(string domain) => Uri.EscapeDataString(domain);

    private async Task<VercelCallResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? body,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readValue,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
            return new VercelCallResult<T>(VercelCallStatus.NotConfigured);

        if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("Vercel__ApiBaseUrl is not an https URL: Vercel domain calls are disabled.");
            return new VercelCallResult<T>(VercelCallStatus.NotConfigured);
        }

        var url = new Uri(baseUri, path);
        if (!string.IsNullOrWhiteSpace(settings.TeamId))
            url = new Uri($"{url}?teamId={Uri.EscapeDataString(settings.TeamId.Trim())}");

        using var request = new HttpRequestMessage(method, url) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken!.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));

        try
        {
            using var response = await httpClient.SendAsync(request, timeout.Token);
            var status = Map(response.StatusCode);
            if (status == VercelCallStatus.Ok)
                return new VercelCallResult<T>(status, await readValue(response, timeout.Token), (int)response.StatusCode);

            var errorCode = await ReadErrorCodeAsync(response, timeout.Token);
            // Status and error code only: never the token, never the provider's message.
            logger.LogWarning(
                "Vercel Domains API {Method} {Path} answered {Status} ({ErrorCode})",
                method.Method, path, (int)response.StatusCode, errorCode ?? "no code");
            return new VercelCallResult<T>(status, default, (int)response.StatusCode, errorCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Vercel Domains API {Method} {Path} timed out", method.Method, path);
            return new VercelCallResult<T>(VercelCallStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Vercel Domains API {Method} {Path} failed", method.Method, path);
            return new VercelCallResult<T>(VercelCallStatus.Unavailable);
        }
    }

    private static VercelCallStatus Map(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => VercelCallStatus.Ok,
        401 or 403 => VercelCallStatus.Unauthorized,
        404 => VercelCallStatus.NotFound,
        409 => VercelCallStatus.Conflict,
        429 => VercelCallStatus.RateLimited,
        >= 500 => VercelCallStatus.Unavailable,
        _ => VercelCallStatus.Rejected,
    };

    private static async Task<VercelDomain> ReadDomainAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;

        var challenges = new List<VercelVerificationChallenge>();
        if (root.TryGetProperty("verification", out var verification) && verification.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in verification.EnumerateArray())
            {
                var type = Text(item, "type");
                var domain = Text(item, "domain");
                var value = Text(item, "value");
                if (type is not null && domain is not null && value is not null)
                    challenges.Add(new VercelVerificationChallenge(type, domain, value, Text(item, "reason")));
            }
        }

        // Only an explicit true is "verified": an answer without the field proves nothing.
        var verified = root.TryGetProperty("verified", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new VercelDomain(Text(root, "name") ?? string.Empty, verified, challenges);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && Text(error, "code") is { } code)
            {
                var safe = new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
                return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, MaxErrorCodeLength)];
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            // An answer that is not the documented error object has no code.
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
