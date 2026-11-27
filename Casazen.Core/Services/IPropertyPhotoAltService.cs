using Casazen.Core.DTOs;
using Casazen.Core.SiteProfile;

namespace Casazen.Core.Services;

public sealed record PhotoAltInput(string Url, LocalizedTextInput? Alt, Guid? RoomId = null);

/// <summary>
/// The alternative text and the size of the photos of a property (DB-11a): what a screen reader says in place of the picture and what the
/// page needs to reserve its room. Reads and writes one property of the caller's org (the tenant filter hides another org's property); the
/// permission is the caller's to check. The map follows the gallery, under the same lock as the photo service, so an upload, a deletion and
/// a save of the texts never overwrite one another.
/// </summary>
public interface IPropertyPhotoAltService
{
    Task<IReadOnlyList<PublicPropertyPhotoDto>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the alternative text and the room of the photos listed and <b>clears</b> both for the gallery photos not listed (the editor sends them
    /// all); the size of a photo is never touched. Answers with the gallery as stored, rooms included; <c>null</c> when the property does not exist
    /// for the caller.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <c>property_photo_not_found</c>: an address that is not in the gallery; <c>property_room_not_found</c>: a room that is not one of the property's.
    /// </exception>
    /// <exception cref="Exceptions.DomainRuleException">422 <c>property_site_profile_italian_required</c> / <c>_text_too_long</c> with the <c>field</c> (<c>photos</c>).</exception>
    Task<IReadOnlyList<PublicPropertyPhotoDto>?> SaveAsync(
        Guid propertyId,
        IReadOnlyList<PhotoAltInput> photos,
        CancellationToken cancellationToken = default);
}
