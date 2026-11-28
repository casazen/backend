using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Branding;

public static partial class OrgBrandingRules
{
    public const string ColorInvalidCode = "org_branding_color_invalid";
    public const string ThemeInvalidCode = "org_branding_theme_invalid";
    public const string FontInvalidCode = "org_branding_font_invalid";
    public const string TaglineTooLongCode = "org_branding_tagline_too_long";
    public const string SubtitleTooLongCode = "org_branding_subtitle_too_long";
    public const string HostNameTooLongCode = "org_branding_host_name_too_long";
    public const string PhoneInvalidCode = "org_branding_phone_invalid";
    public const string ImageEmptyCode = "org_branding_image_empty";
    public const string ImageTooLargeCode = "org_branding_image_too_large";
    public const string ImageTypeInvalidCode = "org_branding_image_type_invalid";
    public const string ImageDimensionsInvalidCode = "org_branding_image_dimensions_invalid";

    public const int TaglineMaxLength = 160;

    public const int SubtitleMaxLength = 300;

    public const int HostNameMaxLength = 100;

    public const int PhoneMinDigits = 6;

    public const int PhoneMaxDigits = 15;

    public static BrandingImageSpec Logo { get; } = new(
        BrandingImageKind.Logo,
        MaxBytes: 2 * 1024 * 1024,
        MinWidth: 64,
        MinHeight: 32,
        MaxWidth: 4000,
        MaxHeight: 4000);

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

    public static string? NormalizeThemeId(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
            return null;

        return PublicSiteThemes.IsSupported(normalized)
            ? normalized
            : throw new DomainRuleException(ThemeInvalidCode, "OrgBrandingThemeInvalid");
    }

    public static string? NormalizeFontId(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
            return null;

        return PublicSiteFonts.IsSupported(normalized)
            ? normalized
            : throw new DomainRuleException(FontInvalidCode, "OrgBrandingFontInvalid");
    }

    public static string? NormalizeTagline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = Whitespace().Replace(value.Trim(), " ");
        return collapsed.Length <= TaglineMaxLength
            ? collapsed
            : throw new DomainRuleException(TaglineTooLongCode, "OrgBrandingTaglineTooLong", TaglineMaxLength);
    }

    public static string? NormalizeSubtitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = Whitespace().Replace(value.Trim(), " ");
        return collapsed.Length <= SubtitleMaxLength
            ? collapsed
            : throw new DomainRuleException(SubtitleTooLongCode, "OrgBrandingSubtitleTooLong", SubtitleMaxLength);
    }

    public static string? NormalizeHostName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = Whitespace().Replace(value.Trim(), " ");
        return collapsed.Length <= HostNameMaxLength
            ? collapsed
            : throw new DomainRuleException(HostNameTooLongCode, "OrgBrandingHostNameTooLong", HostNameMaxLength);
    }

    public static string? NormalizePublicPhone(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        var onlyPhoneCharacters = text.All(c => char.IsAsciiDigit(c) || c is ' ' or '.' or '-' or '(' or ')' or '/' or '+');
        var plusOnlyFirst = text.LastIndexOf('+') <= 0;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (!onlyPhoneCharacters || !plusOnlyFirst || digits.Length < PhoneMinDigits || digits.Length > PhoneMaxDigits)
            throw new DomainRuleException(PhoneInvalidCode, "OrgBrandingPhoneInvalid");

        return text.StartsWith('+') ? "+" + digits : digits;
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

public enum BrandingImageKind
{
    Logo,
    Hero,
}

public sealed record BrandingImageSpec(
    BrandingImageKind Kind,
    long MaxBytes,
    int MinWidth,
    int MinHeight,
    int MaxWidth,
    int MaxHeight);
