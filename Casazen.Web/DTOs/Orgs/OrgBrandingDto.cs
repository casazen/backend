using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Orgs;

public class OrgBrandingDto
{
    public string? LogoUrl { get; set; }

    public string? HeroImageUrl { get; set; }

    public string? PrimaryColor { get; set; }

    public string PublicThemeId { get; set; } = PublicSiteThemes.Default;

    public string? PublicFontId { get; set; }

    public string? Tagline { get; set; }

    public string? Subtitle { get; set; }

    public string? HostName { get; set; }

    public string? PublicPhone { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool ShowPoweredBy { get; set; }

    public static OrgBrandingDto FromOrg(Org org, PlanTier effectiveTier) => new()
    {
        LogoUrl = org.LogoUrl,
        HeroImageUrl = org.HeroImageUrl,
        PrimaryColor = org.ThemeColor,
        PublicThemeId = PublicSiteThemes.Resolve(org.PublicThemeId),
        PublicFontId = PublicSiteFonts.Resolve(org.PublicFontId),
        Tagline = org.Tagline,
        Subtitle = org.Subtitle,
        HostName = org.HostName,
        PublicPhone = org.PublicPhone,
        Slug = org.Slug,
        DisplayName = org.DisplayName,
        ShowPoweredBy = effectiveTier == PlanTier.Starter,
    };
}

/// <summary>
/// Body of <c>PUT /api/orgs/me/branding</c>: the text branding, replaced as a whole (images have their own endpoints).
/// The annotations only bound the input; the rules (hex color, supported theme, tagline length) are
/// <see cref="OrgBrandingRules"/>. Error messages are keys of <c>Resources/SharedResources.resx</c>.
/// </summary>
/// <remarks>
/// The color, the theme and the tagline are replaced as a whole: a body without them clears them, as it always did. The
/// public profile of DB-03 (<see cref="Subtitle"/>, <see cref="HostName"/>, <see cref="PublicPhone"/>) and the font of DB-01
/// (<see cref="PublicFontId"/>) follow the rule of
/// <c>PUT /api/properties/{id}</c> instead: a member that is not in the body keeps its stored value, so a client that
/// does not know it (the appearance form of today) never erases it; <c>null</c> or a blank clears it.
/// </remarks>
public class UpdateOrgBrandingDto
{
    [MaxLength(20, ErrorMessage = "OrgBrandingColorInvalid")]
    public string? PrimaryColor { get; set; }

    [MaxLength(50, ErrorMessage = "OrgBrandingThemeInvalid")]
    public string? PublicThemeId { get; set; }

    [MaxLength(500, ErrorMessage = "OrgBrandingTaglineTooLongInput")]
    public string? Tagline { get; set; }

    [MaxLength(500, ErrorMessage = "OrgBrandingSubtitleTooLongInput")]
    public string? Subtitle
    {
        get;
        set
        {
            field = value;
            SubtitleSent = true;
        }
    }

    [MaxLength(200, ErrorMessage = "OrgBrandingHostNameTooLongInput")]
    public string? HostName
    {
        get;
        set
        {
            field = value;
            HostNameSent = true;
        }
    }

    [MaxLength(40, ErrorMessage = "OrgBrandingPhoneInvalid")]
    public string? PublicPhone
    {
        get;
        set
        {
            field = value;
            PublicPhoneSent = true;
        }
    }

    [MaxLength(20, ErrorMessage = "OrgBrandingFontInvalid")]
    public string? PublicFontId
    {
        get;
        set
        {
            field = value;
            PublicFontIdSent = true;
        }
    }

    [JsonIgnore]
    public bool SubtitleSent { get; private set; }

    [JsonIgnore]
    public bool PublicFontIdSent { get; private set; }

    [JsonIgnore]
    public bool HostNameSent { get; private set; }

    [JsonIgnore]
    public bool PublicPhoneSent { get; private set; }
}
