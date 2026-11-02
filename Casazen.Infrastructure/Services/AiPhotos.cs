using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The photos a client sends for the AI to look at (<see cref="AiPhotoLimits"/>): base64 images the web app reduced in the browser. Each one
/// is checked on its content (JPEG, PNG or WebP, <see cref="ImageSignature"/>), not on the type the client declares, and on its size, before
/// it can reach an AI provider. Used by every endpoint that sends photos to the AI (the text of a listing first).
/// </summary>
public static class AiPhotos
{
    /// <summary>The images of <paramref name="photos"/> (base64, with or without a <c>data:</c> prefix), in order.</summary>
    /// <param name="photos">The photos as the client sent them.</param>
    /// <param name="maxPhotos">How many the endpoint accepts.</param>
    /// <exception cref="DomainRuleException">
    /// 422 <see cref="AiPhotoLimits.InvalidCode"/>: more than <paramref name="maxPhotos"/>, not base64, empty, larger than
    /// <see cref="AiPhotoLimits.MaxPhotoBytes"/> or not an image of a supported format.
    /// </exception>
    public static IReadOnlyList<AiImage> Decode(IReadOnlyList<string> photos, int maxPhotos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count > maxPhotos)
            throw Invalid();

        var images = new List<AiImage>(photos.Count);
        foreach (var photo in photos)
        {
            var data = StripDataPrefix(photo ?? string.Empty);
            // A base64 text of n characters holds at most 3n/4 bytes: a longer text cannot be within the limit.
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
