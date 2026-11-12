using System.Globalization;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The body of the bulk write of the calendar and the reading of its answer and of the process it starts (E08/F05/US02/T01, T02, NF-36, D7): the names are
/// the ones of the OpenAPI (<c>CalendarBulkRequest</c>, <c>CalendarBulkValues</c>) and of the Postman collection (<c>GET /rest/v1/calendar/process/{id}</c>).
/// Pure: nothing here calls Octorate.
/// </summary>
internal static class OctorateCalendarRequests
{
    /// <summary>The longest interval of one element of the bulk call, in days.</summary>
    private const int MaxIntervalDays = 366;

    /// <summary>
    /// The JSON array of the bulk call: for each update <c>{ room, dateFrom, dateTo, values }</c>, with the members of the values that are not set left out
    /// and the dates as <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <exception cref="ArgumentException">No update, too many, a room that is not a number, an interval that is backwards or too long, or values with nothing to write.</exception>
    internal static string UpdateBody(IReadOnlyList<OctorateCalendarUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
            throw new ArgumentException("A write of the calendar has at least one interval.", nameof(updates));

        if (updates.Count > OctorateApiClient.MaxCalendarUpdatesPerCall)
            throw new ArgumentException($"A write of the calendar has at most {OctorateApiClient.MaxCalendarUpdatesPerCall} intervals.", nameof(updates));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var update in updates)
            {
                ArgumentNullException.ThrowIfNull(update);
                if (!long.TryParse(update.RoomId?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var room) || room <= 0)
                    throw new ArgumentException("The room of the calendar is a number.", nameof(updates));

                if (update.To < update.From || update.Days > MaxIntervalDays)
                    throw new ArgumentException($"An interval of the calendar is 1 to {MaxIntervalDays} days.", nameof(updates));

                if (update.Values is null || update.Values.IsEmpty)
                    throw new ArgumentException("An interval of the calendar has a value to write.", nameof(updates));

                writer.WriteStartObject();
                writer.WriteNumber("room", room);
                writer.WriteString("dateFrom", Day(update.From));
                writer.WriteString("dateTo", Day(update.To));
                writer.WritePropertyName("values");
                WriteValues(writer, update.Values);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValues(Utf8JsonWriter writer, OctorateCalendarValues values)
    {
        writer.WriteStartObject();
        if (values.Availability is { } availability)
            writer.WriteNumber("availability", availability);

        if (values.Price is { } price)
            writer.WriteNumber("price", price);

        if (values.MinStay is { } minStay)
            writer.WriteNumber("minstay", minStay);

        if (values.MaxStay is { } maxStay)
            writer.WriteNumber("maxstay", maxStay);

        if (values.StopSells is { } stopSells)
            writer.WriteBoolean("stopSells", stopSells);

        if (values.CloseToArrival is { } closeToArrival)
            writer.WriteBoolean("closeToArrival", closeToArrival);

        if (values.CloseToDeparture is { } closeToDeparture)
            writer.WriteBoolean("closeToDeparture", closeToDeparture);

        if (values.CutOffDays is { } cutOffDays)
            writer.WriteNumber("cutOffDays", cutOffDays);

        writer.WriteEndObject();
    }

    /// <summary>
    /// The answer of the bulk call: <c>{ "success": true, "process": [ids] }</c>. An answer that says <c>success: false</c> (Octorate's way of saying it found
    /// no access to the room, or no such room) is a refusal of the request (<see cref="OctorateRequestRejectedException"/>); one that is not that object, or
    /// that does not say <c>success: true</c>, is an answer that cannot be read (<c>null</c>), never an acceptance. A <c>process</c> that is empty or absent is
    /// a result with no id: Octorate wrote nothing (a room with no license answers so).
    /// </summary>
    internal static OctorateCalendarUpdateResult? ReadUpdateResult(string? body, int status)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success))
                return null;

            if (success.ValueKind == JsonValueKind.False)
                throw new OctorateRequestRejectedException(status);

            if (success.ValueKind != JsonValueKind.True)
                return null;

            var ids = new List<string>();
            if (root.TryGetProperty("process", out var process))
            {
                if (process.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in process.EnumerateArray())
                    {
                        if (OctorateJson.TextOf(item) is { } id)
                            ids.Add(id);
                    }
                }
                else if (OctorateJson.TextOf(process) is { } single)
                {
                    ids.Add(single);
                }
            }

            return new OctorateCalendarUpdateResult(ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The process of a bulk call as <c>GET /rest/v1/calendar/process/{id}</c> answers: <c>status</c>, and <c>sent[]</c> with the <c>date</c> and the
    /// <c>status</c> of each date. <c>null</c> for an answer that is not an object, and for one that carries an <c>error</c> (Octorate could not load the process:
    /// the state is not known, which is not the state of the dates).
    /// </summary>
    internal static OctorateCalendarProcess? ReadProcess(string? body, string id)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (OctorateJson.Text(root, "error") is not null)
                return null;

            var dates = new List<OctorateCalendarProcessDate>();
            if (root.TryGetProperty("sent", out var sent) && sent.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sent.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    DateOnly? date = DateOnly.TryParseExact(OctorateJson.Text(item, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed
                        : null;
                    dates.Add(new OctorateCalendarProcessDate(date, OctorateJson.Text(item, "status")));
                }
            }

            return new OctorateCalendarProcess(id, OctorateJson.Text(root, "status"), dates);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
