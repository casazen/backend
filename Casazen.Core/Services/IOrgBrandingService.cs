using Casazen.Core.Branding;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IOrgBrandingService
{
    /// <summary>
    /// Replaces the text branding: primary color (<c>#rrggbb</c>, null = theme color), theme (null = default theme)
    /// and tagline (null = none). Images are not touched. The public profile of DB-03 (subtitle, name of the host, public
    /// phone) changes only for the fields the update carries (<see cref="FieldUpdate{T}"/>): a caller that does not know them
    /// never erases them.
    /// </summary>
    Task<Org?> UpdateAsync(Guid orgId, OrgBrandingUpdate update, CancellationToken cancellationToken = default);

    Task<Org?> SetImageAsync(
        Guid orgId,
        BrandingImageKind kind,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<Org?> RemoveImageAsync(Guid orgId, BrandingImageKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// Text branding of <see cref="IOrgBrandingService.UpdateAsync"/>, as submitted (normalized by the service). The color, theme
/// and tagline are replaced as a whole, as they always were; <paramref name="Subtitle"/>, <paramref name="HostName"/> and
/// <paramref name="PublicPhone"/> (DB-03) and <paramref name="PublicFontId"/> (DB-01) are set, cleared (<c>null</c> or blank)
/// or left as they are when not sent.
/// </summary>
public sealed record OrgBrandingUpdate(
    string? PrimaryColor,
    string? PublicThemeId,
    string? Tagline,
    FieldUpdate<string> Subtitle = default,
    FieldUpdate<string> HostName = default,
    FieldUpdate<string> PublicPhone = default,
    FieldUpdate<string> PublicFontId = default);

public readonly record struct FieldUpdate<T>(bool IsSent, T? Value)
{
    public static FieldUpdate<T> Unchanged => default;

    public static FieldUpdate<T> Set(T? value) => new(true, value);
}
