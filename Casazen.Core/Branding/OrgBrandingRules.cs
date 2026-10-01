using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Branding;

/// <summary>
/// Validation and normalization of the public-site branding of an org (BK-12, A3-17): primary color, theme, tagline
/// and the logo/hero images. One place for the rules; the console mirrors the limits in
/// <c>src/features/settings/site-appearance/branding-rules.ts</c>.
/// </summary>
public static partial class OrgBrandingRules
{
    public const string ColorInvalidCode = "org_branding_color_invalid";
    public const string ThemeInvalidCode = "org_branding_theme_invalid";
    public const string TaglineTooLongCode = "org_branding_tagline_too_long";
    public const string ImageEmptyCode = "org_branding_image_empty";
    public const string ImageTooLargeCode = "org_branding_image_too_large";
    public const string ImageTypeInvalidCode = "org_branding_image_type_invalid";
    public const string ImageDimensionsInvalidCode = "org_branding_image_dimensions_invalid";

    /// <summary>Longest tagline, after whitespace is collapsed: one line under the site name in the hero.</summary>
    public const int TaglineMaxLength = 160;

    /// <summary>Logo: shown in the site header (about 40 px tall), small file.</summary>
    public static BrandingImageSpec Logo { get; } = new(
        BrandingImageKind.Logo,
        MaxBytes: 2 * 1024 * 1024,
        MinWidth: 64,
        MinHeight: 32,
        MaxWidth: 4000,
        MaxHeight: 4000);

    /// <summary>Hero: full-width cover of the landing page, wide enough for a desktop screen.</summary>
    public static BrandingImageSpec Hero { get; } = new(
        BrandingImageKind.Hero,
        MaxBytes: 10 * 1024 * 1024,
        MinWidth: 1200,
        MinHeight: 400,
        MaxWidth: 8000,
        MaxHeight: 8000);

    public static BrandingImageSpec SpecFor(BrandingImageKind kind) => kind switch
    {
        BrandingImageKind.Logo => Logo,
        BrandingImageKind.Hero => Hero,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// The primary color as stored (<c>#rrggbb</c>, lower case), or <c>null</c> for "use the theme's color" when
    /// <paramref name="value"/> is empty. Accepts <c>#rgb</c> / <c>#rrggbb</c> in any case, with or without <c>#</c>.
    /// Throws <see cref="DomainRuleException"/> (<see cref="ColorInvalidCode"/>) for anything else: the value ends up
    /// in a CSS custom property of the public site, so nothing but a hex color is ever stored.
    /// </summary>
    public static string? NormalizePrimaryColor(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        var match = HexColor().Match(trimmed);
        if (!match.Success)
            throw new DomainRuleException(ColorInvalidCode, "OrgBrandingColorInvalid");

        var hex = match.Groups["hex"].Value.ToLowerInvariant();
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        return "#" + hex;
    }

    /// <summary>
    /// The theme id as stored, or <c>null</c> for the default theme when <paramref name="value"/> is empty. Throws
    /// <see cref="DomainRuleException"/> (<see cref="ThemeInvalidCode"/>) for a theme the public site does not support.
    /// </summary>
    public static string? NormalizeThemeId(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
            return null;

        return PublicSiteThemes.IsSupported(normalized)
            ? normalized
            : throw new DomainRuleException(ThemeInvalidCode, "OrgBrandingThemeInvalid");
    }

    /// <summary>
    /// The tagline as stored: trimmed, any run of whitespace (new lines included) collapsed into one space;
    /// <c>null</c> when empty. Throws <see cref="DomainRuleException"/> (<see cref="TaglineTooLongCode"/>) above
    /// <see cref="TaglineMaxLength"/> characters. Plain text: the public site renders it as text, never as HTML.
    /// </summary>
    public static string? NormalizeTagline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = Whitespace().Replace(value.Trim(), " ");
        return collapsed.Length <= TaglineMaxLength
            ? collapsed
            : throw new DomainRuleException(TaglineTooLongCode, "OrgBrandingTaglineTooLong", TaglineMaxLength);
    }

    /// <summary>
    /// Checks an uploaded image against <paramref name="spec"/> from its bytes, never from the client's file name or
    /// content type: PNG, JPEG or WebP (SVG is refused, it can carry scripts), at most <see cref="BrandingImageSpec.MaxBytes"/>,
    /// with pixel dimensions within the spec. Returns the detected format. Throws <see cref="DomainRuleException"/>.
    /// </summary>
    public static ImageHeaderInfo ValidateImage(BrandingImageSpec spec, ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
            throw new DomainRuleException(ImageEmptyCode, "OrgBrandingImageEmpty");

        if (content.Length > spec.MaxBytes)
            throw new DomainRuleException(ImageTooLargeCode, "OrgBrandingImageTooLarge", spec.MaxBytes / (1024 * 1024));

        var info = ImageHeaderReader.TryRead(content)
            ?? throw new DomainRuleException(ImageTypeInvalidCode, "OrgBrandingImageTypeInvalid");

        if (info.Width < spec.MinWidth || info.Height < spec.MinHeight ||
            info.Width > spec.MaxWidth || info.Height > spec.MaxHeight)
        {
            throw new DomainRuleException(
                ImageDimensionsInvalidCode,
                "OrgBrandingImageDimensionsInvalid",
                spec.MinWidth,
                spec.MinHeight,
                spec.MaxWidth,
                spec.MaxHeight);
        }

        return info;
    }

    [GeneratedRegex("^#?(?<hex>[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Which branding image of the org.</summary>
public enum BrandingImageKind
{
    Logo,
    Hero,
}

/// <summary>Accepted size (bytes) and pixel dimensions of a branding image.</summary>
public sealed record BrandingImageSpec(
    BrandingImageKind Kind,
    long MaxBytes,
    int MinWidth,
    int MinHeight,
    int MaxWidth,
    int MaxHeight);
