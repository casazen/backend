using Casazen.Core.SiteProfile;

namespace Casazen.Core.Services;

/// <summary>
/// The site profile of a property (DB-11a): the optional facts and texts the property page of the booking site shows when the host
/// filled them in. Reads and writes one property of the caller's org (the tenant filter hides another org's property, so it is simply
/// not found); the permission to do so is the caller's to check (<c>PropertySiteProfileController</c>).
/// </summary>
public interface IPropertySiteProfileService
{
    /// <summary>The profile as the host editor shows it; <c>null</c> when the property does not exist for the caller.</summary>
    Task<SitePropertyProfileView?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the profile with <paramref name="input"/> (whatever it leaves out is cleared) and answers with what is stored;
    /// <c>null</c> when the property does not exist for the caller.
    /// </summary>
    /// <remarks>
    /// Since NF-31 the save also carries the texts of the listing in the other languages (<see cref="SitePropertyProfileInput.Translations"/>).
    /// Every text a person saves is <see cref="Entities.Enums.TextOrigin.UserWritten"/>, or
    /// <see cref="Entities.Enums.TextOrigin.ModifiedAfterTranslation"/> when it changes a machine translation: no input of this call can
    /// carry <see cref="Entities.Enums.TextOrigin.AutoTranslated"/>.
    /// </remarks>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>property_not_bookable_in_long_mode</c> (a long-rent property has no page on the booking site) or one of
    /// <see cref="SitePropertyProfileErrorCodes"/> with the <c>field</c> that broke the rule. Nothing is saved.
    /// </exception>
    Task<SitePropertyProfileView?> SaveAsync(Guid propertyId, SitePropertyProfileInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// The call a machine translation will use (NF-31, AI-02, not built yet): adds the translated texts to the listing as
    /// <see cref="Entities.Enums.TextOrigin.AutoTranslated"/>, where nothing is written or over an earlier machine translation, and leaves every
    /// text a person wrote or edited exactly as it is. The rest of the profile is not touched. It is not reachable from any endpoint.
    /// <c>null</c> when the property does not exist for the caller.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>property_not_bookable_in_long_mode</c>, <c>property_site_profile_language_unknown</c> or
    /// <c>property_site_profile_text_too_long</c>. Nothing is saved.
    /// </exception>
    Task<SitePropertyProfileView?> SaveAutoTranslationsAsync(
        Guid propertyId,
        IReadOnlyDictionary<string, ListingTextsInput> translated,
        CancellationToken cancellationToken = default);
}
