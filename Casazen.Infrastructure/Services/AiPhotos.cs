using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public static class AiPhotos
{
    public static IReadOnlyList<AiImage> Decode(IReadOnlyList<string> photos, int maxPhotos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count > maxPhotos)
            throw Invalid();

        var images = new List<AiImage>(photos.Count);
        foreach (var photo in photos)
        {
            var data = StripDataPrefix(photo ?? string.Empty);
            if (data.Length == 0 || data.Length > (AiPhotoLimits.MaxPhotoBytes + 2) / 3 * 4)
                throw Invalid();

            var buffer = new byte[data.Length * 3 / 4 + 3];
            if (!Convert.TryFromBase64String(data, buffer, out var length) || length == 0 || length > AiPhotoLimits.MaxPhotoBytes)
                throw Invalid();

            var bytes = buffer.AsSpan(0, length).ToArray();
            var mediaType = ImageSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, ImageSignature.HeaderLength)));
            if (mediaType is null)
                throw Invalid();

            images.Add(new AiImage(mediaType, bytes));
        }

        return images;
    }

    private static string StripDataPrefix(string value)
    {
        var text = value.Trim();
        var comma = text.IndexOf(',');
        return text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? text[(comma + 1)..] : text;
    }

    private static DomainRuleException Invalid() => new(AiPhotoLimits.InvalidCode, "AiPhotosInvalid");
}
