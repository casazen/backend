using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctorateApiClient"/> over HTTP (ADR-023-R3 to R9), against the real Octorate sandbox or production: there is no mock or
/// fixture adapter (PO decision 2026-11-04).
/// </summary>
/// <remarks>
/// <para><b>Tenant.</b> Every call reads the org from <see cref="IOctorateOrgScope"/> first (no org: it throws before anything else),
/// then that org's token from <see cref="IOctorateTokenService"/> and a call from that org's bucket of <see cref="OctorateRateLimiter"/>.
/// No method takes an org, so a call never uses another org's connection (ADR-023-R4).</para>
/// <para><b>HTTP.</b> The typed client of <c>AddCasazenOctorate</c>: 30-second timeout, https base address from
/// <c>Octorate:ApiBaseUrl</c> (each call carries its <c>/rest/v1/...</c> or <c>/rest/v2/...</c> path), User-Agent <c>CasaZen/{version}</c>,
/// the token as <c>Authorization: Bearer</c> (ADR-023-R5).</para>
/// <para><b>Errors.</b> 404, 409, 401/403, 403 <c>WrongEnviroment</c>, 429 and 5xx become the typed exceptions of
/// <see cref="OctorateException"/> (ADR-023-R6); any other 4xx is <see cref="OctorateRequestRejectedException"/>. A token Octorate
/// refuses (403 <c>ApiLoginInvalid</c>) is refreshed and the call made again once. A 429 is retried after 1, 2 and 4 seconds, then
/// given up (ADR-023-R9). Nothing is ever turned into an empty result: a refusal of the guest data before CasaZen's outbound IPs are
/// whitelisted is an <see cref="OctorateAuthException"/>.</para>
/// <para><b>Logs.</b> The operation, the org, Octorate's status and error type, the time: never a body, a token or a code.</para>
/// </remarks>
public sealed class OctorateApiClient(
    HttpClient httpClient,
    IOctorateTokenService tokenService,
    OctorateRateLimiter rateLimiter,
    IOctorateOrgScope orgScope,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OctorateApiClient> logger) : IOctorateApiClient
{
    /// <summary>Longest accommodation id CasaZen accepts (the id column of the mappings is 64 characters).</summary>
    public const int MaxAccommodationIdLength = 64;

    /// <summary>The pages of accommodations read at most in one listing (pages are numbered from 0).</summary>
    private const int MaxAccommodationPages = 50;

    /// <summary>The <c>type</c> of Octorate's 403 for an access token that is not valid (any more): refresh and retry once.</summary>
    private const string ApiLoginInvalidType = "ApiLoginInvalid";

    /// <summary>The members of a product read by <see cref="GetRoomsAsync"/> (the v3 read answers 400 without <c>fields</c>).</summary>
    private const string RoomFields =
        "id,name,adults,children,bedroomQuantity,bathroomQuantity,bedQuantity,squareMetersSize,derivedRule";

    /// <summary>The members of a connection read by <see cref="GetConnectionsAsync"/>: never its portal username or password.</summary>
    private const string ConnectionFields = "id,channelId,channelName,hotelId,status";

    /// <summary>The JSON of a partial change: camelCase, the members that are not set are left out.</summary>
    private static readonly JsonSerializerOptions PartialUpdateJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<OctorateConfiguration> GetConfigurationAsync(CancellationToken ct)
    {
        using var document = await GetJsonAsync("/rest/v1/api/configuration", "configuration", ct);
        var root = Unwrap(document.RootElement);
        if (root.ValueKind != JsonValueKind.Object)
            throw new OctorateServerException(null);

        return new OctorateConfiguration(
            OctorateHttp.StringOf(root, "environment"),
            IntOf(root, "quotaLimit"),
            IntOf(root, "quotaStatus"),
            OctorateHttp.StringOf(root, "category"));
    }

    public async Task<OctorateProperty[]> GetPropertiesAsync(CancellationToken ct)
    {
        var found = new List<OctorateProperty>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxAccommodationPages; page++)
        {
            JsonDocument document;
            try
            {
                document = await GetJsonAsync($"/rest/v1/accommodation?page={page}", "accommodations", ct);
            }
            catch (OctorateException ex) when (page > 0 && ex is OctorateNotFoundException or OctorateRequestRejectedException)
            {
                // Past the last page.
                break;
            }

            using (document)
            {
                var added = 0;
                foreach (var item in Items(document.RootElement))
                {
                    if (ReadProperty(item) is { } property && seen.Add(property.Id))
                    {
                        found.Add(property);
                        added++;
                    }
                }

                // An empty page, or one that repeats what was read (an answer that ignores the page), is the end.
                if (added == 0)
                    return [.. found];
            }
        }

        logger.LogWarning(
            "Octorate accommodations of org {OrgId}: stopped after {Pages} pages, the list may be longer", orgScope.OrgId, MaxAccommodationPages);
        return [.. found];
    }

    public async Task<OctorateProperty> GetPropertyAsync(string accommodationId, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationId);
        using var document = await GetJsonAsync($"/rest/v1/accommodation/{Uri.EscapeDataString(id)}", "accommodation", ct);
        return ReadProperty(Unwrap(document.RootElement)) ?? throw new OctorateServerException(null);
    }

    public async Task UpdatePropertyAsync(string accommodationId, OctoratePropertyUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.IsEmpty)
            throw new ArgumentException("A change of an accommodation sets at least one member.", nameof(update));

        var id = CheckedAccommodationId(accommodationId);
        var json = JsonSerializer.Serialize(update, PartialUpdateJson);
        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, Address($"/rest/v2/accommodation/{Uri.EscapeDataString(id)}"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "accommodation-update",
            ct);
    }

    public async Task<OctorateRoom[]> GetRoomsAsync(string accommodationId, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationId);
        using var document = await GetJsonAsync(
            $"/rest/v3/roomrates/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(RoomFields)}", "rooms", ct);
        return [.. Items(document.RootElement).Select(ReadRoom).OfType<OctorateRoom>()];
    }

    public async Task<OctorateChannelConnection[]> GetConnectionsAsync(string accommodationId, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationId);
        using var document = await GetJsonAsync(
            $"/rest/v1/connection?accommodation={Uri.EscapeDataString(id)}&fields={Uri.EscapeDataString(ConnectionFields)}", "connections", ct);
        var root = document.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("data", out _)
            ? new[] { root }
            : Items(root);
        return [.. items.Select(ReadConnection).OfType<OctorateChannelConnection>()];
    }

    /// <summary>
    /// One call for the org of the scope: the org first (none: it throws and nothing leaves), then its token and a call from its bucket,
    /// the answer of a success, or the typed error. Retries a 429 after the backoff delays and a refused token once after a refresh.
    /// </summary>
    private async Task<OctorateAnswer> SendAsync(Func<HttpRequestMessage> createRequest, string operation, CancellationToken ct)
    {
        var orgId = orgScope.OrgId;
        var token = await tokenService.GetAccessTokenAsync(orgId, ct);
        var tokenRefreshed = false;
        var rateLimitRetries = 0;

        while (true)
        {
            if (!await rateLimiter.TryAcquireAsync(orgId))
            {
                logger.LogWarning(
                    "Octorate {Operation} for org {OrgId} not sent: the org used its {Limit} calls a minute",
                    operation, orgId, OctorateRateLimiter.RequestsPerMinute);
                throw new OctorateRateLimitException(null);
            }

            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var started = timeProvider.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Octorate {Operation} for org {OrgId}: no answer ({Error})", operation, orgId, ex.GetType().Name);
                throw new OctorateServerException(null, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Octorate {Operation} for org {OrgId}: no answer within {Timeout}", operation, orgId, OctorateHttp.Timeout);
                throw new OctorateServerException(null, ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await OctorateHttp.ReadBodyAsync(response, ct);
                var elapsedMs = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
                if (response.IsSuccessStatusCode)
                {
                    logger.LogDebug(
                        "Octorate {Operation} for org {OrgId}: HTTP {StatusCode} in {ElapsedMs} ms", operation, orgId, status, elapsedMs);
                    return new OctorateAnswer(status, body);
                }

                var errorType = OctorateHttp.ReadError(body).Type;
                logger.LogWarning(
                    "Octorate {Operation} for org {OrgId}: HTTP {StatusCode} ({ErrorType}) in {ElapsedMs} ms",
                    operation, orgId, status, errorType ?? "no type", elapsedMs);

                if (status == 429 && rateLimitRetries < OctorateRateLimiter.BackoffDelays.Count)
                {
                    await Task.Delay(OctorateRateLimiter.BackoffDelays[rateLimitRetries++], timeProvider, ct);
                    continue;
                }

                if (status is 401 or 403 && !tokenRefreshed && string.Equals(errorType, ApiLoginInvalidType, StringComparison.OrdinalIgnoreCase))
                {
                    token = await tokenService.RefreshRejectedTokenAsync(orgId, token, ct);
                    tokenRefreshed = true;
                    continue;
                }

                throw ErrorOf(status, errorType, body);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, string operation, CancellationToken ct)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Address(pathAndQuery)), operation, ct);
        try
        {
            return JsonDocument.Parse(answer.Body);
        }
        catch (JsonException ex)
        {
            throw new OctorateServerException(answer.StatusCode, ex);
        }
    }

    private Uri Address(string pathAndQuery) => OctorateHttp.Address(OctorateHttp.ApiBaseUrl(configuration), pathAndQuery);

    /// <summary>The typed error of an Octorate answer that is not a success (and not retried).</summary>
    internal static OctorateException ErrorOf(int status, string? errorType, string body) => status switch
    {
        404 => new OctorateNotFoundException(),
        409 => new OctorateConflictException(),
        429 => new OctorateRateLimitException(status),
        401 or 403 when IsWrongEnvironment(errorType, body) => new OctorateWrongEnvironmentException(status),
        401 or 403 => OctorateAuthException.Refused(status),
        >= 500 => new OctorateServerException(status),
        _ => new OctorateRequestRejectedException(status),
    };

    private static bool IsWrongEnvironment(string? errorType, string body) =>
        string.Equals(errorType, OctorateWrongEnvironmentException.OctorateErrorType, StringComparison.OrdinalIgnoreCase)
        || body.Contains(OctorateWrongEnvironmentException.OctorateErrorType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The accommodation id of a call, trimmed: never empty, never longer than <see cref="MaxAccommodationIdLength"/>.</summary>
    internal static string CheckedAccommodationId(string accommodationId)
    {
        var id = accommodationId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxAccommodationIdLength)
            throw new ArgumentException($"An accommodation id has 1 to {MaxAccommodationIdLength} characters.", nameof(accommodationId));

        return id;
    }

    /// <summary>An accommodation of an answer, or <c>null</c> when the item has no usable id.</summary>
    internal static OctorateProperty? ReadProperty(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateHttp.StringOf(item, "id");
        if (id is null || id.Length > MaxAccommodationIdLength)
            return null;

        item.TryGetProperty("location", out var location);
        var latitude = DecimalOf(item, "latitude") ?? DecimalOf(location, "latitude");
        var longitude = DecimalOf(item, "longitude") ?? DecimalOf(location, "longitude");
        if (latitude is 0m && longitude is 0m)
            (latitude, longitude) = (null, null);

        item.TryGetProperty("networkInfo", out var network);
        return new OctorateProperty
        {
            Id = id,
            Name = OctorateHttp.StringOf(item, "name"),
            Address = OctorateHttp.StringOf(item, "address") ?? OctorateHttp.StringOf(location, "address"),
            City = OctorateHttp.StringOf(item, "city") ?? OctorateHttp.StringOf(location, "city"),
            PostalCode = OctorateHttp.StringOf(item, "zipCode") ?? OctorateHttp.StringOf(item, "zip") ?? OctorateHttp.StringOf(location, "zipCode"),
            Country = OctorateHttp.StringOf(item, "country") ?? OctorateHttp.StringOf(location, "countryAlpha2"),
            Latitude = latitude,
            Longitude = longitude,
            Category = OctorateHttp.StringOf(item, "propertyCategory"),
            NetworkId = OctorateHttp.StringOf(network, "accessNetwork"),
            SandboxProperty = BoolOf(item, "sandboxProperty"),
        };
    }

    /// <summary>A product of an answer of <c>roomrates</c>, or <c>null</c> when the item has no usable id.</summary>
    internal static OctorateRoom? ReadRoom(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateHttp.StringOf(item, "id");
        if (id is null || id.Length > MaxAccommodationIdLength)
            return null;

        item.TryGetProperty("derivedRule", out var derivedRule);
        var parent = OctorateHttp.StringOf(derivedRule, "parent");
        return new OctorateRoom
        {
            Id = id,
            Name = OctorateHttp.StringOf(item, "name"),
            Adults = IntOf(item, "adults"),
            Children = IntOf(item, "children"),
            Bedrooms = IntOf(item, "bedroomQuantity"),
            Bathrooms = IntOf(item, "bathroomQuantity"),
            Beds = IntOf(item, "bedQuantity"),
            AreaSqm = IntOf(item, "squareMetersSize"),
            DerivedFromId = parent is null or "0" || parent == id ? null : parent,
        };
    }

    /// <summary>A channel connection of an answer of <c>connection</c>; <c>null</c> for an item that is not an object.</summary>
    internal static OctorateChannelConnection? ReadConnection(JsonElement item) =>
        item.ValueKind != JsonValueKind.Object
            ? null
            : new OctorateChannelConnection(
                OctorateHttp.StringOf(item, "id"),
                OctorateHttp.StringOf(item, "channelId"),
                OctorateHttp.StringOf(item, "channelName"),
                OctorateHttp.StringOf(item, "hotelId"),
                OctorateHttp.StringOf(item, "status"));

    /// <summary>The items of a list answer: a bare array (v1) or the <c>data</c> array of a <c>{data, page}</c> answer (v2, v3).</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();

        return [];
    }

    /// <summary>The object of a single answer: the <c>data</c> object of a <c>{data: {...}}</c> answer, or the answer itself.</summary>
    internal static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    internal static int? IntOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && OctorateHttp.TryReadLong(value, out var number)
        && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : null;

    internal static decimal? DecimalOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    internal static bool? BoolOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>A successful answer: Octorate's status and its text.</summary>
    private sealed record OctorateAnswer(int StatusCode, string Body);
}
