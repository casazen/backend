using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The content operations of <see cref="IOctorateApiClient"/> (E02/F03/US01/T02, T06, T07): the creation of an accommodation and of its unit, the
/// groups of the content of a unit, the push to a channel, the photos and the result of a publication. One method for each operation, with the
/// same token, rate-limit gate, error mapping and logs as the rest of the client: no body, token or address of a photo is ever logged.
/// </summary>
public sealed partial class OctorateApiClient
{
    /// <summary>The longest address of a photo CasaZen sends (the column of <c>PhotoUploadTask</c>).</summary>
    private const int MaxPhotoUrlLength = 1000;

    public async Task<OctoratePropertyCreated> CreatePropertyAsync(OctoratePropertyCreate request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = OctorateContentRequests.PropertyCreateBody(request);

        // The accommodation does not exist yet, so the partner token is the one that can create it; the answer carries the refresh token, once.
        var answer = await SendAsPlatformAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address("/rest/v2/accommodation")) { Content = JsonContent(json) },
            "accommodation-create",
            ct,
            OctorateContentRequests.TranslateCreateError);
        return OctorateContentRequests.ReadCreated(answer.Body) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public async Task<string> CreateRoomAsync(string accommodationId, OctorateRoomCreate room, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(room);
        var accommodation = CheckedAccommodationId(accommodationId);
        var json = OctorateContentRequests.RoomCreateBody(accommodation, room);
        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address($"/rest/v3/roomrates/{Uri.EscapeDataString(accommodation)}")) { Content = JsonContent(json) },
            "room-create",
            ct);
        return OctorateContentRequests.ReadCreatedUnitId(answer.Body) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public Task UpdateDescriptionsAsync(string accommodationId, string productId, OctorateDescriptions descriptions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        if (descriptions.IsEmpty)
            throw new ArgumentException("A write of the descriptions sets at least one member.", nameof(descriptions));

        return PostContentAsync(accommodationId, productId, "descriptions", OctorateContentRequests.DescriptionsBody(descriptions), "content-descriptions", ct);
    }

    public async Task<OctorateDescriptions?> GetDescriptionsAsync(string accommodationId, string productId, CancellationToken ct)
    {
        var answer = await GetContentAsync(accommodationId, productId, "descriptions", "content-descriptions-read", ct);
        if (answer is null)
            return null;

        try
        {
            return OctorateContentRequests.ReadDescriptions(answer.Body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new OctorateServerException(answer.StatusCode, ex);
        }
    }

    public async Task UpdateRoomsAsync(string accommodationId, string productId, IReadOnlyList<OctorateContentRoom> rooms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        // One call for each room: the room number tells them apart.
        foreach (var room in rooms)
            await PostContentAsync(accommodationId, productId, "rooms", OctorateContentRequests.RoomBody(room), "content-rooms", ct);
    }

    public async Task<IReadOnlyList<OctorateContentRoom>> GetContentRoomsAsync(string accommodationId, string productId, CancellationToken ct)
    {
        var answer = await GetContentAsync(accommodationId, productId, "rooms", "content-rooms-read", ct);
        if (answer is null)
            return [];

        try
        {
            return OctorateContentRequests.ReadContentRooms(answer.Body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new OctorateServerException(answer.StatusCode, ex);
        }
    }

    public Task UpdateAmenitiesAsync(string accommodationId, string productId, IReadOnlyCollection<long> amenityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(amenityIds);
        if (amenityIds.Count == 0)
            throw new ArgumentException("A write of the amenities names at least one.", nameof(amenityIds));

        return PostContentAsync(accommodationId, productId, "amenities", OctorateContentRequests.AmenitiesBody(amenityIds), "content-amenities", ct);
    }

    public Task UpdateReservationRulesAsync(string accommodationId, string productId, OctorateReservationRules rules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.IsEmpty)
            throw new ArgumentException("A write of the rules sets at least one member.", nameof(rules));

        return PostContentAsync(accommodationId, productId, "reservation", OctorateContentRequests.ReservationRulesBody(rules), "content-reservation", ct);
    }

    public Task UpdateCancellationPolicyAsync(string accommodationId, string productId, OctorateCancellationPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return PostContentAsync(accommodationId, productId, "cancellation", OctorateContentRequests.CancellationBody(policy), "content-cancellation", ct);
    }

    public async Task<OctorateChannelPush> PushChannelAsync(string accommodationId, string productId, OctorateChannelTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var accommodation = CheckedAccommodationId(accommodationId);
        var portal = target.PortalCode?.Trim();
        if (string.IsNullOrEmpty(portal) || portal.Length > 50)
            throw new ArgumentException("A portal code has 1 to 50 characters.", nameof(target));

        string path;
        if (target.UsesQueue)
        {
            var product = CheckedId(productId, nameof(productId));
            path = $"/rest/v1/content/products/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(product)}/pushQueue/{Uri.EscapeDataString(portal)}";
        }
        else
        {
            path = $"/rest/v1/content/connections/{CheckedConnectionId(target.ConnectionId ?? string.Empty)}/push";
        }

        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Address(path)), "content-push", ct);
        return new OctorateChannelPush(OctorateContentRequests.ReadProcessId(answer.Body));
    }

    public async Task<OctoratePhotoQueueEntry> UploadPhotoAsync(string accommodationId, string photoUrl, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        if (string.IsNullOrWhiteSpace(photoUrl)
            || photoUrl.Length > MaxPhotoUrlLength
            || !Uri.TryCreate(photoUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A photo is sent by its absolute https address.", nameof(photoUrl));
        }

        // The address goes in a form body: in the query the sandbox answered 500 (S25). The answer is 202 with the entry of the queue.
        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address($"/rest/v1/accommodation/{Uri.EscapeDataString(accommodation)}/photos"))
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("url", photoUrl)]),
            },
            "photo-upload",
            ct);
        return OctorateContentRequests.ReadPhotoEntry(answer.Body) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public async Task DeletePhotoAsync(string accommodationId, string photoName, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var name = CheckedId(photoName, nameof(photoName));
        try
        {
            await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Delete,
                    Address($"/rest/v1/accommodation/{Uri.EscapeDataString(accommodation)}/photos/{Uri.EscapeDataString(name)}")),
                "photo-delete",
                ct);
        }
        catch (OctorateNotFoundException)
        {
            // The photo is not there any more: what was asked is done.
        }
    }

    public async Task<OctoratePhotoQueueEntry?> GetPhotoQueueAsync(string accommodationId, string queueId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var queue = CheckedId(queueId, nameof(queueId));
        OctorateAnswer answer;
        try
        {
            answer = await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    Address($"/rest/v1/accommodation/{Uri.EscapeDataString(accommodation)}/queue/{Uri.EscapeDataString(queue)}")),
                "photo-queue",
                ct);
        }
        catch (OctorateNotFoundException)
        {
            return null;
        }

        return OctorateContentRequests.ReadPhotoEntry(answer.Body, queue) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public async Task<OctorateContentProcess> GetContentProcessAsync(string accommodationId, string processId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var process = CheckedId(processId, nameof(processId));
        var answer = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                Address($"/rest/v1/content/products/{Uri.EscapeDataString(accommodation)}/process/{Uri.EscapeDataString(process)}")),
            "content-process",
            ct);
        return OctorateContentRequests.ReadProcess(answer.Body);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>A write of one part of the content of a unit: <c>POST /rest/v1/content/products/{acc}/{pid}/{part}</c> with a JSON body.</summary>
    private Task PostContentAsync(string accommodationId, string productId, string part, string json, string operation, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var product = CheckedId(productId, nameof(productId));
        return SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                Address($"/rest/v1/content/products/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(product)}/{part}"))
            {
                Content = JsonContent(json),
            },
            operation,
            ct);
    }

    /// <summary>A read of one part of the content of a unit; <c>null</c> when Octorate holds nothing there yet (404: <c>ApiResourceEmpty</c>, <c>ApiResourceMissing</c>).</summary>
    private async Task<OctorateAnswer?> GetContentAsync(string accommodationId, string productId, string part, string operation, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var product = CheckedId(productId, nameof(productId));
        try
        {
            return await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    Address($"/rest/v1/content/products/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(product)}/{part}")),
                operation,
                ct);
        }
        catch (OctorateNotFoundException)
        {
            return null;
        }
    }
}
