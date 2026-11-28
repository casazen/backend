using System.Text.Encodings.Web;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public static class CrmProfileRules
{
    public const int MaxTags = 50;

    public const int MaxTagLength = 40;

    public const int MaxTagsJsonLength = 2000;

    public const int MaxPageSize = 100;

    public const int MaxStaysShown = 200;

    public const int LatestReviewsShown = 5;

    private static readonly JsonSerializerOptions TagJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
            return [];

        try
        {
            var tags = JsonSerializer.Deserialize<List<string?>>(tagsJson);
            if (tags is null)
                return [];

            return tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag!.Trim()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return JsonSerializer.Serialize(tags, TagJson);
    }

    public static bool TryNormalizeTags(IEnumerable<string?>? tags, out List<string> normalized)
    {
        normalized = [];
        if (tags is null)
            return false;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            var tag = raw?.Trim();
            if (string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength || tag.Any(char.IsControl) || !seen.Add(tag))
                return false;

            result.Add(tag);
            if (result.Count > MaxTags)
                return false;
        }

        if (SerializeTags(result).Length > MaxTagsJsonLength)
            return false;

        normalized = result;
        return true;
    }

    public static List<string> UnionTags(IEnumerable<string> first, IEnumerable<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in first.Concat(second))
        {
            var tag = raw.Trim();
            if (tag.Length == 0 || !seen.Add(tag))
                continue;

            if (result.Count >= MaxTags)
                break;

            result.Add(tag);
            if (SerializeTags(result).Length > MaxTagsJsonLength)
            {
                result.RemoveAt(result.Count - 1);
                break;
            }
        }

        return result;
    }

    public static string TagNeedle(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return JsonSerializer.Serialize(tag.Trim(), TagJson).ToLowerInvariant();
    }

    public static bool TryParseChannel(string? text, out BookingSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var name = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return name.Length > 0
            && char.IsLetter(name[0])
            && Enum.TryParse(name, ignoreCase: true, out source)
            && Enum.IsDefined(source);
    }

    public static (int Adults, int Children) PartyOf(int adults, int children, int guests)
    {
        if (adults <= 0 && children <= 0 && guests > 0)
            return (guests, 0);

        return (Math.Max(adults, 0), Math.Max(children, 0));
    }

    /// <summary>A family when there is a child; otherwise a group from three adults, a couple for two and a single for one.</summary>
    public static CrmGroupKind GroupOf(int adults, int children)
    {
        if (children > 0)
            return CrmGroupKind.Family;

        return adults switch
        {
            >= 3 => CrmGroupKind.Group,
            2 => CrmGroupKind.Couple,
            _ => CrmGroupKind.Single,
        };
    }

    public static CrmGroupKind? TypicalGroup(IEnumerable<(CrmGroupKind Kind, DateTime CheckIn)> stays)
    {
        ArgumentNullException.ThrowIfNull(stays);

        var kinds = stays
            .GroupBy(stay => stay.Kind)
            .Select(g => (Kind: g.Key, Count: g.Count(), Latest: g.Max(stay => stay.CheckIn)))
            .ToList();
        if (kinds.Count == 0)
            return null;

        return kinds
            .OrderByDescending(kind => kind.Count)
            .ThenByDescending(kind => kind.Latest)
            .ThenBy(kind => kind.Kind)
            .First()
            .Kind;
    }

    public static int? AdvanceDays(DateTime bookedAt, DateTime checkIn)
    {
        var days = RomeCalendar.DateInRome(checkIn).DayNumber - RomeCalendar.DateInRome(bookedAt).DayNumber;
        return days < 0 ? null : days;
    }

    public static int Nights(DateTime checkIn, DateTime checkOut) =>
        Math.Max(0, (int)(checkOut.Date - checkIn.Date).TotalDays);
}
