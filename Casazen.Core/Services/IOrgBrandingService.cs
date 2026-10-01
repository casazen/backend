using Casazen.Core.Branding;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Writes the public-site branding of an org (BK-12, A3-17): primary color, theme, tagline, logo and hero image.
/// Values are stored on <see cref="Org"/> and read anonymously through <c>PublicOrgDto</c> and
/// <c>ResolveHostBrandingDto</c>. Every method returns <c>null</c> when the org does not exist and throws
/// <c>DomainRuleException</c> (codes in <see cref="OrgBrandingRules"/>) for an invalid value; nothing is changed then.
/// </summary>
public interface IOrgBrandingService
{
    /// <summary>
    /// Replaces the text branding: primary color (<c>#rrggbb</c>, null = theme color), theme (null = default theme)
    /// and tagline (null = none). Images are not touched.
    /// </summary>
    Task<Org?> UpdateAsync(Guid orgId, OrgBrandingUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the image from its bytes (<see cref="OrgBrandingRules.ValidateImage"/>), stores it in the public bucket
    /// of <c>IFileStorage</c> and points the org's logo or hero URL at it (absolute URL). The previous image of the
    /// same kind is then deleted from the storage when it was uploaded here.
    /// </summary>
    Task<Org?> SetImageAsync(
        Guid orgId,
        BrandingImageKind kind,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the org's logo or hero URL and deletes the stored image when it was uploaded here.</summary>
    Task<Org?> RemoveImageAsync(Guid orgId, BrandingImageKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Text branding of <see cref="IOrgBrandingService.UpdateAsync"/>, as submitted (normalized by the service).</summary>
public sealed record OrgBrandingUpdate(string? PrimaryColor, string? PublicThemeId, string? Tagline);
