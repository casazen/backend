using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

public sealed partial class OctorateApiClient
{
    internal const int MaxCalendarUpdatesPerCall = 1_000;

    public async Task<OctorateCalendarUpdateResult> UpdateCalendarAsync(IReadOnlyList<OctorateCalendarUpdate> updates, CancellationToken ct)
    {
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
