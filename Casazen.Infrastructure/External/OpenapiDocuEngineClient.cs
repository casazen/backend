using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// DocuEngine lease-contract registration (openapi.com). The public specification has one registration product;
/// the request id comes back as <c>data.id</c>, states are <c>NEW</c>/<c>SEARCH</c>/<c>WAIT</c>/<c>DONE</c>/<c>CANCELLED</c>,
/// and a 402 is a failure, never a filed contract.
/// </summary>
public sealed class OpenapiDocuEngineClient(
    HttpClient http,
    IOptions<OpenapiOptions> options,
    ILogger<OpenapiDocuEngineClient> logger) : IOpenapiDocuEngineClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OpenapiOptions _options = options.Value;
    private string? _mintedToken;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<string> SubmitLeaseRegistrationAsync(
        LeaseContract lease,
        ReadOnlyMemory<byte> signedContract,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            throw NotConfigured();

        var search = OpenapiLeaseSearch.Build(lease, signedContract, _options.FieldOrderOrEmpty());
        var body = new Dictionary<string, object?>
        {
            ["documentId"] = _options.LeaseDocumentId!.Trim(),
            ["search"] = search,
        };
        if (Callback() is { } callback)
            body["callback"] = callback;

        using var response = await SendAsync(HttpMethod.Post, "requests", body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
            throw Failed(RliRegistrationFailureCodes.ProviderError, "Openapi wallet has no credit for the filing.");

        using var document = await ReadDocumentAsync(response, cancellationToken);
        var id = document is null ? null : RequestId(document.RootElement);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(id))
            throw Failed(RliRegistrationFailureCodes.ProviderError, "Openapi did not accept the lease registration.");

        return id;
    }

    public async Task<ProviderRegistrationStatus> GetStatusAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (!_options.IsConfigured)
            throw NotConfigured();

        using var response = await SendAsync(HttpMethod.Get, "requests/" + Uri.EscapeDataString(requestId), null, cancellationToken);
        using var document = await ReadDocumentAsync(response, cancellationToken);
        var state = document is null ? null : RequestState(document.RootElement);
        if (!response.IsSuccessStatusCode || state is null)
            throw Failed(RliRegistrationFailureCodes.ProviderError, "Openapi did not return the filing status.");

        return state switch
        {
            "DONE" => new ProviderRegistrationStatus(ProviderRegistrationState.Registered),
            "CANCELLED" => new ProviderRegistrationStatus(
                ProviderRegistrationState.Failed, FailureCode: RliRegistrationFailureCodes.ProviderRejected),
            _ => new ProviderRegistrationStatus(ProviderRegistrationState.InProgress),
        };
    }

    public async Task<Stream> DownloadReceiptAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (!_options.IsConfigured)
            throw NotConfigured();

        using var response = await SendAsync(
            HttpMethod.Get, "requests/" + Uri.EscapeDataString(requestId) + "/documents", null, cancellationToken);
        using var document = await ReadDocumentAsync(response, cancellationToken);
        var url = document is null ? null : FirstDownloadUrl(document.RootElement);
        if (!response.IsSuccessStatusCode || url is null)
            throw Failed(RliRegistrationFailureCodes.ProviderError, "Openapi did not return a receipt.");

        var download = await http.GetAsync(url, cancellationToken);
        if (!download.IsSuccessStatusCode)
        {
            download.Dispose();
            throw Failed(RliRegistrationFailureCodes.ProviderError, "Openapi receipt download failed.");
        }

        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        download.Dispose();
        return new MemoryStream(bytes, writable: false);
    }

    private Dictionary<string, object?>? Callback()
    {
        if (string.IsNullOrWhiteSpace(_options.CallbackUrl) || string.IsNullOrWhiteSpace(_options.CallbackSecret))
            return null;
        return new Dictionary<string, object?>
        {
            ["url"] = _options.CallbackUrl.Trim(),
            ["method"] = "JSON",
            ["headers"] = new Dictionary<string, string> { ["X-Casazen-Openapi-Secret"] = _options.CallbackSecret.Trim() },
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await BearerAsync(cancellationToken));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request, cancellationToken);
    }

    private async Task<string> BearerAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.Token))
            return _options.Token.Trim();
        if (_mintedToken is not null)
            return _mintedToken;

        var oauth = _options.OAuthUrl!.TrimEnd('/');
        var host = new Uri(_options.BaseUrl!.Trim()).Host;
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth + "/tokens");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Email!.Trim()}:{_options.ApiKey!.Trim()}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = JsonContent.Create(new
        {
            scopes = new[]
            {
                "GET:" + host + "/documents",
                "GET:" + host + "/requests",
                "POST:" + host + "/requests",
                "PATCH:" + host + "/requests",
            },
            ttl = 3600,
        }, options: Json);

        using var response = await http.SendAsync(request, cancellationToken);
        var token = await ReadTokenAsync(response, cancellationToken);
        // POST /tokens uses the same data envelope as DocuEngine. A root "token", or an empty one, is not a credential.
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(token))
            throw Failed(RliRegistrationFailureCodes.ProviderError, "Openapi did not issue a token.");

        _mintedToken = token;
        return _mintedToken;
    }

    private static async Task<string?> ReadTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("token", out var token))
                return null;
            return token.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RequestId(JsonElement root) =>
        DataObject(root) is { } data && data.TryGetProperty("id", out var id) ? id.GetString() : null;

    private static string? RequestState(JsonElement root) =>
        DataObject(root) is { } data && data.TryGetProperty("state", out var state) ? state.GetString() : null;

    private static string? FirstDownloadUrl(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("downloadUrl", out var url) && url.GetString() is { Length: > 0 } value)
                return value;
        }

        return null;
    }

    private static JsonElement? DataObject(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        return data;
    }

    private static LeaseRegistrationProviderException NotConfigured() =>
        new(RliRegistrationFailureCodes.ProviderError, "Openapi lease registration is not configured.");

    private LeaseRegistrationProviderException Failed(string code, string message)
    {
        logger.LogWarning("Openapi lease registration failed with {FailureCode}", code);
        return new LeaseRegistrationProviderException(code, message);
    }

}
