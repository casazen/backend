using System.Text.Encodings.Web;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// The pure rules behind the profile API of the CRM (E07/F01/US03, NF-88): the limits of the tags and how they are stored, how the
/// <c>channel</c> filter is read, how a stay is told to be a single, a couple, a family or a group, and the booking advance. Everything here is
/// a function of its arguments, so the service reads from the database and these rules decide, and the tests reach them without a database.
/// </summary>
public static class CrmProfileRules
{
    /// <summary>Most tags a profile holds (ADR-021-R9).</summary>
    public const int MaxTags = 50;

    /// <summary>Longest tag, in characters.</summary>
    public const int MaxTagLength = 40;

    /// <summary>
    /// Longest text of all the tags together, as stored: the column <c>TagsJson</c> holds 2,000 characters, which 50 tags of 40 characters
    /// do not fit, so the tags together are checked against it (a request that would not fit is refused, never cut).
    /// </summary>
    public const int MaxTagsJsonLength = 2000;

    /// <summary>Most profiles or proposals in one page.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Most stays the detail of a profile lists (the totals count every one).</summary>
    public const int MaxStaysShown = 200;

    /// <summary>Reviews the detail of a profile lists: the latest ones (the count and the average are over all).</summary>
    public const int LatestReviewsShown = 5;

    /// <summary>
    /// How the tags are written to <c>TagsJson</c>: the letters stay as they are (no <c>è</c> for an accented one), so the length of the
    /// column is the length of what the host typed, and the text of a tag is found again by <see cref="TagNeedle"/>.
    /// </summary>
    private static readonly JsonSerializerOptions TagJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---- tags ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The tags stored in <paramref name="tagsJson"/>, trimmed; empty for an empty or unreadable text (a profile made before the tags).</summary>
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

    /// <summary>The text stored in <c>TagsJson</c> for <paramref name="tags"/>.</summary>
    public static string SerializeTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return JsonSerializer.Serialize(tags, TagJson);
    }

    /// <summary>
    /// Checks the tags a host wants to give a profile and returns them trimmed, in the order given. False (and nothing returned) when there
    /// are none at all (<c>null</c>: an empty list is how the tags are cleared), more than <see cref="MaxTags"/>, one that is empty, longer than
    /// <see cref="MaxTagLength"/> or made of control characters, two that differ only in the case (a duplicate), or when all together they
    /// would not fit <see cref="MaxTagsJsonLength"/> characters.
    /// </summary>
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

    /// <summary>
    /// The tags of two profiles that become one: the first profile's, then those of the second that it does not have (the same word in another
    /// case is the same tag), cut where the next one would pass <see cref="MaxTags"/> or <see cref="MaxTagsJsonLength"/>. The cut drops the extra
    /// ones, it never fails a merge.
    /// </summary>
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

    /// <summary>
    /// What a tag looks like inside <c>TagsJson</c>, in lower case: the filter on a tag looks for it in the lower-cased column, so the tag is
    /// found whatever its case, and as a whole word (the quotes are part of the text, <c>"vip"</c> does not find <c>"vip-gold"</c>).
    /// </summary>
    public static string TagNeedle(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return JsonSerializer.Serialize(tag.Trim(), TagJson).ToLowerInvariant();
    }

    // ---- the channel filter -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the <c>channel</c> of the profile filter as a booking source: the name of the source in any case, with or without separators
    /// (<c>booking_com</c>, <c>Booking.com</c> and <c>BookingCom</c> are one). False for anything that is not the name of a source.
    /// </summary>
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

    // ---- the group of a stay ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adults and children of a booking. A booking from a channel often has only the number of guests: with neither adults nor children
    /// given, all of them are adults.
    /// </summary>
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

    /// <summary>
    /// The kind of group the guest usually comes as: the one most of the stays were. With a tie, the kind of the latest stay among those tied.
    /// <c>null</c> without a stay.
    /// </summary>
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

    // ---- the booking advance ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Days between the day the booking was made and the day of the arrival, both on the Europe/Rome calendar. <c>null</c> when the booking
    /// was recorded after the arrival (an import of an old stay): the advance of that stay is not known and is left out of the average.
    /// </summary>
    public static int? AdvanceDays(DateTime bookedAt, DateTime checkIn)
    {
        var days = RomeCalendar.DateInRome(checkIn).DayNumber - RomeCalendar.DateInRome(bookedAt).DayNumber;
        return days < 0 ? null : days;
    }

    /// <summary>The nights of a stay: the days from the check-in to the check-out (dates are midnight UTC of their day).</summary>
    public static int Nights(DateTime checkIn, DateTime checkOut) =>
        Math.Max(0, (int)(checkOut.Date - checkIn.Date).TotalDays);
}
