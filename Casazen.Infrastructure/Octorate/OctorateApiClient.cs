using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
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
/// No method takes an org, so a call never uses another org's connection (ADR-023-R4). The partner-level calls (<see cref="GetPortalsAsync"/>)
/// are the one exception to "an org first": they carry no property data and act for the application, so they use the partner token and the
/// bucket <see cref="OctorateRateLimiter.PlatformKey"/>, and need no org.</para>
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

    /// <summary>The most products one call of the calendar read names (Octorate answers at most 20 products a call).</summary>
    internal const int MaxCalendarProducts = 20;

    /// <summary>The <c>type</c> of Octorate's 403 for an access token that is not valid (any more): refresh and retry once.</summary>
    private const string ApiLoginInvalidType = "ApiLoginInvalid";

    /// <summary>A number at or above this is a count of milliseconds since 1970 (as seconds it would be the year 5138).</summary>
    private const long EpochMillisecondsThreshold = 100_000_000_000;

    /// <summary>The members of a product read by <see cref="GetRoomsAsync"/> (the v3 read answers 400 without <c>fields</c>).</summary>
    private const string RoomFields =
        "id,name,adults,children,bedroomQuantity,bathroomQuantity,bedQuantity,squareMetersSize,derivedRule";

    /// <summary>The members of a connection read by <see cref="GetConnectionsAsync"/>: never its portal username or password.</summary>
    private const string ConnectionFields = "id,channelId,channelName,hotelId,status";

    /// <summary>The member of an accommodation read by <see cref="GetPoliceAccountAsync"/>: the police account, as Octorate documents it (Q-002).</summary>
    private const string PoliceAccountFields = "policeAccount";

    /// <summary>The members of a connection read by <see cref="GetOrgConnectionsAsync"/>: ids and state, never its portal username or password.</summary>
    private const string OrgConnectionFields = "id,channelId,hotelId,status,userLogged,accommodationId";

    /// <summary>The most digits of a connection id (Octorate's ids are 64-bit numbers).</summary>
    private const int MaxConnectionIdDigits = 19;

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

    public async Task<OctoratePoliceAccount> GetPoliceAccountAsync(string accommodationId, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationId);
        using var document = await GetJsonAsync(
            $"/rest/v1/accommodation/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(PoliceAccountFields)}", "police-account", ct);

        // An answer that is not an accommodation is one CasaZen cannot read; an accommodation without a police account is not an error.
        var accommodation = Unwrap(document.RootElement);
        return accommodation.ValueKind == JsonValueKind.Object ? ReadPoliceAccount(accommodation) : throw new OctorateServerException(null);
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

    public async Task<OctorateReservationPage> GetReservationsAsync(string accommodationId, OctorateReservationQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var id = CheckedAccommodationId(accommodationId);
        if (query.Page is < 0 or > OctorateReservationQuery.MaxPage)
            throw new ArgumentOutOfRangeException(nameof(query), query.Page, $"The page is 0 to {OctorateReservationQuery.MaxPage}.");
        if (query.PageSize is < 1 or > OctorateReservationQuery.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, $"A page has 1 to {OctorateReservationQuery.MaxPageSize} reservations.");
        if (query.To < query.From)
            throw new ArgumentException("The last day of the list is not before its first day.", nameof(query));

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/rest/v1/reservation/{Uri.EscapeDataString(id)}?type={ReservationListTypeName(query.Type)}&startDate={query.From:yyyy-MM-dd}&endDate={query.To:yyyy-MM-dd}&size={query.PageSize}&page={query.Page}");
        using var document = await GetJsonAsync(path, "reservations", ct);

        // An answer that is not a list is an answer CasaZen cannot read, not a list with nothing in it: the caller must not read "no
        // reservation" into it and mark a listing as read. An item that cannot be read is left out of the page but counted in it.
        return OctorateReservationReader.ReadPage(document.RootElement) ?? throw new OctorateServerException(null);
    }

    public async Task<OctorateReservation> GetReservationAsync(string accommodationId, string reservationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        using var document = await GetJsonAsync(
            $"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}", "reservation", ct);
        return OctorateReservationReader.Read(Unwrap(document.RootElement), reservation) ?? throw new OctorateServerException(null);
    }

    public async Task<OctorateCalendarDay[]> GetCalendarAsync(
        string accommodationId,
        IReadOnlyCollection<string> productIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var accommodation = CheckedAccommodationId(accommodationId);
        var products = productIds.Select(p => CheckedId(p, nameof(productIds))).Distinct(StringComparer.Ordinal).ToList();
        if (products.Count == 0)
            throw new ArgumentException("A calendar read names at least one product.", nameof(productIds));
        if (to < from)
            throw new ArgumentException("The last day of the calendar is before its first day.", nameof(to));

        var days = new List<OctorateCalendarDay>();
        foreach (var chunk in products.Chunk(MaxCalendarProducts))
        {
            ct.ThrowIfCancellationRequested();

            // `dateTo` is included, as Octorate documents it.
            var path = string.Create(
                CultureInfo.InvariantCulture,
                $"/rest/v1/calendar/{Uri.EscapeDataString(accommodation)}?dateFrom={from:yyyy-MM-dd}&dateTo={to:yyyy-MM-dd}&products={Uri.EscapeDataString(string.Join(',', chunk))}");
            using var document = await GetJsonAsync(path, "calendar", ct);

            var read = OctorateCalendarReader.Read(document.RootElement, chunk);
            if (read.Count == 0)
            {
                // A calendar with no day for a range that is not empty is an answer that was not understood: never an empty calendar.
                logger.LogWarning(
                    "Octorate calendar of org {OrgId}: no day could be read from the answer for {Products} products", orgScope.OrgId, chunk.Length);
                throw new OctorateServerException(null);
            }

            days.AddRange(read);
        }

        return [.. days];
    }

    public async Task CancelReservationAsync(string accommodationId, string reservationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Delete,
                Address($"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}")),
            "reservation-cancel",
            ct);
    }

    public async Task SetCheckInStatusAsync(string accommodationId, string reservationId, OctorateCheckInStatus status, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        var name = CheckInStatusName(status);
        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Put,
                Address($"/rest/v1/checkin/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}?status={name}")),
            "checkin-status",
            ct);
    }

    public async Task<OctoratePortal[]> GetPortalsAsync(CancellationToken ct)
    {
        // A partner-level call: no org, the partner token, the bucket "platform". The answer is a bare array (v1) or {data: [...]}.
        using var document = await GetPlatformJsonAsync("/rest/v1/meta/portals", "portals", ct);
        return [.. Items(document.RootElement).Select(ReadPortal).OfType<OctoratePortal>()];
    }

    public async Task<OctorateConnectionSummary[]> GetOrgConnectionsAsync(CancellationToken ct)
    {
        // The connections of every accommodation of the org's token in one call; only the ids and the state are asked for, so the portal
        // username and password of a connection never reach CasaZen.
        using var document = await GetJsonAsync(
            $"/rest/v1/connection?fields={Uri.EscapeDataString(OrgConnectionFields)}", "org-connections", ct);
        var root = document.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("data", out _)
            ? new[] { root }
            : Items(root);
        return [.. items.Select(ReadConnectionSummary).OfType<OctorateConnectionSummary>()];
    }

    public Task<OctorateConnectionActionResult> ImportRoomsAsync(string accommodationId, string connectionId, CancellationToken ct) =>
        RunD6OperationAsync(OctorateD6Operation.ImportRooms, accommodationId, connectionId, ct);

    public Task<OctorateConnectionActionResult> ImportReservationsAsync(string accommodationId, string connectionId, CancellationToken ct) =>
        RunD6OperationAsync(OctorateD6Operation.ImportReservations, accommodationId, connectionId, ct);

    public Task<OctorateConnectionActionResult> ImportCalendarAsync(string accommodationId, string connectionId, CancellationToken ct) =>
        RunD6OperationAsync(OctorateD6Operation.ImportCalendar, accommodationId, connectionId, ct);

    public Task<OctorateConnectionActionResult> MarkConnectedAsync(string accommodationId, string connectionId, CancellationToken ct) =>
        RunD6OperationAsync(OctorateD6Operation.MarkConnected, accommodationId, connectionId, ct);

    /// <summary>
    /// One operation of the D6 sequence on a connection (<c>POST /rest/v1/connection/{action}/{acc}/{connection}</c>). The four typed methods
    /// above are the only callers and <see cref="OctorateD6Operation"/> has no member that creates or publishes a listing, so no path of the
    /// import reaches such an operation. A 200 whose body is not a JSON list or object, or that names another action than the one asked, is
    /// <see cref="OctorateServerException"/>: the result is unknown and is never read as a success.
    /// </summary>
    private async Task<OctorateConnectionActionResult> RunD6OperationAsync(
        OctorateD6Operation operation,
        string accommodationId,
        string connectionId,
        CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var connection = CheckedConnectionId(connectionId);
        var name = D6OperationName(operation);
        var answer = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                Address($"/rest/v1/connection/{name}/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(connection)}")),
            $"connection-{name}",
            ct);
        return ReadD6Result(operation, connection, answer);
    }

    /// <summary>
    /// One call for the org of the scope: the org first (none: it throws and nothing leaves), then its token and a call from its bucket,
    /// the answer of a success, or the typed error. Retries a 429 after the backoff delays and a refused token once after a refresh.
    /// </summary>
    private async Task<OctorateAnswer> SendAsync(Func<HttpRequestMessage> createRequest, string operation, CancellationToken ct)
    {
        var orgId = orgScope.OrgId;
        var caller = new Caller(
            $"org {orgId}",
            token => tokenService.GetAccessTokenAsync(orgId, token),
            (rejected, token) => tokenService.RefreshRejectedTokenAsync(orgId, rejected, token),
            () => rateLimiter.TryAcquireAsync(orgId));
        return await SendAsync(createRequest, operation, caller, ct);
    }

    /// <summary>
    /// One partner-level call (the application's own, no org): the partner token, a refused partner token renewed once, and a call from the
    /// bucket <see cref="OctorateRateLimiter.PlatformKey"/>. Otherwise the same as <see cref="SendAsync(Func{HttpRequestMessage}, string, CancellationToken)"/>.
    /// </summary>
    private Task<OctorateAnswer> SendAsPlatformAsync(Func<HttpRequestMessage> createRequest, string operation, CancellationToken ct)
    {
        var caller = new Caller(
            OctorateRateLimiter.PlatformKey,
            tokenService.GetPartnerAccessTokenAsync,
            tokenService.RefreshRejectedPartnerTokenAsync,
            rateLimiter.TryAcquirePlatformAsync);
        return SendAsync(createRequest, operation, caller, ct);
    }

    /// <summary>The call itself, for whoever <paramref name="caller"/> is: its token, its bucket, its refresh of a refused token.</summary>
    private async Task<OctorateAnswer> SendAsync(Func<HttpRequestMessage> createRequest, string operation, Caller caller, CancellationToken ct)
    {
        var token = await caller.GetTokenAsync(ct);
        var tokenRefreshed = false;
        var rateLimitRetries = 0;

        while (true)
        {
            if (!await caller.TryAcquireAsync())
            {
                logger.LogWarning(
                    "Octorate {Operation} for {Caller} not sent: it used its {Limit} calls a minute",
                    operation, caller.Label, OctorateRateLimiter.RequestsPerMinute);
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
                logger.LogWarning("Octorate {Operation} for {Caller}: no answer ({Error})", operation, caller.Label, ex.GetType().Name);
                throw new OctorateServerException(null, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Octorate {Operation} for {Caller}: no answer within {Timeout}", operation, caller.Label, OctorateHttp.Timeout);
                throw new OctorateServerException(null, ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await OctorateHttp.ReadBodyAsync(response, ct);
                var elapsedMs = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
                // 304: a PATCH or a PUT whose content is identical to what Octorate holds answers "not modified" (S25, D13): it is done.
                if (response.IsSuccessStatusCode || status == 304)
                {
                    logger.LogDebug(
                        "Octorate {Operation} for {Caller}: HTTP {StatusCode} in {ElapsedMs} ms", operation, caller.Label, status, elapsedMs);
                    return new OctorateAnswer(status, body);
                }

                var errorType = OctorateHttp.ReadError(body).Type;
                logger.LogWarning(
                    "Octorate {Operation} for {Caller}: HTTP {StatusCode} ({ErrorType}) in {ElapsedMs} ms",
                    operation, caller.Label, status, errorType ?? "no type", elapsedMs);

                if (status == 429 && rateLimitRetries < OctorateRateLimiter.BackoffDelays.Count)
                {
                    await Task.Delay(OctorateRateLimiter.BackoffDelays[rateLimitRetries++], timeProvider, ct);
                    continue;
                }

                if (status is 401 or 403 && !tokenRefreshed && string.Equals(errorType, ApiLoginInvalidType, StringComparison.OrdinalIgnoreCase))
                {
                    token = await caller.RefreshRejectedAsync(token, ct);
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
        return ParseJson(answer);
    }

    /// <summary>A read for the application itself (the partner-level calls): see <see cref="SendAsPlatformAsync"/>.</summary>
    private async Task<JsonDocument> GetPlatformJsonAsync(string pathAndQuery, string operation, CancellationToken ct)
    {
        var answer = await SendAsPlatformAsync(() => new HttpRequestMessage(HttpMethod.Get, Address(pathAndQuery)), operation, ct);
        return ParseJson(answer);
    }

    private static JsonDocument ParseJson(OctorateAnswer answer)
    {
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

    /// <summary>The id of a reservation or of a product, trimmed: never empty, never longer than <see cref="MaxAccommodationIdLength"/>.</summary>
    internal static string CheckedId(string value, string parameterName)
    {
        var id = value?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxAccommodationIdLength)
            throw new ArgumentException($"An id has 1 to {MaxAccommodationIdLength} characters.", parameterName);

        return id;
    }

    /// <summary>
    /// The id of a connection, trimmed: a number of 1 to <see cref="MaxConnectionIdDigits"/> digits, as the specification declares it
    /// (<c>integer</c>, <c>int64</c>). Anything else is refused before a call leaves.
    /// </summary>
    internal static string CheckedConnectionId(string connectionId)
    {
        var id = connectionId?.Trim();
        if (!IsNumericId(id))
            throw new ArgumentException($"A connection id is a number of 1 to {MaxConnectionIdDigits} digits.", nameof(connectionId));

        return id!;
    }

    private static bool IsNumericId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxConnectionIdDigits && id.All(char.IsAsciiDigit);

    /// <summary>The <c>action</c> of the connection operation, as Octorate spells it.</summary>
    internal static string D6OperationName(OctorateD6Operation operation) => operation switch
    {
        OctorateD6Operation.ImportRooms => "IMPORT_ROOMS",
        OctorateD6Operation.ImportReservations => "IMPORT_RESERVATIONS",
        OctorateD6Operation.ImportCalendar => "IMPORT_CALENDAR",
        OctorateD6Operation.MarkConnected => "MARK_CONNECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an operation of the D6 sequence."),
    };

    /// <summary>
    /// The answer of an operation of the D6 sequence: the list of mappings it touched, bare or as the <c>data</c> array of the wrapper that
    /// names the action performed (the example of the specification), and nothing else. Anything else is an answer CasaZen cannot trust and is
    /// never read as a success: a body that is not a list or an object, <c>{}</c> or an error that came with HTTP 200, a wrapper without a
    /// <c>data</c> array, one without <c>additionalInfo.actionPerformed</c> or one that names another action: <see cref="OctorateServerException"/>.
    /// </summary>
    internal static OctorateConnectionActionResult ReadD6Result(OctorateD6Operation operation, string connectionId, OctorateAnswer answer)
    {
        using var document = ParseJson(answer);
        var root = document.RootElement;
        var list = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when NamesTheAction(root, operation) && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            _ => throw new OctorateServerException(answer.StatusCode),
        };

        return new OctorateConnectionActionResult(
            operation,
            connectionId,
            [.. list.EnumerateArray().Select(ReadImportedMapping).OfType<OctorateImportedMapping>()]);
    }

    /// <summary>True when the wrapper of an answer says, in <c>additionalInfo.actionPerformed</c>, that it is the answer of this operation.</summary>
    private static bool NamesTheAction(JsonElement wrapper, OctorateD6Operation operation) =>
        wrapper.TryGetProperty("additionalInfo", out var info)
        && info.ValueKind == JsonValueKind.Object
        && OctorateHttp.StringOf(info, "actionPerformed") is { } performed
        && string.Equals(performed, D6OperationName(operation), StringComparison.OrdinalIgnoreCase);

    /// <summary>A mapping of an answer of an operation of the D6 sequence, or <c>null</c> when the item lacks the product or the external id.</summary>
    internal static OctorateImportedMapping? ReadImportedMapping(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var product = OctorateHttp.StringOf(item, "productId");
        var external = OctorateHttp.StringOf(item, "externalId");
        return product is null || external is null || product.Length > MaxAccommodationIdLength || external.Length > MaxAccommodationIdLength
            ? null
            : new OctorateImportedMapping(product, external);
    }

    /// <summary>
    /// A connection of an answer of <c>connection</c> without an accommodation filter, or <c>null</c> when the item has no numeric id, no
    /// channel or no accommodation (the id is also read from <c>accommodation.id</c> when the answer nests it).
    /// </summary>
    internal static OctorateConnectionSummary? ReadConnectionSummary(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        item.TryGetProperty("accommodation", out var accommodation);
        var id = OctorateHttp.StringOf(item, "id");
        var channel = OctorateHttp.StringOf(item, "channelId");
        var accommodationId = OctorateHttp.StringOf(item, "accommodationId") ?? OctorateHttp.StringOf(accommodation, "id");
        if (!IsNumericId(id)
            || channel is null
            || channel.Length > MaxAccommodationIdLength
            || accommodationId is null
            || accommodationId.Length > MaxAccommodationIdLength)
        {
            return null;
        }

        var external = OctorateHttp.StringOf(item, "hotelId");
        return new OctorateConnectionSummary(
            id!,
            channel,
            accommodationId,
            external is { Length: <= MaxAccommodationIdLength } ? external : null,
            OctorateHttp.StringOf(item, "status"),
            BoolOf(item, "userLogged"));
    }

    /// <summary>The <c>type</c> of the list of reservations, as Octorate spells it.</summary>
    internal static string ReservationListTypeName(OctorateReservationListType type) => type switch
    {
        OctorateReservationListType.ModifiedBetween => "MODIFIED_BETWEEN",
        OctorateReservationListType.CreateTime => "CREATETIME",
        OctorateReservationListType.Cancellation => "CANCELLATION",
        OctorateReservationListType.Stay => "STAY",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of list of reservations."),
    };

    /// <summary>The <c>status</c> of the check-in status operation, as Octorate spells it.</summary>
    internal static string CheckInStatusName(OctorateCheckInStatus status) => status switch
    {
        OctorateCheckInStatus.CheckInEnd => "CHECKIN_END",
        OctorateCheckInStatus.CheckOutEnd => "CHECKOUT_END",
        OctorateCheckInStatus.NoShow => "NO_SHOW",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a check-in status."),
    };

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

    /// <summary>
    /// The police account the host connected on the Octorate panel, from an accommodation of an answer. Not every answer has it (an
    /// accommodation created through the API has none): a member that is missing stays <c>null</c>, and nothing is assumed about it. The code
    /// and the web service key of the object are not read.
    /// </summary>
    internal static OctoratePoliceAccount ReadPoliceAccount(JsonElement accommodation)
    {
        JsonElement police = default;
        if (accommodation.ValueKind == JsonValueKind.Object)
            accommodation.TryGetProperty("policeAccount", out police);

        return new OctoratePoliceAccount
        {
            Valid = BoolOf(police, "valid"),
            Autorun = BoolOf(police, "autorun"),
            LastCron = DateTimeOf(police, "lastCron"),
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

    /// <summary>The names under which a portal's identifier may come, most specific first; a number is read as its text.</summary>
    private static readonly string[] PortalCodeMembers = ["portalCode", "channelId", "portalId", "id", "code"];

    private static readonly string[] PortalNameMembers = ["name", "portalName", "title"];

    private static readonly string[] PortalCategoryMembers = ["portalCategory", "category"];

    /// <summary>
    /// A portal of an answer of <c>meta/portals</c>, or <c>null</c> when the item has no usable identifier (none, or longer than
    /// <see cref="OctoratePortal.CodeMaxLength"/>). The flags are read as <c>true</c> only when Octorate says so (a JSON boolean, a
    /// <c>"true"</c> text or a non-zero number): a flag it does not send is <c>false</c>. The site and the logo are kept only when they are
    /// absolute https addresses.
    /// </summary>
    internal static OctoratePortal? ReadPortal(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var code = FirstStringOf(item, PortalCodeMembers);
        if (code is null || code.Length > OctoratePortal.CodeMaxLength)
            return null;

        return new OctoratePortal
        {
            PortalCode = code,
            Name = FirstStringOf(item, PortalNameMembers) ?? code,
            PortalCategory = FirstStringOf(item, PortalCategoryMembers),
            Creatable = FlagOf(item, "creatable") ?? false,
            Enabled = FlagOf(item, "enabled") ?? false,
            Preferred = FlagOf(item, "preferred") ?? false,
            Website = HttpsUrlOf(item, "website"),
            Logo = HttpsUrlOf(item, "logo") ?? HttpsUrlOf(item, "logoUrl"),
            RequiresHotelId = FlagOf(item, "requiresHotelId") ?? false,
            RequiresPassword = FlagOf(item, "requiresPassword") ?? false,
            RequiresTokenAuthentication = FlagOf(item, "requiresTokenAuthentication") ?? false,
            FeatureContent = FlagOf(item, "featureContent") ?? false,
            OctorateManageable = FlagOf(item, "octorateManageable") ?? false,
            CommissionIncludedInPayments = FlagOf(item, "commissionIncludedInPayments") ?? false,
            CommissionsInvoiced = FlagOf(item, "commissionsInvoiced"),
        };
    }

    /// <summary>The first of <paramref name="names"/> that <paramref name="element"/> has with a text (or a number), trimmed.</summary>
    private static string? FirstStringOf(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            if (OctorateHttp.StringOf(element, name) is { } text)
                return text;
        }

        return null;
    }

    /// <summary>A flag Octorate sends as a JSON boolean, as <c>"true"</c>/<c>"false"</c> text or as a number; <c>null</c> when absent or unclear.</summary>
    private static bool? FlagOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>An address Octorate gives, only when it is an absolute https one of at most 500 characters (never <c>javascript:</c>, <c>data:</c> or a relative path).</summary>
    private static string? HttpsUrlOf(JsonElement element, string name)
    {
        var text = OctorateHttp.StringOf(element, name);
        return text is { Length: <= 500 }
               && Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
            ? text
            : null;
    }

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

    /// <summary>
    /// A moment of an answer, in UTC: an ISO text (a text without a zone is taken as UTC) or a number of epoch milliseconds (of seconds,
    /// when it is too small to be milliseconds). <c>null</c> when the member is missing or is none of these: never a guess.
    /// </summary>
    internal static DateTime? DateTimeOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch) && epoch > 0)
        {
            try
            {
                return (epoch >= EpochMillisecondsThreshold
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch)).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A successful answer: Octorate's status and its text.</summary>
    internal sealed record OctorateAnswer(int StatusCode, string Body);

    /// <summary>
    /// Who a call is made for: an org (its token, its bucket) or the platform (the partner token, the bucket <c>platform</c>). The label is
    /// what the log says; it is an id or a fixed word, never a name.
    /// </summary>
    private sealed record Caller(
        string Label,
        Func<CancellationToken, Task<string>> GetTokenAsync,
        Func<string, CancellationToken, Task<string>> RefreshRejectedAsync,
        Func<Task<bool>> TryAcquireAsync);
}
