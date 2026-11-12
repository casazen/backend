using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Photos;

/// <summary>
/// Which photos of a property go to the channel manager (E02/F03/US01/T02, NF-32, ADR-036): the photos of the listing are already public, GUID-named
/// objects of the public bucket (<c>Property.PhotoUrls</c>, <see cref="StorageKeys.PropertyPhoto"/>), so the channel manager downloads them from
/// their own address, and there is no photo id, no token endpoint and no second copy.
/// </summary>
/// <remarks>
/// Only an entry that is an object of the <b>property's own</b> photo folder is sent: the test <c>PropertyPhotoService.DeleteAsync</c> applies before
/// it deletes an object. A legacy relative path, an external address, a photo of another property and anything that is not an object of the public
/// bucket (a document lives in the private bucket and is never a URL) are skipped, so a gallery can never make CasaZen send what is not a
/// photo of its listing. The address must be an absolute https one: the channel manager does not follow anything else.
/// </remarks>
public sealed class PublicPhotoUrlService(IFileStorage storage)
{
    /// <summary>The longest address that is kept (the column of <see cref="PhotoUploadTask.PhotoUrl"/>).</summary>
    public const int MaxUrlLength = PhotoUploadTask.PhotoUrlMaxLength;

    /// <summary>The photos of the gallery that are sent, in the order of the gallery (the cover first), each once.</summary>
    public IReadOnlyList<string> ListingPhotoUrls(Guid propertyId, IEnumerable<string>? photoUrls)
    {
        if (photoUrls is null)
            return [];

        var folder = StorageKeys.PropertyPhoto(propertyId, string.Empty);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in photoUrls)
        {
            var url = candidate?.Trim();
            if (string.IsNullOrEmpty(url)
                || url.Length > MaxUrlLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var key = storage.TryGetPublicKey(url);
            if (key is null || !key.StartsWith(folder, StringComparison.Ordinal) || key.Length == folder.Length)
                continue;

            if (seen.Add(url))
                urls.Add(url);
        }

        return urls;
    }

    /// <summary>The photos of <paramref name="property"/> that are sent.</summary>
    public IReadOnlyList<string> ListingPhotoUrls(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ListingPhotoUrls(property.Id, property.PhotoUrls);
    }
}
