using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Octorate;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
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
public sealed partial class OctorateApiClient(
    HttpClient httpClient,
    IOctorateTokenService tokenService,
    OctorateRateLimiter rateLimiter,
    IOctorateOrgScope orgScope,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OctorateApiClient> logger,
    OctorateMasterBoundary? master = null) : IOctorateApiClient
{
    public const int MaxAccommodationIdLength = 64;

    public const int ArrivalTimeMaxLength = 10;

    private const int MaxAccommodationPages = 50;

    internal const int MaxCalendarProducts = 20;

    /// <summary>The <c>type</c> of Octorate's 403 for an access token that is not valid (any more): refresh and retry once.</summary>
    private const string ApiLoginInvalidType = "ApiLoginInvalid";

    /// <summary>A number at or above this is a count of milliseconds since 1970 (as seconds it would be the year 5138).</summary>
    private const long EpochMillisecondsThreshold = 100_000_000_000;

    private const string RoomFields =
        "id,name,adults,children,bedroomQuantity,bathroomQuantity,bedQuantity,squareMetersSize,derivedRule";

    private const string ConnectionFields = "id,channelId,channelName,hotelId,status";

    private const string PoliceAccountFields = "policeAccount";

    private const string OrgConnectionFields = "id,channelId,hotelId,status,userLogged,loginAt,accommodationId";

    private const int MaxConnectionIdDigits = 19;

    internal const int NotAvailableStatus = 406;

    internal const int FindLookbackDays = 30;

    internal const int MaxFindPages = 10;

    private const int MaxReasonLength = 200;

    internal const int MaxChatMessagePages = 200;

    private const string ChatInstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

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
        if (master is not null && await master.AssignedAsync(orgScope.OrgId, ct) is { } assigned)
        {
            var properties = new List<OctorateProperty>();
            foreach (var id in assigned)
                properties.Add(await GetPropertyAsync(id, ct));
            return [.. properties];
        }

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

        var accommodation = Unwrap(document.RootElement);
        return accommodation.ValueKind == JsonValueKind.Object ? ReadPoliceAccount(accommodation) : throw new OctorateServerException(null);
    }

    public async Task<OctoratePoliceAccountStatus> GetPoliceAccountStatusAsync(string octorateAccommodationId, CancellationToken ct)
    {
        try
        {
            var account = await GetPoliceAccountAsync(octorateAccommodationId, ct);

            return account.Valid is null
                ? OctoratePoliceAccountStatus.NotReadable
                : new OctoratePoliceAccountStatus(true, account.Valid, account.Autorun, account.LastCron);
        }
        catch (OctorateException ex) when (ex is not (OctorateRateLimitException or OctorateServerException))
        {
            // A refusal, an unknown accommodation or an org with no connection mean «cannot tell», never «not valid». What is worth a
            // retry (429, 5xx, no answer, an answer that cannot be read) keeps its typed error. Codes only: never a body.
            logger.LogInformation(
                "Police account of an accommodation of org {OrgId} is not readable ({Error})", orgScope.OrgId, ex.Code);
            return OctoratePoliceAccountStatus.NotReadable;
        }
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

    public async Task<OctorateGuestContactInfo> GetGuestContactInfoAsync(string accommodationId, string guestId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var guest = CheckedId(guestId, nameof(guestId));

        OctorateAnswer answer;
        try
        {
            answer = await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Post,
                    Address($"/rest/v1/guests/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(guest)}/contactInfo"))
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                },
                "guest-contact-info",
                ct);
        }
        catch (OctorateAuthException ex) when (!ex.IsNotConnected)
        {
            // 401 or 403 that is not a stale token (that one was refreshed and retried by SendAsync): the static IP addresses of CasaZen are not
            // registered with Octorate yet, or the application has no contactInfo permission. Never turned into a guest with no contact.
            logger.LogWarning(
                "Octorate guest-contact-info for org {OrgId}: refused (HTTP {StatusCode}); the sensitive data is not released to this application",
                orgScope.OrgId,
                ex.OctorateStatusCode);
            throw new OctorateSensitiveDataNotAllowedException(ex.OctorateStatusCode);
        }

        using var document = ParseJson(answer);
        var root = Unwrap(document.RootElement);

        if (root.ValueKind != JsonValueKind.Object)
            return new OctorateGuestContactInfo(null, null, Recognized: false);

        return ReadGuestContactInfo(root);
    }

    private static readonly string[] GuestEmailNames = ["email", "emailAddress", "mail"];

    private static readonly string[] GuestPhoneNames = ["phone", "phoneNumber", "mobile", "telephone"];

    internal static OctorateGuestContactInfo ReadGuestContactInfo(JsonElement root)
    {
        var recognized = HasContactMember(root, GuestEmailNames) || HasContactMember(root, GuestPhoneNames);
        return new OctorateGuestContactInfo(OctorateJson.Text(root, GuestEmailNames), OctorateJson.Text(root, GuestPhoneNames), recognized);
    }

    private static bool HasContactMember(JsonElement root, string[] names) =>
        OctorateJson.Find(root, names) is { } member && member.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null;

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

            var path = string.Create(
                CultureInfo.InvariantCulture,
                $"/rest/v1/calendar/{Uri.EscapeDataString(accommodation)}?dateFrom={from:yyyy-MM-dd}&dateTo={to:yyyy-MM-dd}&products={Uri.EscapeDataString(string.Join(',', chunk))}");
            using var document = await GetJsonAsync(path, "calendar", ct);

            var read = OctorateCalendarReader.Read(document.RootElement, chunk);
            if (read.Count == 0)
            {
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

    public async Task CancelReservationAsync(OctorateBookingTarget target, string reservationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var accommodation = CheckedAccommodationId(target.AccommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        var path = $"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}";

        // What Octorate holds now. A reservation that is cancelled already is done: the retry of a cancellation whose answer was lost sends
        // nothing (Octorate has no idempotency key, D13). Otherwise this document is what the PUT sends back, with only its status changed.
        string body;
        using (var document = await GetJsonAsync(path, "reservation", ct))
        {
            var current = Unwrap(document.RootElement);
            var held = OctorateReservationReader.Read(current, reservation) ?? throw new OctorateServerException(null);
            if (held.Status == OctorateBookingStatus.Cancelled)
                return;

            body = OctorateReservationRequests.CancelBody(current, target);
        }

        var accepted = false;
        try
        {
            await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Put, Address(path))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                },
                "reservation-cancel",
                ct);
            accepted = true;
        }
        catch (OctorateRequestRejectedException refused)
        {
            logger.LogInformation(
                "Octorate reservation-cancel for org {OrgId}: the status change was refused (HTTP {StatusCode}), the reservation is deleted instead",
                orgScope.OrgId,
                refused.OctorateStatusCode);
        }

        if (accepted)
        {
            // A 2xx is not the proof: Octorate leaves out, without saying so, what it does not take (the sandbox ignores fields it does not write).
            // The reservation is cancelled when Octorate reads it as cancelled; otherwise the status was ignored and the way the sandbox proved
            // is next. A read that fails is its typed error (nothing is deleted on a guess, the caller sees no confirmation).
            if (await StatusOfReservationAsync(path, reservation, ct) == OctorateBookingStatus.Cancelled)
                return;

            logger.LogWarning(
                "Octorate reservation-cancel for org {OrgId}: the status change was accepted but the reservation is not cancelled, it is deleted instead",
                orgScope.OrgId);
        }

        await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Address(path)), "reservation-delete", ct);
    }

    private async Task<OctorateBookingStatus?> StatusOfReservationAsync(string path, string reservationId, CancellationToken ct)
    {
        using var document = await GetJsonAsync(path, "reservation", ct);
        var held = OctorateReservationReader.Read(Unwrap(document.RootElement), reservationId) ?? throw new OctorateServerException(null);
        return held.Status;
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

    public async Task<OctorateCheckInGuestResult[]> UpsertCheckInGuestsAsync(
        string accommodationId,
        string reservationId,
        IReadOnlyList<OctorateCheckInGuestUpsert> guests,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(guests);
        var path = CheckInGuestsPath(accommodationId, reservationId);
        var results = new List<OctorateCheckInGuestResult>(guests.Count);
        foreach (var guest in guests)
        {
            ct.ThrowIfCancellationRequested();
            var existingId = string.IsNullOrWhiteSpace(guest.OctorateGuestId) ? null : CheckedId(guest.OctorateGuestId, nameof(guests));
            var json = CheckInGuestJson(guest, existingId);
            try
            {
                var answer = existingId is null
                    ? await SendAsync(
                        () => new HttpRequestMessage(HttpMethod.Post, Address(path))
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json"),
                        },
                        "checkin-guest-create",
                        ct)
                    : await SendAsync(
                        () => new HttpRequestMessage(HttpMethod.Put, Address($"{path}/{Uri.EscapeDataString(existingId)}"))
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json"),
                        },
                        "checkin-guest-update",
                        ct);

                var (answeredId, validated) = ReadCheckInGuestAnswer(answer.Body);
                var guestId = existingId ?? answeredId;
                results.Add(guestId is null
                    ? new OctorateCheckInGuestResult(guest.Position, null, false, null, CheckInErrorCodes.Server)
                    : new OctorateCheckInGuestResult(guest.Position, guestId, existingId is null, validated, null));
            }
            catch (OctorateException ex)
            {
                results.Add(new OctorateCheckInGuestResult(guest.Position, existingId, false, null, CheckInErrorCodes.Of(ex)));
            }
        }

        return [.. results];
    }

    public async Task DeleteCheckInGuestAsync(string accommodationId, string reservationId, string octorateGuestId, CancellationToken ct)
    {
        var path = CheckInGuestsPath(accommodationId, reservationId);
        var guestId = CheckedId(octorateGuestId, nameof(octorateGuestId));
        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, Address($"{path}/{Uri.EscapeDataString(guestId)}")),
            "checkin-guest-delete",
            ct);
    }

    public async Task<OctorateReservationArrival> GetReservationArrivalAsync(string accommodationId, string reservationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        using var document = await GetJsonAsync(
            $"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}", "reservation-arrival", ct);
        var root = Unwrap(document.RootElement);
        if (root.ValueKind != JsonValueKind.Object)
            throw new OctorateServerException(null);

        var arrivalTime = OctorateHttp.StringOf(root, "arrivalTime");
        return new OctorateReservationArrival(
            InstantOf(OctorateHttp.StringOf(root, "effectiveCheckin")),
            arrivalTime is { Length: > ArrivalTimeMaxLength } ? arrivalTime[..ArrivalTimeMaxLength] : arrivalTime);
    }

    public async Task<OctoratePlaceEntry[]> GetCheckInCitiesAsync(string country, CancellationToken ct)
    {
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetter))
            throw new ArgumentException("A country is its two-letter code.", nameof(country));

        using var document = await GetJsonAsync($"/rest/v2/checkin/city/{Uri.EscapeDataString(country.ToUpperInvariant())}", "checkin-cities", ct);
        var places = ReadCheckInCities(document.RootElement);

        return places.Length > 0 ? places : throw new OctorateServerException(null);
    }

    internal static OctoratePlaceEntry[] ReadCheckInCities(JsonElement root) =>
    [
        .. Items(root)
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new OctoratePlaceEntry(
                OctorateHttp.StringOf(item, "internalName") ?? string.Empty,
                OctorateHttp.StringOf(item, "countryPoliceCode") ?? string.Empty,
                OctorateHttp.StringOf(item, "regionType")))
            .Where(place => place.Name.Length > 0 && place.PoliceCode.Length > 0),
    ];

    private static string CheckInGuestsPath(string accommodationId, string reservationId)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        return $"/rest/v1/checkin/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}/guest";
    }

    /// <summary>
    /// The JSON of a check-in guest, with the member names of Octorate's guest example (<see cref="OctorateCheckInGuestUpsert"/>): the members
    /// that are not set are left out, so a tax value that must not travel is never sent, not even as null.
    /// </summary>
    internal static string CheckInGuestJson(OctorateCheckInGuestUpsert guest, string? octorateGuestId)
    {
        ArgumentNullException.ThrowIfNull(guest);
        var body = new Dictionary<string, object?>
        {
            ["id"] = octorateGuestId,
            ["type"] = "GUEST",
            ["accommodatedType"] = guest.AccommodatedType,
            ["familyName"] = guest.FamilyName,
            ["givenName"] = guest.GivenName,
            ["sex"] = guest.Sex,
            ["birthDate"] = DateText(guest.BirthDate),
            ["birthCountry"] = guest.BirthCountry,
            ["birthCity"] = guest.BirthCity,
            ["citizenship"] = guest.Citizenship,
            ["nationality"] = guest.Citizenship,
            ["residenceCountry"] = guest.ResidenceCountry,
            ["city"] = guest.ResidenceCity,
            ["documentType"] = guest.DocumentType,
            ["documentCode"] = guest.DocumentCode,
            ["documentIssuePlace"] = guest.DocumentIssuePlace,
            ["documentIssueDate"] = DateText(guest.DocumentIssueDate),
            ["documentExpire"] = MidnightText(guest.DocumentExpire),
            ["checkin"] = DateText(guest.CheckIn),
            ["checkout"] = DateText(guest.CheckOut),
            ["draft"] = guest.Draft,
            ["cityTaxPrice"] = guest.CityTaxPrice,
            ["cityTaxExemption"] = guest.CityTaxExemption,
            ["excludeCityTax"] = guest.ExcludeCityTax,
        };
        return JsonSerializer.Serialize(body.Where(member => member.Value is not null).ToDictionary(member => member.Key, member => member.Value));
    }

    internal static (string? Id, bool? Validated) ReadCheckInGuestAnswer(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = Unwrap(document.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            var id = OctorateHttp.StringOf(root, "id");
            return (id is { Length: <= MaxAccommodationIdLength } ? id : null, BoolOf(root, "validated"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>An instant Octorate writes as text (ISO, UTC) or as milliseconds since 1970; null when it is neither.</summary>
    internal static DateTime? InstantOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
               && milliseconds is > 0 and < 253_402_300_800_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
            : null;
    }

    private static string? DateText(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The midnight of a day as a date-time (<c>2030-01-01T00:00:00</c>): <c>documentExpire</c> is a <c>date-time</c> in the OpenAPI and the
    /// sandbox read it back as written. Built from a <see cref="DateTime"/>, because a <see cref="DateOnly"/> refuses a format with a time part.
    /// </summary>
    private static string? MidnightText(DateOnly? date) =>
        date?.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public async Task<OctorateAvailabilityResult> CheckAvailabilityAsync(
        OctorateBookingTarget target,
        DateOnly from,
        DateOnly to,
        int guests,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(guests, 1);
        if (to <= from)
            throw new ArgumentException("A stay has at least one night.", nameof(to));

        var accommodation = CheckedAccommodationId(target.AccommodationId);
        var product = CheckedId(target.ProductId, nameof(target));

        var lastNight = to.AddDays(-1);
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/rest/v1/calendar/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(product)}/availabilityCheck?startDate={from:yyyy-MM-dd}&endDate={lastNight:yyyy-MM-dd}");

        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Address(path)), "availability-check", ct, NotAvailableStatus);
        return answer.StatusCode == NotAvailableStatus
            ? new OctorateAvailabilityResult(false, ReasonOf(answer.Body))
            : new OctorateAvailabilityResult(true, null);
    }

    public async Task<OctorateReservation> CreateReservationAsync(
        OctorateBookingTarget target,
        OctorateReservationCreate request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        var accommodation = CheckedAccommodationId(target.AccommodationId);
        CheckedId(target.ProductId, nameof(target));

        var channelId = OctorateReservationRequests.DirectChannelIdOf(configuration)
            ?? throw new InvalidOperationException(
                $"{OctorateReservationRequests.DirectChannelIdKey} must be the channel id of the direct bookings (Octorate__DirectChannelId, docs/runbooks/deploy-checklist.md).");
        var json = OctorateReservationRequests.CreateBody(target, request, channelId, timeProvider.GetUtcNow().UtcDateTime);

        // availability=true: the night is taken off every channel (the default of Octorate, written so it never depends on it).
        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address($"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}?availability=true"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "reservation-create",
            ct);

        using var document = ParseJson(answer);
        var root = document.RootElement;
        var item = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().FirstOrDefault() : Unwrap(root);
        return OctorateReservationReader.Read(item) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public Task<OctorateReservation> GetReservationAsync(OctorateBookingTarget target, string reservationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return GetReservationAsync(target.AccommodationId, reservationId, ct);
    }

    public async Task<OctorateReservation> UpdateReservationAsync(
        OctorateBookingTarget target,
        string reservationId,
        OctorateReservationUpdate update,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(update);
        var accommodation = CheckedAccommodationId(target.AccommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));

        var json = OctorateReservationRequests.UpdateBody(target, update);
        var path = $"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}";

        // Never over a reservation that changed on Octorate since the version the change is based on: Octorate has no ETag (D13), so the version CasaZen
        // saw is compared with the one Octorate holds now, and a difference is a conflict, as if Octorate had answered 409 (its own 409 is mapped alike).
        if (update.BasedOnUpdateTime is { } basedOn)
        {
            var held = await GetReservationAsync(accommodation, reservation, ct);
            if (held.UpdatedAt is { } heldAt && heldAt != basedOn)
            {
                if (HoldsTheStayOf(held, update))
                {
                    logger.LogInformation(
                        "Octorate reservation-update for org {OrgId}: the reservation already holds the stay and the guests of the change, nothing is written", orgScope.OrgId);
                    return held;
                }

                logger.LogInformation(
                    "Octorate reservation-update for org {OrgId}: the reservation changed on Octorate since the version of the change, nothing is written", orgScope.OrgId);
                throw new OctorateConflictException();
            }
        }

        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, Address(path))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "reservation-update",
            ct);

        // A 2xx is not the proof: Octorate leaves out, without saying so, what it does not write. The reservation is read again, and the stay and the
        // guests are what was sent or the change is a refusal, never a success. The price is Octorate's to work out (a rule of its own may differ): logged.
        var after = await GetReservationAsync(accommodation, reservation, ct);
        var mapper = new BookingStateMapper();
        var arrival = mapper.ToStayDate(after.CheckIn, null) is { } checkIn ? DateOnly.FromDateTime(checkIn) : (DateOnly?)null;
        var departure = mapper.ToStayDate(after.CheckOut, null) is { } checkOut ? DateOnly.FromDateTime(checkOut) : (DateOnly?)null;
        if ((arrival is { } a && a != update.CheckIn)
            || (departure is { } d && d != update.CheckOut)
            || (after.TotalGuests is { } guests && guests != update.Guests))
        {
            logger.LogWarning(
                "Octorate reservation-update for org {OrgId}: the change was accepted but the reservation does not hold the stay or the guests that were sent", orgScope.OrgId);
            throw new OctorateRequestRejectedException(200);
        }

        if (after.RoomGross is { } gross && Math.Abs(gross - update.RoomGross) >= 0.005m)
        {
            logger.LogWarning(
                "Octorate reservation-update for org {OrgId}: the price of the stay Octorate holds is not the one that was sent", orgScope.OrgId);
        }

        return after;
    }

    internal static bool HoldsTheStayOf(OctorateReservation reservation, OctorateReservationUpdate update)
    {
        var mapper = new BookingStateMapper();
        var arrival = mapper.ToStayDate(reservation.CheckIn, null) is { } checkIn ? DateOnly.FromDateTime(checkIn) : (DateOnly?)null;
        var departure = mapper.ToStayDate(reservation.CheckOut, null) is { } checkOut ? DateOnly.FromDateTime(checkOut) : (DateOnly?)null;
        if (arrival != update.CheckIn || departure != update.CheckOut || reservation.TotalGuests != update.Guests)
            return false;

        return reservation.RoomGross is not { } gross || Math.Abs(gross - update.RoomGross) < 0.005m;
    }

    public async Task<OctoratePaymentResult> RegisterPaymentAsync(
        string accommodationId,
        string reservationId,
        OctoratePaymentRegistration payment,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var accommodation = CheckedAccommodationId(accommodationId);
        var reservation = CheckedId(reservationId, nameof(reservationId));
        var json = OctorateReservationRequests.PaymentBody(payment);

        var answer = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                Address($"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservation)}/payment"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "reservation-payment",
            ct);
        return new OctoratePaymentResult(PaymentIdOf(answer.Body));
    }

    public async Task<OctorateReservation?> FindReservationByExternalReferenceAsync(
        string accommodationId,
        string externalReference,
        CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reference = externalReference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > OctorateReservationRequests.MaxReferenceLength)
            throw new ArgumentException("A reference has 1 to 25 characters.", nameof(externalReference));

        var today = timeProvider.TodayInRomeAsDateOnly();
        var from = today.AddDays(-FindLookbackDays);
        var to = today.AddDays(1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        for (var page = 0; page < MaxFindPages; page++)
        {
            var result = await GetReservationsAsync(
                accommodation,
                new OctorateReservationQuery(OctorateReservationListType.CreateTime, from, to, page, OctorateReservationQuery.MaxPageSize),
                ct);

            var found = result.Reservations.FirstOrDefault(r => string.Equals(r.Refer?.Trim(), reference, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found;

            if (result.Unreadable > 0)
                complete = false;

            var added = result.Reservations.Count(r => seen.Add(r.Id));
            if (result.ItemCount == 0 || added == 0)
            {
                if (complete)
                    return null;

                logger.LogWarning(
                    "Octorate reservation lookup of org {OrgId}: some reservations could not be read, the absence is not proven", orgScope.OrgId);
                throw new OctorateServerException(null);
            }
        }

        logger.LogWarning(
            "Octorate reservation lookup of org {OrgId}: stopped after {Pages} pages, the absence is not proven", orgScope.OrgId, MaxFindPages);
        throw new OctorateServerException(null);
    }

    public async Task<OctoratePmsRoom[]> GetPmsRoomsAsync(string accommodationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        using var document = await GetJsonAsync($"/rest/v1/pms/{Uri.EscapeDataString(accommodation)}", "pms-rooms", ct);
        return [.. Items(document.RootElement).Select(ReadPmsRoom).OfType<OctoratePmsRoom>()];
    }

    public async Task<OctorateChatThreadPage> GetChatThreadsAsync(string accommodationId, int offset, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/rest/v1/chat/{Uri.EscapeDataString(accommodation)}/threads?offset={offset}&filterReadStatus=false");
        using var document = await GetChatJsonAsync(path, "chat-threads", ct);

        var page = OctorateChatReader.ReadThreadPage(document.RootElement) ?? throw new OctorateServerException(null);
        if (page.Unreadable > 0)
        {
            logger.LogWarning(
                "Octorate chat threads of org {OrgId}: {Items} items of the page at {Offset} could not be read", orgScope.OrgId, page.Unreadable, offset);
        }

        return page;
    }

    public async Task<OctorateChatThread> GetChatThreadAsync(string accommodationId, string threadId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var thread = CheckedThreadId(threadId);
        using var document = await GetChatJsonAsync(
            $"/rest/v1/chat/{Uri.EscapeDataString(accommodation)}/threads/{Uri.EscapeDataString(thread)}", "chat-thread", ct);
        return OctorateChatReader.ReadThread(Unwrap(document.RootElement), thread) ?? throw new OctorateServerException(null);
    }

    public async Task<OctorateChatMessage[]> GetChatMessagesAsync(string accommodationId, string threadId, DateTime? createdAfter, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var thread = CheckedThreadId(threadId);
        var basePath = $"/rest/v1/chat/{Uri.EscapeDataString(accommodation)}/threads/{Uri.EscapeDataString(thread)}/messages";
        var after = createdAfter is { } instant
            ? "createdAfter=" + Uri.EscapeDataString(AsUtcInstant(instant).ToString(ChatInstantFormat, CultureInfo.InvariantCulture))
            : null;

        var messages = new List<OctorateChatMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxChatMessagePages; page++)
        {
            var query = string.Join('&', new[] { after, page > 0 ? string.Create(CultureInfo.InvariantCulture, $"page={page}") : null }.OfType<string>());
            using var document = await GetChatJsonAsync(query.Length > 0 ? $"{basePath}?{query}" : basePath, "chat-messages", ct);

            var read = OctorateChatReader.ReadMessagePage(document.RootElement) ?? throw new OctorateServerException(null);
            if (read.ItemCount > read.Messages.Count)
            {
                logger.LogWarning(
                    "Octorate chat messages of thread {ThreadId} of org {OrgId}: {Items} items could not be read",
                    thread,
                    orgScope.OrgId,
                    read.ItemCount - read.Messages.Count);
            }

            var added = 0;
            foreach (var message in read.Messages)
            {
                if (seen.Add(message.Id))
                {
                    messages.Add(message);
                    added++;
                }
            }

            if (read.ItemCount == 0 || read.TotalPages is null || page + 1 >= read.TotalPages)
                return [.. messages];

            if (added == 0)
                throw new OctorateServerException(null);
        }

        logger.LogWarning(
            "Octorate chat messages of thread {ThreadId} of org {OrgId}: stopped after {Pages} pages, the thread may be longer",
            thread,
            orgScope.OrgId,
            MaxChatMessagePages);
        throw new OctorateServerException(null);
    }

    public async Task<OctorateChatMessage[]> GetRecentChatMessagesAsync(string accommodationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        using var document = await GetChatJsonAsync(
            $"/rest/v1/accommodation/recentMessages?property={Uri.EscapeDataString(accommodation)}", "chat-recent", ct, RecentChatErrorOf);

        var read = OctorateChatReader.ReadMessagePage(document.RootElement) ?? throw new OctorateServerException(null);
        if (read.ItemCount > read.Messages.Count)
        {
            logger.LogWarning(
                "Octorate recent chat messages of org {OrgId}: {Items} items could not be read", orgScope.OrgId, read.ItemCount - read.Messages.Count);
        }

        return [.. read.Messages];
    }

    public async Task<OctoratePortal[]> GetPortalsAsync(CancellationToken ct)
    {
        using var document = await GetPlatformJsonAsync("/rest/v1/meta/portals", "portals", ct);
        return [.. Items(document.RootElement).Select(ReadPortal).OfType<OctoratePortal>()];
    }

    public async Task<OctorateBillingData?> GetBookerBillingDataAsync(string accommodationId, string reservationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);

        var customerId = (await GetReservationAsync(accommodation, reservationId, ct)).InvoiceHolderId;
        if (customerId is null)
            return null;

        using var document = await GetJsonAsync(
            $"/rest/v1/guests/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(customerId)}", "customer-billing", ct);
        return OctorateBillingMapping.Read(Unwrap(document.RootElement));
    }

    public async Task<bool> UpsertBookerBillingDataAsync(string accommodationId, string reservationId, OctorateBillingData data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        var accommodation = CheckedAccommodationId(accommodationId);

        var json = OctorateBillingMapping.WriteBody(data);
        if (json == OctorateBillingMapping.EmptyBody)
            throw new ArgumentException("Billing data with nothing to write is not sent.", nameof(data));

        var customerId = (await GetReservationAsync(accommodation, reservationId, ct)).InvoiceHolderId;
        if (customerId is null)
            return false;

        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Put,
                Address($"/rest/v1/guests/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(customerId)}"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "customer-billing-write",
            ct);
        return true;
    }

    public async Task<OctorateConnectionSummary[]> GetOrgConnectionsAsync(CancellationToken ct)
    {
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
    /// <paramref name="answerStatus"/> is a status that is an answer of the operation and not an error (the 406 of the availability check).
    /// </summary>
    private async Task<OctorateAnswer> SendAsync(
        Func<HttpRequestMessage> createRequest,
        string operation,
        CancellationToken ct,
        int? answerStatus = null,
        Func<int, string, OctorateException?>? translateError = null,
        Func<int, string?, string, OctorateException?>? mapError = null,
        bool binary = false)
    {
        var orgId = orgScope.OrgId;
        var quotaOrg = orgId;
        if (master is not null)
        {
            using var candidate = createRequest();
            quotaOrg = await master.AuthorizeAsync(orgId, candidate.Method, candidate.RequestUri!, ct);
        }
        var caller = new Caller(
            $"org {orgId}",
            token => tokenService.GetAccessTokenAsync(orgId, token),
            (rejected, token) => tokenService.RefreshRejectedTokenAsync(orgId, rejected, token),
            () => rateLimiter.TryAcquireAsync(quotaOrg));
        return await SendAsync(createRequest, operation, caller, ct, answerStatus, translateError, mapError, binary);
    }

    /// <summary>
    /// One partner-level call (the application's own, no org): the partner token, a refused partner token renewed once, and a call from the
    /// bucket <see cref="OctorateRateLimiter.PlatformKey"/>. Otherwise the same as <see cref="SendAsync(Func{HttpRequestMessage}, string, CancellationToken)"/>.
    /// </summary>
    private Task<OctorateAnswer> SendAsPlatformAsync(
        Func<HttpRequestMessage> createRequest,
        string operation,
        CancellationToken ct,
        Func<int, string, OctorateException?>? translateError = null)
    {
        var caller = new Caller(
            OctorateRateLimiter.PlatformKey,
            tokenService.GetPartnerAccessTokenAsync,
            tokenService.RefreshRejectedPartnerTokenAsync,
            rateLimiter.TryAcquirePlatformAsync);
        return SendAsync(createRequest, operation, caller, ct, translateError: translateError);
    }

    private async Task<OctorateAnswer> SendAsync(
        Func<HttpRequestMessage> createRequest,
        string operation,
        Caller caller,
        CancellationToken ct,
        int? answerStatus = null,
        Func<int, string, OctorateException?>? translateError = null,
        Func<int, string?, string, OctorateException?>? mapError = null,
        bool binary = false)
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
                if (binary && response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    logger.LogDebug("Octorate {Operation} for {Caller}: HTTP {StatusCode}, {Bytes} bytes", operation, caller.Label, status, bytes.Length);
                    return new OctorateAnswer(status, string.Empty, bytes);
                }

                var body = await OctorateHttp.ReadBodyAsync(response, ct);
                var elapsedMs = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
                if (response.IsSuccessStatusCode || status == 304 || status == answerStatus)
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

                if (translateError?.Invoke(status, body) is { } translated)
                    throw translated;

                throw mapError?.Invoke(status, errorType, body) ?? ErrorOf(status, errorType, body);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, string operation, CancellationToken ct)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Address(pathAndQuery)), operation, ct);
        return ParseJson(answer);
    }

    private async Task<JsonDocument> GetChatJsonAsync(
        string pathAndQuery,
        string operation,
        CancellationToken ct,
        Func<int, string?, string, OctorateException?>? mapError = null)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Address(pathAndQuery)), operation, ct, mapError: mapError ?? ChatErrorOf);
        return ParseJson(answer);
    }

    /// <summary>
    /// The refusal that is the chat's own: a 403 whose answer is not an error of Octorate's API (no <c>type</c>: the page of the application server,
    /// which is what the sandbox answers to every chat call of a license without the <c>chat</c> module) and not the wrong environment. A 403 that
    /// is a typed error of the API (a refused token, the wrong environment) keeps its common meaning; <c>null</c> for every other answer. Decided by
    /// the status and the shape of the answer, never by its text.
    /// </summary>
    internal static OctorateException? ChatErrorOf(int status, string? errorType, string body) =>
        status == 403 && string.IsNullOrEmpty(errorType) && !IsWrongEnvironment(errorType, body)
            ? OctorateAuthException.ChatNotLicensed(status)
            : null;

    /// <summary>
    /// The refusals of <c>recentMessages</c>: those of <see cref="ChatErrorOf"/>, and a 500 that is not an error of Octorate's API either (no <c>type</c>).
    /// Where the other chat reads answer 403, the sandbox answers this one with a 500 when the <c>chat</c> module is not in the license
    /// (<c>sandbox-run-results.md</c>, <c>octorate-integration-facts.md</c>: "chat not available"), and it is the first call of every pass of the
    /// reconciliation. A 500 that carries a <c>type</c> is a failure of Octorate and keeps its common meaning; <c>null</c> for every other answer.
    /// Decided by the status and the shape of the answer, never by its text.
    /// </summary>
    internal static OctorateException? RecentChatErrorOf(int status, string? errorType, string body) =>
        status == 500 && string.IsNullOrEmpty(errorType)
            ? OctorateAuthException.ChatNotLicensed(status)
            : ChatErrorOf(status, errorType, body);

    internal static string CheckedThreadId(string threadId)
    {
        var id = threadId?.Trim();
        if (!IsNumericId(id))
            throw new ArgumentException($"A thread id is a number of 1 to {MaxConnectionIdDigits} digits.", nameof(threadId));

        return id!;
    }

    private static DateTime AsUtcInstant(DateTime instant) =>
        instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc);

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

    internal static OctorateException ErrorOf(int status, string? errorType, string body) => status switch
    {
        404 => new OctorateNotFoundException(),
        409 => new OctorateConflictException(),
        429 => new OctorateRateLimitException(status),
        403 when IsQuotaExhausted(errorType, body) => new OctorateRateLimitException(status, quotaExhausted: true),
        401 or 403 when IsWrongEnvironment(errorType, body) => new OctorateWrongEnvironmentException(status),
        401 or 403 => OctorateAuthException.Refused(status),
        >= 500 => new OctorateServerException(status),
        _ => new OctorateRequestRejectedException(status),
    };

    private static bool IsQuotaExhausted(string? errorType, string body) =>
        string.Equals(errorType, OctorateRateLimitException.QuotaErrorType, StringComparison.OrdinalIgnoreCase)
        || body.Contains(OctorateRateLimitException.QuotaErrorType, StringComparison.OrdinalIgnoreCase);

    private static bool IsWrongEnvironment(string? errorType, string body) =>
        string.Equals(errorType, OctorateWrongEnvironmentException.OctorateErrorType, StringComparison.OrdinalIgnoreCase)
        || body.Contains(OctorateWrongEnvironmentException.OctorateErrorType, StringComparison.OrdinalIgnoreCase);

    internal static string CheckedAccommodationId(string accommodationId)
    {
        var id = accommodationId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxAccommodationIdLength)
            throw new ArgumentException($"An accommodation id has 1 to {MaxAccommodationIdLength} characters.", nameof(accommodationId));

        return id;
    }

    internal static string CheckedId(string value, string parameterName)
    {
        var id = value?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxAccommodationIdLength)
            throw new ArgumentException($"An id has 1 to {MaxAccommodationIdLength} characters.", parameterName);

        return id;
    }

    internal static string CheckedConnectionId(string connectionId)
    {
        var id = connectionId?.Trim();
        if (!IsNumericId(id))
            throw new ArgumentException($"A connection id is a number of 1 to {MaxConnectionIdDigits} digits.", nameof(connectionId));

        return id!;
    }

    private static bool IsNumericId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxConnectionIdDigits && id.All(char.IsAsciiDigit);

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

    private static bool NamesTheAction(JsonElement wrapper, OctorateD6Operation operation) =>
        wrapper.TryGetProperty("additionalInfo", out var info)
        && info.ValueKind == JsonValueKind.Object
        && OctorateHttp.StringOf(info, "actionPerformed") is { } performed
        && string.Equals(performed, D6OperationName(operation), StringComparison.OrdinalIgnoreCase);

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
            BoolOf(item, "userLogged"),
            DateTimeOf(item, "loginAt"));
    }

    internal static string ReservationListTypeName(OctorateReservationListType type) => type switch
    {
        OctorateReservationListType.ModifiedBetween => "MODIFIED_BETWEEN",
        OctorateReservationListType.CreateTime => "CREATETIME",
        OctorateReservationListType.Cancellation => "CANCELLATION",
        OctorateReservationListType.Stay => "STAY",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of list of reservations."),
    };

    internal static string CheckInStatusName(OctorateCheckInStatus status) => status switch
    {
        OctorateCheckInStatus.CheckInEnd => "CHECKIN_END",
        OctorateCheckInStatus.CheckOutEnd => "CHECKOUT_END",
        OctorateCheckInStatus.NoShow => "NO_SHOW",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a check-in status."),
    };

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
            Country = OctorateHttp.StringOf(item, "countryAlpha2") ?? OctorateHttp.StringOf(location, "countryAlpha2")
                ?? OctorateHttp.StringOf(item, "country") ?? OctorateHttp.StringOf(location, "country"),
            Latitude = latitude,
            Longitude = longitude,
            Category = OctorateHttp.StringOf(item, "propertyCategory"),
            NetworkId = OctorateHttp.StringOf(network, "accessNetwork"),
            BillingNetworkName = OctorateHttp.StringOf(item, "billingNetworkName"),
            NetworkAccessEnabled = BoolOf(network, "accessEnabled"),
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

    internal static OctorateChannelConnection? ReadConnection(JsonElement item) =>
        item.ValueKind != JsonValueKind.Object
            ? null
            : new OctorateChannelConnection(
                OctorateHttp.StringOf(item, "id"),
                OctorateHttp.StringOf(item, "channelId"),
                OctorateHttp.StringOf(item, "channelName"),
                OctorateHttp.StringOf(item, "hotelId"),
                OctorateHttp.StringOf(item, "status"));

    internal static OctoratePmsRoom? ReadPmsRoom(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateHttp.StringOf(item, "id");
        return id is null || id.Length > MaxAccommodationIdLength
            ? null
            : new OctoratePmsRoom(id, OctorateHttp.StringOf(item, "parentId"), OctorateHttp.StringOf(item, "name"));
    }

    internal static string? PaymentIdOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return OctorateHttp.StringOf(Unwrap(document.RootElement), "id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The reason Octorate gives when a room cannot be booked (sandbox: <c>Not sellable</c>, <c>Day size mismatch</c>): the text of the answer,
    /// or its <c>message</c>, <c>description</c>, <c>error</c> or <c>type</c>, on one line and at most <see cref="MaxReasonLength"/> characters;
    /// <c>null</c> for an empty answer or a page of HTML.
    /// </summary>
    internal static string? ReasonOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        string? text;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            text = root.ValueKind == JsonValueKind.String
                ? root.GetString()
                : FirstStringOf(root, ReasonMembers);
        }
        catch (JsonException)
        {
            text = body.TrimStart().StartsWith('<') ? null : body;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= MaxReasonLength ? line : line[..MaxReasonLength];
    }

    private static readonly string[] ReasonMembers = ["message", "description", "error", "type"];

    private static readonly string[] PortalCodeMembers = ["portalCode", "channelId", "portalId", "id", "code"];

    /// <summary>
    /// The names under which a portal's name may come, most specific first. <c>meta/portals</c> sends the public name as
    /// <c>externalName</c> («Booking», «Airbnb»); its <c>internalName</c> is a technical id and is never shown.
    /// </summary>
    private static readonly string[] PortalNameMembers = ["externalName", "name", "portalName", "title"];

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

    private static string? FirstStringOf(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            if (OctorateHttp.StringOf(element, name) is { } text)
                return text;
        }

        return null;
    }

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

    internal static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();

        return [];
    }

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

    internal sealed record OctorateAnswer(int StatusCode, string Body, byte[]? Bytes = null);

    private sealed record Caller(
        string Label,
        Func<CancellationToken, Task<string>> GetTokenAsync,
        Func<string, CancellationToken, Task<string>> RefreshRejectedAsync,
        Func<Task<bool>> TryAcquireAsync);
}
