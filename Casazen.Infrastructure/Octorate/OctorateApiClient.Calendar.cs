using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The calendar write and the follow-up of its process of <see cref="IOctorateApiClient"/> (E08/F05/US02/T01, T02, NF-36, D7): the same token, rate-limit gate,
/// error mapping and logs as the rest of the client, no body or id of a room ever logged.
/// </summary>
public sealed partial class OctorateApiClient
{
    /// <summary>The most intervals one bulk call carries.</summary>
    internal const int MaxCalendarUpdatesPerCall = 1_000;

    public async Task<OctorateCalendarUpdateResult> UpdateCalendarAsync(IReadOnlyList<OctorateCalendarUpdate> updates, CancellationToken ct)
    {
        // Nothing leaves for a body CasaZen cannot make: the ids of the rooms are numbers, the intervals are not backwards.
        var json = OctorateCalendarRequests.UpdateBody(updates);

        // The answer is 200 also when Octorate wrote nothing (a room with no license): what it holds is read by the caller, never assumed.
        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address("/rest/v1/calendar/bulk")) { Content = JsonContent(json) },
            "calendar-update",
            ct);
        return OctorateCalendarRequests.ReadUpdateResult(answer.Body, answer.StatusCode) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public async Task<OctorateCalendarProcess> GetCalendarProcessAsync(string processId, CancellationToken ct)
    {
        var process = CheckedId(processId, nameof(processId));
        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Address($"/rest/v1/calendar/process/{Uri.EscapeDataString(process)}")),
            "calendar-process",
            ct);
        return OctorateCalendarRequests.ReadProcess(answer.Body, process) ?? throw new OctorateServerException(answer.StatusCode);
    }
}
