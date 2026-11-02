using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.DTOs;

namespace Casazen.Core.SiteProfile;

/// <summary>
/// What is known of one photo of the gallery besides its address (DB-11a): the text read instead of the picture (alternative text, Italian
/// always, English if the host wrote it) and its size in pixels as the browser draws it, so the page reserves the room before the photo
/// arrives.
/// </summary>
/// <param name="Alt">The alternative text; <c>null</c> = the host wrote none.</param>
/// <param name="Width">Width in pixels, read from the file when it was uploaded; <c>null</c> = not known (a photo uploaded before this existed).</param>
/// <param name="Height">Height in pixels, as above.</param>
public sealed record PhotoMeta(LocalizedText? Alt = null, int? Width = null, int? Height = null)
{
    public bool IsEmpty => Alt is null && Width is null && Height is null;
}

/// <summary>
/// Reads and writes <c>Properties.PhotoAltJson</c>: a JSON object from the address of a photo to its <see cref="PhotoMeta"/>
/// (<c>{ "https://…/a.jpg": { "it": "…", "en": "…", "w": 1600, "h": 1067 } }</c>). The map follows the gallery (<c>PhotoUrls</c>): a photo that
/// leaves it leaves the map, and a photo never listed simply has no entry. Reading is forgiving: a column that cannot be read is an empty map.
/// </summary>
public static class PropertyPhotoMeta
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyDictionary<string, PhotoMeta> Empty { get; } = new Dictionary<string, PhotoMeta>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, PhotoMeta> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, Options);
            var map = new Dictionary<string, PhotoMeta>(StringComparer.Ordinal);
            foreach (var (url, entry) in stored ?? [])
            {
                if (string.IsNullOrWhiteSpace(url) || entry is null)
                    continue;

                var alt = string.IsNullOrWhiteSpace(entry.It)
                    ? null
                    : new LocalizedText(entry.It, string.IsNullOrWhiteSpace(entry.En) ? null : entry.En);
                var meta = new PhotoMeta(alt, Positive(entry.W), Positive(entry.H));
                if (!meta.IsEmpty)
                    map[url] = meta;
            }

            return map;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>The text to store: <c>null</c> when nothing is known of any photo, so the column stays empty.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, PhotoMeta> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var stored = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (url, meta) in map)
        {
            if (!meta.IsEmpty)
                stored[url] = new Entry { It = meta.Alt?.It, En = meta.Alt?.En, W = meta.Width, H = meta.Height };
        }

        return stored.Count == 0 ? null : JsonSerializer.Serialize(stored, Options);
    }

    /// <summary>
    /// The photos of the gallery, in its order, each with what is known of it: the form the public page and the host editor read. A photo
    /// with no entry has no alternative text and no size.
    /// </summary>
    public static IReadOnlyList<PublicPropertyPhotoDto> Of(IEnumerable<string> galleryUrls, string? json)
    {
        ArgumentNullException.ThrowIfNull(galleryUrls);

        var map = Parse(json);
        return
        [
            .. galleryUrls.Select(url => map.TryGetValue(url, out var meta)
                ? new PublicPropertyPhotoDto(url, meta.Alt, meta.Width, meta.Height)
                : new PublicPropertyPhotoDto(url, null, null, null)),
        ];
    }

    /// <summary>The stored map with the size of the photos just uploaded (their texts untouched).</summary>
    public static string? WithSizes(string? json, IReadOnlyDictionary<string, (int Width, int Height)> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        var map = new Dictionary<string, PhotoMeta>(Parse(json), StringComparer.Ordinal);
        foreach (var (url, size) in sizes)
            map[url] = (map.TryGetValue(url, out var current) ? current : new PhotoMeta()) with { Width = size.Width, Height = size.Height };

        return Serialize(map);
    }

    /// <summary>The stored map without a photo that left the gallery.</summary>
    public static string? Without(string? json, string url)
    {
        var map = new Dictionary<string, PhotoMeta>(Parse(json), StringComparer.Ordinal);
        map.Remove(url);
        return Serialize(map);
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private sealed class Entry
    {
        public string? It { get; set; }

        public string? En { get; set; }

        public int? W { get; set; }

        public int? H { get; set; }
    }
}
