using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The operations on a connection of <see cref="IOctorateApiClient"/> (E09/F02/US01/T01, NF-56 to NF-58): list, create, change and delete a
/// connection, run one of its actions, read and save the mapping of the rooms. One method for each Octorate operation, with the same HTTPS client,
/// timeout, tenant isolation, rate-limit gate, backoff and typed errors as the rest of the client; the one addition is the 550 of the partner
/// (<see cref="OctoratePartnerFailureException"/>), mapped here and nowhere else so the calls of the other epics keep their mapping.
/// </summary>
/// <remarks>
/// <para><b>Credentials.</b> The portal username and password travel in the body of one create (or one change) and nowhere else: the log lines
/// carry the connection id, the portal code and the status; the exceptions of the client never carry a body.</para>
/// <para><b>Proof.</b> The paths and bodies are the ones of the OpenAPI of 2026-10-31 (S13). The sandbox has no channel connected and its API
/// portal needs a category CasaZen does not have, so none of these calls has been run against a real connection: an answer that is not the
/// documented shape is a failure, never a success.</para>
/// </remarks>
public sealed partial class OctorateApiClient
{
    /// <summary>The members of a connection the list asks for: never its portal username or password.</summary>
    private const string ConnectionInfoFields =
        "id,channelId,accommodationId,hotelId,status,userLogged,loginAt,updateCalendar,pullReservations,lastPortalAcceptLog";

    /// <summary>The most mappings one save sends (a listing has a handful of rooms and rates; more is a mistake).</summary>
    internal const int MaxMappingsPerSave = 100;

    /// <summary>The most digits of a portal code CasaZen sends as a number.</summary>
    private const int MaxPortalCodeDigits = 18;

    /// <summary>The longest line of the channel's own words that is kept (<see cref="OctorateConnectionInfo.LastError"/>).</summary>
    private const int MaxChannelTextLength = 200;

    /// <summary>The names under which the log of the channel may give its moment, most specific first.</summary>
    private static readonly string[] PortalLogTimeMembers = ["time", "createTime", "date", "timestamp", "updateTime", "insertTime"];

    /// <summary>The names under which the log of the channel may give a failure, most specific first.</summary>
    private static readonly string[] PortalLogErrorMembers = ["errorMessage", "error"];

    /// <summary>The names under which the log of the channel may give its text, read as a failure only when the log says it failed.</summary>
    private static readonly string[] PortalLogTextMembers = ["message", "description", "text"];

    public async Task<OctorateConnectionInfo[]> ListConnectionsAsync(string accommodationCode, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationCode);
        using var document = await GetConnectionJsonAsync(
            $"/rest/v1/connection?accommodation={Uri.EscapeDataString(accommodation)}&fields={Uri.EscapeDataString(ConnectionInfoFields)}",
            "connections-list",
            ct);
        var root = document.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("data", out _)
            ? new[] { root }
            : Items(root);
        return [.. items.Select(item => ReadConnectionInfo(item, accommodation)).OfType<OctorateConnectionInfo>()];
    }

    public async Task<OctorateConnectionInfo> CreateConnectionAsync(string accommodationCode, OctorateConnectionCreate request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accommodation = CheckedAccommodationId(accommodationCode);
        var json = ConnectionCreateBody(request);

        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address($"/rest/v1/connection/{Uri.EscapeDataString(accommodation)}")) { Content = JsonContent(json) },
            "connection-create",
            ct,
            mapError: PartnerFailureOf);

        // The answer is the connection with its credentials echoed: only the members CasaZen needs are read, and an answer with no usable id is
        // not a success (the connection may exist: the caller looks it up with the list before it creates another).
        using var document = ParseJson(answer);
        var created = ReadConnectionInfo(Unwrap(document.RootElement), accommodation) ?? throw new OctorateServerException(answer.StatusCode);
        logger.LogInformation(
            "Octorate connection {ConnectionId} created for portal {PortalCode}: {Status}", created.ConnectionId, created.PortalCode, created.Status ?? "no status");
        return created;
    }

    public async Task UpdateConnectionAsync(string accommodationCode, string connectionId, OctorateConnectionPatch patch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.IsEmpty)
            throw new ArgumentException("A change of a connection sets at least one member.", nameof(patch));

        var accommodation = CheckedAccommodationId(accommodationCode);
        var connection = CheckedConnectionId(connectionId);
        var json = ConnectionPatchBody(patch);

        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Patch,
                Address($"/rest/v1/connection/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(connection)}"))
            {
                Content = JsonContent(json),
            },
            "connection-update",
            ct,
            mapError: PartnerFailureOf);
        logger.LogInformation("Octorate connection {ConnectionId} changed", connection);
    }

    public async Task DeleteConnectionAsync(string accommodationCode, string connectionId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationCode);
        var connection = CheckedConnectionId(connectionId);

        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Delete,
                Address($"/rest/v1/connection/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(connection)}")),
            "connection-delete",
            ct,
            mapError: PartnerFailureOf);
        logger.LogInformation("Octorate connection {ConnectionId} deleted", connection);
    }

    public async Task<IReadOnlyList<OctorateRoomMapping>> RunConnectionActionAsync(
        string accommodationCode,
        string connectionId,
        OctorateConnectionAction action,
        CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationCode);
        var connection = CheckedConnectionId(connectionId);
        var name = ConnectionActionName(action);

        var answer = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                Address($"/rest/v1/connection/{name}/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(connection)}")),
            $"connection-{name}",
            ct,
            mapError: PartnerFailureOf);

        var mappings = ReadActionMappings(name, answer);
        logger.LogInformation("Octorate connection {ConnectionId} action {Action} done: {Mappings} mappings listed", connection, name, mappings.Count);
        return mappings;
    }

    public async Task<OctorateChannelRoom[]> GetConnectionMappingAsync(string accommodationCode, string connectionId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationCode);
        var connection = CheckedConnectionId(connectionId);
        var suffix = $"{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(connection)}";

        OctorateChannelRoom[] rooms;
        using (var external = await GetConnectionJsonAsync($"/rest/v1/connection/external/{suffix}", "connection-rooms", ct))
            rooms = [.. Items(external.RootElement).Select(ReadChannelRoom).OfType<OctorateChannelRoom>()];

        var productOf = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var mapped = await GetConnectionJsonAsync($"/rest/v1/connection/mapping/{suffix}", "connection-mapping", ct))
        {
            foreach (var item in Items(mapped.RootElement))
            {
                if (ReadImportedMapping(item) is { } mapping)
                    productOf.TryAdd(mapping.ExternalId, mapping.ProductId);
            }
        }

        return [.. rooms.Select(room => productOf.TryGetValue(room.ChannelRoomId, out var product) ? room with { MappedProductId = product } : room)];
    }

    public async Task SaveConnectionMappingAsync(
        string accommodationCode,
        string connectionId,
        IReadOnlyList<OctorateRoomMapping> mappings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count == 0)
            throw new ArgumentException("A save of the mapping names at least one room.", nameof(mappings));
        if (mappings.Count > MaxMappingsPerSave)
            throw new ArgumentException($"A save of the mapping names at most {MaxMappingsPerSave} rooms.", nameof(mappings));

        var accommodation = CheckedAccommodationId(accommodationCode);
        var connection = CheckedConnectionId(connectionId);

        // Everything is checked before the first call leaves: a bad id never leaves half of the mapping saved.
        var pairs = mappings
            .Select(mapping => (Product: CheckedId(mapping.ProductId, nameof(mappings)), Channel: CheckedId(mapping.ChannelRoomId, nameof(mappings))))
            .ToList();

        foreach (var (product, channel) in pairs)
        {
            await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Post,
                    Address(
                        $"/rest/v1/connection/mapping/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(connection)}"
                        + $"?product={Uri.EscapeDataString(product)}&externalProduct={Uri.EscapeDataString(channel)}")),
                "connection-mapping-save",
                ct,
                mapError: PartnerFailureOf);
        }

        logger.LogInformation("Octorate connection {ConnectionId} mapping saved: {Mappings} rooms", connection, pairs.Count);
    }

    /// <summary>The 550 of Octorate (the partner fails) is its own error; every other status keeps the common mapping of <see cref="ErrorOf"/>.</summary>
    internal static OctorateException? PartnerFailureOf(int status, string? errorType, string body) =>
        status == OctoratePartnerFailureException.OctorateStatus ? new OctoratePartnerFailureException() : null;

    /// <summary>The <c>action</c> of the connection operation, as Octorate spells it.</summary>
    internal static string ConnectionActionName(OctorateConnectionAction action) => action switch
    {
        OctorateConnectionAction.ImportRooms => "IMPORT_ROOMS",
        OctorateConnectionAction.ImportReservations => "IMPORT_RESERVATIONS",
        OctorateConnectionAction.ImportCalendar => "IMPORT_CALENDAR",
        OctorateConnectionAction.MarkConnected => "MARK_CONNECTED",
        OctorateConnectionAction.CommitMappingAsCompleted => "COMMIT_MAPPING_AS_COMPLETED",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not an action of a connection."),
    };

    /// <summary>
    /// The body of a create: the portal as the number Octorate calls <c>channelId</c> (and as the <c>portal</c> the specification requires), the
    /// access data the portal asked for, and the settings of a connection that reads and writes nothing yet: pulls off, only mapped rooms, no
    /// content push (D6).
    /// </summary>
    internal static string ConnectionCreateBody(OctorateConnectionCreate request)
    {
        var channel = CheckedPortalCode(request.PortalCode);
        var body = new JsonObject
        {
            ["channelId"] = channel,
            ["portal"] = new JsonObject { ["id"] = channel },
            ["updateCalendar"] = false,
            ["pullReservations"] = false,
            ["pullMappedOnly"] = true,
            ["content"] = false,
            ["mailOnError"] = false,
            ["correctionRatio"] = 0,
            ["correctionFixed"] = false,
            ["correctionRound"] = false,
            ["availabilityPercentage"] = 100,
        };

        if (request.HotelId?.Trim() is { Length: > 0 } hotel)
            body["hotelId"] = hotel;

        if (request.Username?.Trim() is { Length: > 0 } username)
            body["username"] = username;

        if (!string.IsNullOrEmpty(request.Password))
            body["password"] = request.Password;

        return body.ToJsonString();
    }

    /// <summary>The body of a change: the members that are set, and nothing else.</summary>
    internal static string ConnectionPatchBody(OctorateConnectionPatch patch)
    {
        var body = new JsonObject();
        if (patch.UpdateCalendar is { } updateCalendar)
            body["updateCalendar"] = updateCalendar;

        if (patch.PullReservations is { } pullReservations)
            body["pullReservations"] = pullReservations;

        if (patch.PullMappedOnly is { } pullMappedOnly)
            body["pullMappedOnly"] = pullMappedOnly;

        if (patch.Content is { } content)
            body["content"] = content;

        if (patch.MailOnError is { } mailOnError)
            body["mailOnError"] = mailOnError;

        if (patch.AvailabilityPercentage is { } availability)
            body["availabilityPercentage"] = availability;

        if (patch.CorrectionRatio is { } ratio)
            body["correctionRatio"] = ratio;

        if (patch.CorrectionFixed is { } fixedAmount)
            body["correctionFixed"] = fixedAmount;

        if (patch.CorrectionRound is { } round)
            body["correctionRound"] = round;

        if (patch.HotelId?.Trim() is { Length: > 0 } hotel)
            body["hotelId"] = hotel;

        if (patch.Username?.Trim() is { Length: > 0 } username)
            body["username"] = username;

        if (!string.IsNullOrEmpty(patch.Password))
            body["password"] = patch.Password;

        return body.ToJsonString();
    }

    /// <summary>The portal code as a number: 1 to <see cref="MaxPortalCodeDigits"/> digits, as the specification declares <c>channelId</c> (<c>int64</c>).</summary>
    internal static long CheckedPortalCode(string portalCode)
    {
        var code = portalCode?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > MaxPortalCodeDigits || !code.All(char.IsAsciiDigit))
            throw new ArgumentException($"A portal code is a number of 1 to {MaxPortalCodeDigits} digits.", nameof(portalCode));

        return long.Parse(code, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A connection of an answer of <c>connection</c>, or <c>null</c> when the item has no numeric id or no channel, or when it belongs to another
    /// accommodation than <paramref name="requestedAccommodation"/> (an answer that ignores the filter must not give a connection of another
    /// listing). The portal username and password the item may hold are not read.
    /// </summary>
    internal static OctorateConnectionInfo? ReadConnectionInfo(JsonElement item, string? requestedAccommodation)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        item.TryGetProperty("accommodation", out var accommodation);
        item.TryGetProperty("portal", out var portal);
        var id = OctorateHttp.StringOf(item, "id");
        var channel = OctorateHttp.StringOf(item, "channelId") ?? OctorateHttp.StringOf(portal, "id");
        if (!IsNumericId(id) || channel is null || channel.Length > MaxAccommodationIdLength)
            return null;

        var owner = OctorateHttp.StringOf(item, "accommodationId") ?? OctorateHttp.StringOf(accommodation, "id");
        if (requestedAccommodation is not null && owner is not null && !string.Equals(owner, requestedAccommodation, StringComparison.Ordinal))
            return null;

        var loginAt = DateTimeOf(item, "loginAt");
        var (loggedAt, error) = ReadPortalLog(item);
        return new OctorateConnectionInfo(
            id!,
            channel,
            OctorateHttp.StringOf(item, "status"),
            loggedAt ?? loginAt,
            error,
            BoolOf(item, "userLogged"),
            loginAt,
            BoolOf(item, "updateCalendar"),
            BoolOf(item, "pullReservations"));
    }

    /// <summary>
    /// The moment and the failure of the last log of the channel (<c>lastPortalAcceptLog</c>). A bare moment is a moment; a bare text is
    /// not read as an error (a log is as likely to report a success). From an object: the first moment it gives, and the text of an explicit
    /// error member, or of its message when the log itself says it failed. Anything else is <c>null</c>.
    /// </summary>
    private static (DateTime? At, string? Error) ReadPortalLog(JsonElement item)
    {
        if (!item.TryGetProperty("lastPortalAcceptLog", out var log))
            return (null, null);

        if (log.ValueKind != JsonValueKind.Object)
            return (DateTimeOf(item, "lastPortalAcceptLog"), null);

        DateTime? at = null;
        foreach (var name in PortalLogTimeMembers)
        {
            if (DateTimeOf(log, name) is { } moment)
            {
                at = moment;
                break;
            }
        }

        var failed = BoolOf(log, "success") == false
                     || BoolOf(log, "ok") == false
                     || BoolOf(log, "failed") == true
                     || BoolOf(log, "error") == true
                     || IsFailureWord(OctorateHttp.StringOf(log, "status") ?? OctorateHttp.StringOf(log, "result"));
        string? error = null;
        foreach (var name in PortalLogErrorMembers)
        {
            if (log.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String && OneLine(member.GetString()) is { } text)
            {
                error = text;
                break;
            }
        }

        if (error is null && failed)
            error = OneLine(FirstStringOf(log, PortalLogTextMembers)) ?? "failed";

        return (at, error);
    }

    private static bool IsFailureWord(string? word) =>
        word is not null && (word.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                             || word.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                             || word.Equals("FAILURE", StringComparison.OrdinalIgnoreCase)
                             || word.Equals("KO", StringComparison.OrdinalIgnoreCase)
                             || word.Equals("REFUSED", StringComparison.OrdinalIgnoreCase));

    /// <summary>A text of the channel on one line, at most <see cref="MaxChannelTextLength"/> characters; <c>null</c> when blank.</summary>
    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= MaxChannelTextLength ? line : line[..MaxChannelTextLength];
    }

    /// <summary>
    /// A room of the channel of an answer of <c>connection/external</c>, or <c>null</c> when the item has no usable id. The id is the one of the
    /// external product, the <c>externalId</c> of the mapping; the name is the room's, else its code on the channel, else the id.
    /// </summary>
    internal static OctorateChannelRoom? ReadChannelRoom(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateHttp.StringOf(item, "id");
        if (id is null || id.Length > MaxAccommodationIdLength)
            return null;

        var name = OneLine(OctorateHttp.StringOf(item, "roomName")) ?? OneLine(OctorateHttp.StringOf(item, "roomId")) ?? id;
        return new OctorateChannelRoom(
            id,
            name,
            null,
            OneLine(OctorateHttp.StringOf(item, "rateName")),
            BoolOf(item, "manageable") ?? true);
    }

    /// <summary>
    /// The mappings an action listed: a bare list, or the <c>data</c> list of the wrapper that names the action performed (the example of the
    /// specification). Anything else (a body that is not a list, an error that came with HTTP 200, a wrapper without <c>data</c> or one that names
    /// another action) is <see cref="OctorateServerException"/>: the result is unknown and is never read as a success.
    /// </summary>
    internal static IReadOnlyList<OctorateRoomMapping> ReadActionMappings(string actionName, OctorateAnswer answer)
    {
        using var document = ParseJson(answer);
        var root = document.RootElement;
        var list = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when WrapperNamesAction(root, actionName) && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            _ => throw new OctorateServerException(answer.StatusCode),
        };

        return
        [
            .. list.EnumerateArray()
                .Select(ReadImportedMapping)
                .OfType<OctorateImportedMapping>()
                .Select(mapping => new OctorateRoomMapping(mapping.ProductId, mapping.ExternalId)),
        ];
    }

    private static bool WrapperNamesAction(JsonElement wrapper, string actionName) =>
        wrapper.TryGetProperty("additionalInfo", out var info)
        && info.ValueKind == JsonValueKind.Object
        && OctorateHttp.StringOf(info, "actionPerformed") is { } performed
        && string.Equals(performed, actionName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A read of a connection operation: the common errors, plus the 550 of the partner.</summary>
    private async Task<JsonDocument> GetConnectionJsonAsync(string pathAndQuery, string operation, CancellationToken ct)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Address(pathAndQuery)), operation, ct, mapError: PartnerFailureOf);
        return ParseJson(answer);
    }
}
