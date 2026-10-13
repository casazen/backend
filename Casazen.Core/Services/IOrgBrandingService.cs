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
    /// and tagline (null = none). Images are not touched. The public profile of DB-03 (subtitle, name of the host, public
    /// phone) changes only for the fields the update carries (<see cref="FieldUpdate{T}"/>): a caller that does not know them
    /// never erases them.
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

/// <summary>
/// Text branding of <see cref="IOrgBrandingService.UpdateAsync"/>, as submitted (normalized by the service). The color, theme
/// and tagline are replaced as a whole, as they always were; <paramref name="Subtitle"/>, <paramref name="HostName"/> and
/// <paramref name="PublicPhone"/> (DB-03) are set, cleared (<c>null</c> or blank) or left as they are when not sent.
/// </summary>
public sealed record OrgBrandingUpdate(
    string? PrimaryColor,
    string? PublicThemeId,
    string? Tagline,
    FieldUpdate<string> Subtitle = default,
    FieldUpdate<string> HostName = default,
    FieldUpdate<string> PublicPhone = default);

/// <summary>
/// A field of an update that can be left out: <see cref="IsSent"/> is false for "keep what is stored" (the default value),
/// true for "write <see cref="Value"/>", which may be <c>null</c> to clear the field. JSON cannot tell an absent member from a
/// null one in a plain nullable property; the request DTOs record it and hand it over here.
/// </summary>
public readonly record struct FieldUpdate<T>(bool IsSent, T? Value)
{
    /// <summary>The field is left as it is.</summary>
    public static FieldUpdate<T> Unchanged => default;

    /// <summary>The field is written with <paramref name="value"/> (<c>null</c> clears it).</summary>
    public static FieldUpdate<T> Set(T? value) => new(true, value);
}
