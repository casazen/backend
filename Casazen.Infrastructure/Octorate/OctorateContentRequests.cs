using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The bodies CasaZen writes to the content operations of Octorate and the reading of what it answers (E02/F03/US01/T02, T06, T07). The members of
/// the bodies are the ones the sandbox accepted on 2026-11-04 (<c>kb/plan/octorate-integration-facts.md</c>): no field is invented, and a part whose
/// members were not proven (amenities, cancellation policy) is sent only behind its option. Nothing here logs: a body may hold personal data.
/// </summary>
internal static class OctorateContentRequests
{
    /// <summary>The <c>type</c> Octorate gives the 400 of data it refuses (<c>{"type": "ApiParamsExemption", "nested": [...]}</c>).</summary>
    internal const string ParamsExemptionType = "ApiParamsExemption";

    private const int MaxFieldErrors = 30;

    private const int MaxFieldNameLength = 100;

    private const int MaxFieldMessageLength = 300;

    private const int MaxReasonLength = 300;

    // ─── Bodies ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The body of <c>POST /rest/v2/accommodation</c>: the shape of the sandbox, plus the ISO members and the alternative address of PO-19.</summary>
    internal static string PropertyCreateBody(OctoratePropertyCreate request)
    {
        var body = new JsonObject
        {
            ["name"] = request.Name,
            ["givenName"] = request.ContactFirstName,
            ["familyName"] = request.ContactLastName,
            ["email"] = request.ContactEmail,
            ["phone"] = request.ContactPhone,
            ["country"] = request.Country,
            ["isoCountry"] = request.Country,
            ["city"] = request.City,
            ["zip"] = request.ZipCode,
            ["zipCode"] = request.ZipCode,
            ["address"] = request.Address,
            ["latitude"] = request.Latitude,
            ["longitude"] = request.Longitude,
            ["currency"] = request.Currency,
            ["isoCurrency"] = request.Currency,
            ["propertyCategory"] = "APARTMENT",

            // PO-19: Octorate sends the credentials of the user it creates only to the CasaZen address; the owner gets nothing.
            ["welcome_user"] = false,
            ["welcome_alternative_email"] = request.AlternativeEmail,
            ["location"] = new JsonObject
            {
                ["placeType"] = "ACCOMODATION",
                ["address"] = request.Address,
                ["city"] = request.City,
                ["zipCode"] = request.ZipCode,
                ["country"] = request.Country,
                ["latitude"] = request.Latitude,
                ["longitude"] = request.Longitude,
                ["zoom"] = 13,
            },
            ["contact"] = new JsonObject
            {
                ["phoneNumber"] = request.ContactPhone,
                ["email"] = request.ContactEmail,
                ["givenName"] = request.ContactFirstName,
                ["familyName"] = request.ContactLastName,
            },
        };
        if (!string.IsNullOrWhiteSpace(request.TimeZone))
            body["timeZone"] = request.TimeZone;
        if (!string.IsNullOrWhiteSpace(request.NetworkName))
            body["network_name"] = request.NetworkName;
        if (request.CheckinStart is { } start)
            body["checkinStart"] = start;
        if (request.CheckinEnd is { } end)
            body["checkinEnd"] = end;
        if (request.Checkout is { } checkout)
            body["checkout"] = checkout;

        return body.ToJsonString();
    }

    /// <summary>The body of <c>POST /rest/v3/roomrates/{acc}</c>: the members of the unit the sandbox created with.</summary>
    internal static string RoomCreateBody(string accommodationId, OctorateRoomCreate room)
    {
        var body = new JsonObject
        {
            ["rateName"] = LanguageMap(room.RateNames),
            ["name"] = room.Name,
            ["accommodationId"] = accommodationId,
            ["adults"] = room.Adults,
            ["children"] = 0,
            ["infants"] = 0,
            ["quantity"] = 1,
            ["bedroomQuantity"] = room.Bedrooms,
            ["bedQuantity"] = room.Beds,
            ["bathroomQuantity"] = room.Bathrooms,
            ["notRefundable"] = false,
            ["breakfastIncluded"] = false,
            ["statistic"] = true,
            ["calendar"] = true,
            ["website"] = true,
            ["bookingEngine"] = true,
            ["manualReservations"] = true,
            ["closeNextDays"] = 0,
            ["roomCategory"] = "APARTMENT",
        };
        if (room.AreaSqm is { } area and > 0)
            body["squareMetersSize"] = area;
        if (room.Descriptions.Count > 0)
            body["description"] = LanguageMap(room.Descriptions);

        return body.ToJsonString();
    }

    /// <summary>The body of the descriptions: the description and the house rules for each language, the directions as one text; an empty member is left out.</summary>
    internal static string DescriptionsBody(OctorateDescriptions descriptions)
    {
        var body = new JsonObject();
        if (descriptions.Description.Count > 0)
            body["description"] = LanguageMap(descriptions.Description);
        if (descriptions.HouseRules.Count > 0)
            body["houseRules"] = LanguageMap(descriptions.HouseRules);
        if (!string.IsNullOrWhiteSpace(descriptions.Directions))
            body["directions"] = descriptions.Directions;

        return body.ToJsonString();
    }

    /// <summary>The body of one room of the unit, as the sandbox took it: the kind, the number, the guests and the beds (as the default configuration too).</summary>
    internal static string RoomBody(OctorateContentRoom room)
    {
        var beds = new JsonArray();
        foreach (var bed in room.Beds)
            beds.Add(new JsonObject { ["bedType"] = bed.Type, ["quantity"] = bed.Quantity });

        var configurationBeds = new JsonArray();
        foreach (var bed in room.Beds)
            configurationBeds.Add(new JsonObject { ["bedType"] = bed.Type, ["quantity"] = bed.Quantity });

        return new JsonObject
        {
            ["type"] = room.Type,
            ["privateSpace"] = true,
            ["roomNumber"] = room.RoomNumber,
            ["maxGuests"] = room.MaxGuests,
            ["defaultSubRoomBeds"] = beds,
            ["subRoomConfigurations"] = new JsonArray(new JsonObject { ["defaultConfiguration"] = true, ["beds"] = configurationBeds }),
        }.ToJsonString();
    }

    internal static string AmenitiesBody(IReadOnlyCollection<long> amenityIds)
    {
        var ids = new JsonArray();
        foreach (var id in amenityIds.Distinct().Order())
            ids.Add(id);

        return new JsonObject { ["amenities"] = ids }.ToJsonString();
    }

    internal static string ReservationRulesBody(OctorateReservationRules rules)
    {
        var body = new JsonObject();
        if (rules.ChildrenAllowed is { } children)
            body["childrenAllowed"] = children;
        if (rules.PetsAllowed is { } pets)
            body["petsAllowed"] = pets;
        if (rules.SmokersAllowed is { } smokers)
            body["smokersAllowed"] = smokers;
        if (rules.CheckinTimeStart is { } start)
            body["checkinTimeStart"] = start;
        if (rules.CheckinTimeEnd is { } end)
            body["checkinTimeEnd"] = end;
        if (rules.CheckoutTime is { } checkout)
            body["checkoutTime"] = checkout;

        return body.ToJsonString();
    }

    internal static string CancellationBody(OctorateCancellationPolicy policy)
    {
        var body = new JsonObject();
        if (policy.FullRefundHours is { } full)
            body["fullRefundHours"] = full;
        if (policy.PartialRefundHours is { } partial)
            body["partialRefundHours"] = partial;
        if (policy.PartialRefundPercent is { } percent)
            body["partialRefundPercent"] = percent;

        return body.ToJsonString();
    }

    private static JsonObject LanguageMap(IReadOnlyDictionary<string, string> texts)
    {
        var map = new JsonObject();
        foreach (var (language, text) in texts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            map[language] = text;

        return map;
    }

    // ─── Errors ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The meaning of two refusals of the creation of an accommodation: 412 is a network Octorate does not know; 400 <c>ApiParamsExemption</c> is
    /// data it refuses, with the fields it names. Anything else keeps the generic mapping (<c>null</c>).
    /// </summary>
    internal static OctorateException? TranslateCreateError(int status, string body)
    {
        if (status == StatusCodes.Status412PreconditionFailed)
            return new OctorateNetworkUnknownException();

        if (status == StatusCodes.Status400BadRequest
            && string.Equals(OctorateHttp.ReadError(body).Type, ParamsExemptionType, StringComparison.OrdinalIgnoreCase))
        {
            return new OctoratePropertyRejectedException(ReadFieldErrors(body));
        }

        return null;
    }

    /// <summary>
    /// The fields of an <c>ApiParamsExemption</c>: the <c>machine_name</c> and the message of each entry of <c>nested</c> (or <c>fields</c>,
    /// <c>errors</c>). An answer that names no field gives one entry for the property as a whole, so the person is never left with an empty list.
    /// </summary>
    internal static IReadOnlyList<OctorateFieldError> ReadFieldErrors(string body)
    {
        var errors = new List<OctorateFieldError>();
        string? rootMessage = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            rootMessage = OctorateHttp.StringOf(root, "message");
            foreach (var member in new[] { "nested", "fields", "errors" })
            {
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(member, out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in list.EnumerateArray())
                {
                    var field = OctorateHttp.StringOf(item, "machine_name")
                                ?? OctorateHttp.StringOf(item, "machineName")
                                ?? OctorateHttp.StringOf(item, "field")
                                ?? OctorateHttp.StringOf(item, "name");
                    if (field is null || errors.Count >= MaxFieldErrors)
                        continue;

                    errors.Add(new OctorateFieldError(Cut(field, MaxFieldNameLength)!, Cut(OctorateHttp.StringOf(item, "message"), MaxFieldMessageLength)));
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the property as a whole is the field.
        }

        if (errors.Count == 0)
            errors.Add(new OctorateFieldError("property", Cut(rootMessage, MaxFieldMessageLength)));

        return errors;
    }

    // ─── Answers ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The answer of the creation of an accommodation: <c>codice</c> (an array, or one value, else the first of <c>ids</c>) and the refresh token
    /// (<c>refresh_token</c>, or the one of <c>identity</c>). <c>null</c> when either is missing: the caller then refuses the answer.
    /// </summary>
    internal static OctoratePropertyCreated? ReadCreated(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var id = FirstOf(root, "codice") ?? FirstOf(root, "ids");
            var refresh = OctorateHttp.StringOf(root, "refresh_token");
            if (refresh is null && root.TryGetProperty("identity", out var identity))
                refresh = OctorateHttp.StringOf(identity, "refreshToken");

            return id is null || id.Length > OctorateApiClient.MaxAccommodationIdLength || refresh is null
                ? null
                : new OctoratePropertyCreated(id, refresh);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The product id of a unit just created: <c>[{data: {id}}]</c>, <c>{id}</c> or <c>{data: {id}}</c>, else the last digits of the address of the resource.</summary>
    internal static string? ReadCreatedUnitId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var first = root.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                    return null;

                first.TryGetProperty("data", out var data);
                first.TryGetProperty("resource", out var resource);
                return Bounded(OctorateHttp.StringOf(data, "id") ?? OctorateHttp.StringOf(data, "productId") ?? TrailingNumber(OctorateHttp.StringOf(resource, "href")));
            }

            return root.ValueKind == JsonValueKind.Object
                ? Bounded(OctorateHttp.StringOf(root, "id") ?? OctorateHttp.StringOf(OctorateApiClient.Unwrap(root), "id"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The process a push answered with (<c>processId</c>, else <c>id</c>); <c>null</c> when it names none.</summary>
    internal static string? ReadProcessId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = OctorateApiClient.Unwrap(document.RootElement);
            return Bounded(OctorateHttp.StringOf(root, "processId") ?? OctorateHttp.StringOf(root, "id"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The result of a publication process: its state word, and the first error it lists. A state CasaZen does not know is <see cref="OctorateProcessState.Unknown"/>.</summary>
    internal static OctorateContentProcess ReadProcess(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = OctorateApiClient.Unwrap(document.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
                return new OctorateContentProcess(OctorateProcessState.Unknown, null);

            var word = (OctorateHttp.StringOf(root, "status") ?? OctorateHttp.StringOf(root, "state") ?? OctorateHttp.StringOf(root, "processStatus"))
                ?.Trim().ToUpperInvariant();
            var reason = Cut(
                OctorateHttp.StringOf(root, "message") ?? OctorateHttp.StringOf(root, "error") ?? OctorateHttp.StringOf(root, "description"),
                MaxReasonLength);

            // A list of errors in a finished process is a partial refusal: the channel did not take it all (OCT-05).
            foreach (var member in new[] { "errors", "failures" })
            {
                if (root.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    var first = list.EnumerateArray().First();
                    return new OctorateContentProcess(
                        OctorateProcessState.Failed,
                        Cut(first.ValueKind == JsonValueKind.String ? first.GetString() : OctorateHttp.StringOf(first, "message"), MaxReasonLength) ?? reason);
                }
            }

            return word switch
            {
                "DONE" or "COMPLETED" or "COMPLETE" or "SUCCESS" or "SUCCEEDED" or "OK" or "FINISHED" or "PUBLISHED" =>
                    new OctorateContentProcess(OctorateProcessState.Done, null),
                "ERROR" or "FAILED" or "FAIL" or "FAILURE" or "REFUSED" or "REJECTED" or "KO" =>
                    new OctorateContentProcess(OctorateProcessState.Failed, reason),
                "NEW" or "WAIT" or "WAITING" or "QUEUED" or "PENDING" or "RUNNING" or "PROCESSING" or "IN_PROGRESS" or "STARTED" =>
                    new OctorateContentProcess(OctorateProcessState.Pending, null),
                _ => new OctorateContentProcess(OctorateProcessState.Unknown, null),
            };
        }
        catch (JsonException)
        {
            return new OctorateContentProcess(OctorateProcessState.Unknown, null);
        }
    }

    /// <summary>An entry of the photo queue: its id (<c>id</c>, <c>taskId</c>, <c>queueId</c>), its state word and, once done, the name of the photo. <c>null</c> without an id.</summary>
    internal static OctoratePhotoQueueEntry? ReadPhotoEntry(string body, string? fallbackId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = OctorateApiClient.Unwrap(document.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var id = Bounded(OctorateHttp.StringOf(root, "id") ?? OctorateHttp.StringOf(root, "taskId") ?? OctorateHttp.StringOf(root, "queueId") ?? fallbackId);
            if (id is null)
                return null;

            var word = (OctorateHttp.StringOf(root, "status") ?? OctorateHttp.StringOf(root, "state"))?.Trim().ToUpperInvariant() ?? string.Empty;
            var state = word.Contains("DONE", StringComparison.Ordinal) || word is "OK" or "SUCCESS" or "COMPLETED"
                ? OctoratePhotoQueueState.Done
                : word.Contains("ERR", StringComparison.Ordinal) || word.Contains("FAIL", StringComparison.Ordinal)
                    ? OctoratePhotoQueueState.Error
                    : OctoratePhotoQueueState.Waiting;

            root.TryGetProperty("photo", out var photo);
            var name = OctorateHttp.StringOf(root, "photoName")
                       ?? OctorateHttp.StringOf(root, "fileName")
                       ?? OctorateHttp.StringOf(photo, "name")
                       ?? OctorateHttp.StringOf(root, "name")
                       ?? OctorateHttp.StringOf(root, "result");
            var reason = state == OctoratePhotoQueueState.Error
                ? Cut(OctorateHttp.StringOf(root, "message") ?? OctorateHttp.StringOf(root, "error") ?? OctorateHttp.StringOf(root, "errorMessage") ?? word, MaxReasonLength)
                : null;
            return new OctoratePhotoQueueEntry(id, state, state == OctoratePhotoQueueState.Done ? Bounded(name) : null, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The descriptions of a unit: the language maps of the description and of the house rules, and the directions (a text, or a map of which the Italian one is taken).</summary>
    internal static OctorateDescriptions ReadDescriptions(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = OctorateApiClient.Unwrap(document.RootElement);
        string? directions = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("directions", out var directionsElement))
        {
            directions = directionsElement.ValueKind == JsonValueKind.String
                ? OctorateHttp.StringOf(root, "directions")
                : ReadLanguageMap(directionsElement) is { Count: > 0 } map ? map.GetValueOrDefault("IT") ?? map.Values.First() : null;
        }

        return new OctorateDescriptions
        {
            Description = ReadLanguageMap(Member(root, "description")),
            HouseRules = ReadLanguageMap(Member(root, "houseRules")),
            Directions = directions,
        };
    }

    /// <summary>The rooms of a unit with their beds: an array of <c>{index, room: {...}}</c> or of rooms; the beds of <c>defaultSubRoomBeds</c>, else of the first configuration.</summary>
    internal static IReadOnlyList<OctorateContentRoom> ReadContentRooms(string body)
    {
        using var document = JsonDocument.Parse(body);
        var rooms = new List<OctorateContentRoom>();
        foreach (var item in OctorateApiClient.Items(document.RootElement))
        {
            var room = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("room", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : item;
            if (room.ValueKind != JsonValueKind.Object)
                continue;

            var beds = ReadBeds(room);
            rooms.Add(new OctorateContentRoom(
                OctorateHttp.StringOf(room, "type") ?? string.Empty,
                OctorateApiClient.IntOf(room, "roomNumber") ?? OctorateApiClient.IntOf(item, "index") ?? 0,
                OctorateApiClient.IntOf(room, "maxGuests") ?? 0,
                beds));
        }

        return [.. rooms.OrderBy(r => r.RoomNumber).ThenBy(r => r.Type, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<OctorateContentBed> ReadBeds(JsonElement room)
    {
        JsonElement list = default;
        if (room.TryGetProperty("defaultSubRoomBeds", out var direct) && direct.ValueKind == JsonValueKind.Array)
        {
            list = direct;
        }
        else if (room.TryGetProperty("subRoomConfigurations", out var configurations)
                 && configurations.ValueKind == JsonValueKind.Array
                 && configurations.GetArrayLength() > 0
                 && configurations[0].TryGetProperty("beds", out var configured)
                 && configured.ValueKind == JsonValueKind.Array)
        {
            list = configured;
        }

        if (list.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. list.EnumerateArray()
                .Select(bed => new OctorateContentBed(
                    OctorateHttp.StringOf(bed, "bedType") ?? string.Empty,
                    OctorateApiClient.IntOf(bed, "quantity") ?? 0))
                .Where(bed => bed.Type.Length > 0)
                .OrderBy(bed => bed.Type, StringComparer.Ordinal),
        ];
    }

    private static JsonElement Member(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;

    /// <summary>A map of language to text, the language upper-cased; entries that are not a text are left out.</summary>
    private static Dictionary<string, string> ReadLanguageMap(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                map[property.Name.Trim().ToUpperInvariant()] = text.Trim();
        }

        return map;
    }

    /// <summary>The first value of a member that is an array, a number or a text (a text of several values separated by commas gives its first).</summary>
    private static string? FirstOf(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(ScalarText).FirstOrDefault(text => text is not null),
            JsonValueKind.String => ScalarText(value)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() is { Length: > 0 } text ? text.Trim() : null,
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    private static string? TrailingNumber(string? text)
    {
        if (text is null)
            return null;

        var end = text.TrimEnd('/');
        var start = end.Length;
        while (start > 0 && char.IsAsciiDigit(end[start - 1]))
            start--;

        return start < end.Length ? end[start..] : null;
    }

    private static string? Bounded(string? id) =>
        id is { Length: > 0 } && id.Length <= OctorateApiClient.MaxAccommodationIdLength ? id : null;

    private static string? Cut(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
