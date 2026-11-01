using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Limits and codes of «Suggerisci un testo» of the wizard «Aggiungi immobile» (the headline and the description of a listing written by the
/// AI from the photos, the comune and the data of the property).
/// </summary>
public static class ListingTextLimits
{
    /// <summary>Photos the AI looks at: the cover and the next ones of the gallery, in its order.</summary>
    public const int MaxPhotos = 6;

    /// <summary>
    /// Largest photo accepted, in bytes. The web app sends the photos reduced to about 1,024 pixels (150 to 300 KB): a vision model reads no
    /// more than that, and the request stays light.
    /// </summary>
    public const int MaxPhotoBytes = 1_500_000;

    /// <summary>Upper bound of one request: the photos in base64 (4/3 of their size) and the JSON around them.</summary>
    public const long MaxRequestBytes = (long)MaxPhotos * MaxPhotoBytes * 4 / 3 + 64 * 1024;

    /// <summary>The headline: the limit of the field of the listing.</summary>
    public const int TitleMaxLength = PropertyHouseRules.ListingTitleMaxLength;

    /// <summary>The description: the limit of the field of the property.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>
    /// 422: more than <see cref="MaxPhotos"/> photos, a photo that is not a JPEG, PNG or WebP image (its content is checked, not only the
    /// declared type), an empty one or one larger than <see cref="MaxPhotoBytes"/>.
    /// </summary>
    public const string PhotosInvalidCode = "listing_text_photos_invalid";
}

/// <summary>Why the suggestion is not the AI's: the client then composes the text from the data the host entered, as before.</summary>
public static class ListingTextFallbackReasons
{
    /// <summary>No external AI provider is active (<c>Ai:Provider</c> is the stub, or there is no key).</summary>
    public const string AiNotConfigured = "ai_not_configured";

    /// <summary>The platform's monthly AI budget cannot cover the call.</summary>
    public const string AiBudgetExhausted = "ai_budget_exhausted";

    /// <summary>The provider did not answer (error, timeout).</summary>
    public const string AiUnavailable = "ai_unavailable";

    /// <summary>The provider answered with something that is not a headline and a description.</summary>
    public const string AiInvalidAnswer = "ai_invalid_answer";
}

/// <summary>What a suggestion was written from, for the line the host reads under it («Basato su: 6 foto, Como, 3 annunci simili»).</summary>
/// <param name="Photos">Photos the AI looked at (0 when none was sent or the provider reads text only).</param>
/// <param name="Comune">The comune of the property, as the official list names it; <c>null</c> when the property has none.</param>
/// <param name="Province">Its province (plate code, <c>CO</c>).</param>
/// <param name="Region">Its region.</param>
/// <param name="SimilarListings">Listings published on CasaZen in the same comune that the AI compared.</param>
/// <param name="Amenities">Amenities of the property the AI was told about.</param>
public sealed record ListingTextBasis(int Photos, string? Comune, string? Province, string? Region, int SimilarListings, int Amenities);

/// <summary>A headline and a description for the listing, or why there is none.</summary>
/// <param name="AiUsed">True when the AI wrote <paramref name="Title"/> and <paramref name="Description"/>.</param>
/// <param name="Reason">One of <see cref="ListingTextFallbackReasons"/> when <paramref name="AiUsed"/> is false.</param>
/// <param name="Title">At most <see cref="ListingTextLimits.TitleMaxLength"/> characters; <c>null</c> without the AI.</param>
/// <param name="Description">At most <see cref="ListingTextLimits.DescriptionMaxLength"/> characters, paragraphs separated by a blank line.</param>
/// <param name="BasedOn">What the text was (or would have been) written from.</param>
public sealed record ListingTextSuggestion(bool AiUsed, string? Reason, string? Title, string? Description, ListingTextBasis BasedOn);

/// <summary>
/// «Suggerisci un testo» (the wizard «Aggiungi immobile»): the AI writes the headline and the description of a listing from the photos the host
/// uploaded, the comune (never the address), the data of the property and the listings published on CasaZen in the same comune. The host reads
/// it, changes it and saves it: nothing is written to the property here. When no AI is available the answer says why and the client composes
/// the text from the data, as it did before.
/// </summary>
public interface IPropertyListingTextService
{
    /// <summary>
    /// Writes a suggestion for <paramref name="property"/> (already authorized by the caller). <paramref name="photos"/> are the reduced
    /// photos of the gallery, cover first. An answer of the AI for the same data and photos is reused for a while, unless
    /// <paramref name="regenerate"/> asks for a new text.
    /// </summary>
    Task<ListingTextSuggestion> SuggestAsync(
        Property property,
        IReadOnlyList<AiImage> photos,
        bool regenerate,
        CancellationToken cancellationToken = default);
}
