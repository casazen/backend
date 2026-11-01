namespace Casazen.Core.Services;

/// <summary>
/// Photos a client sends with a request for the AI to look at (<see cref="AiImage"/> of an <see cref="AiRequest"/>): base64 images the web
/// app reduced in the browser to about 1,024 pixels (<c>reduce-photo.ts</c>), checked by <c>AiPhotos.Decode</c> before they can reach a
/// provider. Shared by every feature that sends photos to the AI; each one sets how many it accepts.
/// </summary>
public static class AiPhotoLimits
{
    /// <summary>
    /// Largest photo accepted, in bytes. A reduced photo weighs 150 to 300 KB: a vision model reads no more than that, and the request stays
    /// light.
    /// </summary>
    public const int MaxPhotoBytes = 1_500_000;

    /// <summary>
    /// 422: more photos than the endpoint accepts, a photo that is not a JPEG, PNG or WebP image (its content is checked, not only the declared
    /// type), an empty one or one larger than <see cref="MaxPhotoBytes"/>.
    /// </summary>
    public const string InvalidCode = "ai_photos_invalid";

    /// <summary>
    /// Room for the JSON around the photos in the size limit of a request: <c>[RequestSizeLimit]</c> of an endpoint that takes N photos is
    /// <c>N * MaxPhotoBytes * 4 / 3 + RequestOverheadBytes</c> (base64 is 4/3 of the bytes), written as a constant there.
    /// </summary>
    public const long RequestOverheadBytes = 64 * 1024;
}
