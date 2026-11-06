using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Reads the days of the answer of <c>GET /rest/v1/calendar/{acc}</c> (E02/F02/US01/T03). The sandbox runs did not record the shape of this
/// answer (only the values a day holds: <c>availability</c>, <c>price</c>, <c>minstay</c>, <c>maxstay</c>, <c>stopSells</c>, <c>closeToArrival</c>,
/// <c>closeToDeparture</c>, <c>cutOffDays</c>), so the reader accepts the shapes such an answer can have and finds the days in all of them:
/// a list of days that each name their product and their date, a list of products that each hold a list or an object of days, and an object
/// keyed by product that holds days keyed by date. A day needs a date and a product; a record that has neither is not a day.
/// </summary>
/// <remarks>
/// An answer from which no day is read is not an empty calendar, it is an answer the reader could not understand: the client turns it into a
/// failed read (<see cref="OctorateCalendarReader.Read"/> returning nothing), so that nothing is deprecated or blocked on a guess.
/// </remarks>
internal static partial class OctorateCalendarReader
{
    /// <summary>The deepest nesting looked at (data / products / product / days / day).</summary>
    private const int MaxDepth = 8;

    private static readonly string[] ProductNames = ["product", "productId", "roomId"];

    private static readonly string[] ContainerNames =
        ["data", "days", "dates", "calendar", "calendars", "items", "products", "result", "results", "content", "page", "links"];

    /// <summary>The days of the answer. <paramref name="requestedProducts"/> names the product of a day that does not say it when the call asked for one only.</summary>
    internal static List<OctorateCalendarDay> Read(JsonElement root, IReadOnlyCollection<string> requestedProducts)
    {
        ArgumentNullException.ThrowIfNull(requestedProducts);

        var days = new Dictionary<(string Product, DateOnly Date), OctorateCalendarDay>();
        Walk(root, requestedProducts.Count == 1 ? requestedProducts.First() : null, days, depth: 0);
        return [.. days.Values];
    }

    private static void Walk(JsonElement element, string? product, Dictionary<(string, DateOnly), OctorateCalendarDay> days, int depth)
    {
        if (depth > MaxDepth)
            return;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Walk(item, product, days, depth + 1);

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        // A day: it holds its date.
        if (OctorateJson.DateOf(OctorateJson.Text(element, "date", "day")) is { } date)
        {
            AddDay(element, OctorateJson.Text(element, ProductNames) ?? product, date, days);
            return;
        }

        // A product record names itself (`product`, `productId`, or an `id` next to its days); otherwise the product comes from outside.
        var own = OctorateJson.Text(element, ProductNames) ?? OctorateJson.Text(element, "id") ?? product;
        foreach (var member in element.EnumerateObject())
        {
            if (member.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                continue;

            // Days keyed by date: { "2026-11-10": { "availability": 1, ... } }.
            if (member.Value.ValueKind == JsonValueKind.Object && OctorateJson.DateOf(member.Name) is { } keyDate && member.Name.Length == 10)
            {
                AddDay(member.Value, OctorateJson.Text(member.Value, ProductNames) ?? own, keyDate, days);
                continue;
            }

            // Products keyed by id: { "956528": [ ... ] }.
            var next = own is null && IsProductKey(member.Name) ? member.Name : own;
            Walk(member.Value, next, days, depth + 1);
        }
    }

    private static void AddDay(JsonElement record, string? product, DateOnly date, Dictionary<(string, DateOnly), OctorateCalendarDay> days)
    {
        if (product is null)
            return;

        days[(product, date)] = new OctorateCalendarDay(
            product,
            date,
            OctorateJson.Int(record, "availability", "available", "quantity"),
            OctorateJson.Decimal(record, "price"),
            OctorateJson.Bool(record, "stopSells", "stopSell", "stopsell", "closed"),
            OctorateJson.Int(record, "minstay", "minStay", "minimumStay"),
            OctorateJson.Int(record, "maxstay", "maxStay", "maximumStay"),
            OctorateJson.Bool(record, "closeToArrival", "closedToArrival", "cta"),
            OctorateJson.Bool(record, "closeToDeparture", "closedToDeparture", "ctd"));
    }

    /// <summary>A key that can be a product id: letters, digits, dash and underscore, and not one of the words a wrapper uses.</summary>
    private static bool IsProductKey(string name) =>
        ProductKey().IsMatch(name)
        && !ContainerNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        && !DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductKey();
}
