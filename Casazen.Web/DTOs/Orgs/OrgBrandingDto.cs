using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// The caller org's public-site branding (BK-12, A3-17): <c>GET/PUT /api/orgs/me/branding</c> and the logo/hero image
/// endpoints. Same values, same names as the anonymous <c>PublicOrgDto</c> / <c>ResolveHostBrandingDto</c>, plus what the
/// console preview needs (slug, display name, "Powered by").
/// </summary>
public class OrgBrandingDto
{
    /// <summary>Absolute public URL of the logo, or null (the site shows the display name).</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Absolute public URL of the hero image, or null (the landing uses the first property photo).</summary>
    public string? HeroImageUrl { get; set; }

    /// <summary><c>#rrggbb</c>, or null for the theme's own color.</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>The theme the public site renders: always one of <see cref="PublicSiteThemes.All"/>.</summary>
    public string PublicThemeId { get; set; } = PublicSiteThemes.Default;

    public string? Tagline { get; set; }

    /// <summary>The sentence under the slogan in the cover of the public site (DB-03), or null.</summary>
    public string? Subtitle { get; set; }

    /// <summary>How the host is named on the public site (DB-03), or null: the site shows the display name only.</summary>
    public string? HostName { get; set; }

    /// <summary>
    /// The phone number the host chose to publish on the site (DB-03): <c>+</c> or digits only, or null for none. Whatever is
    /// here is what any visitor can read.
    /// </summary>
    public string? PublicPhone { get; set; }

    /// <summary>Public slug of the org (preview link <c>/book/{slug}</c>), edited in the org settings (PL-04).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Name shown on the site when there is no logo, edited in the org settings (PL-04).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the public site shows "Powered by CasaZen" (effective tier Starter, A3-37).</summary>
    public bool ShowPoweredBy { get; set; }

    public static OrgBrandingDto FromOrg(Org org, PlanTier effectiveTier) => new()
    {
        LogoUrl = org.LogoUrl,
        HeroImageUrl = org.HeroImageUrl,
        PrimaryColor = org.ThemeColor,
        PublicThemeId = PublicSiteThemes.Resolve(org.PublicThemeId),
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
/// public profile of DB-03 (<see cref="Subtitle"/>, <see cref="HostName"/>, <see cref="PublicPhone"/>) follows the rule of
/// <c>PUT /api/properties/{id}</c> instead: a member that is not in the body keeps its stored value, so a client that
/// does not know it (the appearance form of today) never erases it; <c>null</c> or a blank clears it.
/// </remarks>
public class UpdateOrgBrandingDto
{
    /// <summary><c>#rgb</c> or <c>#rrggbb</c>; null or empty for the theme's own color.</summary>
    [MaxLength(20, ErrorMessage = "OrgBrandingColorInvalid")]
    public string? PrimaryColor { get; set; }

    /// <summary>One of <see cref="PublicSiteThemes.All"/>; null or empty for the default theme.</summary>
    [MaxLength(50, ErrorMessage = "OrgBrandingThemeInvalid")]
    public string? PublicThemeId { get; set; }

    /// <summary>Plain text, at most <see cref="OrgBrandingRules.TaglineMaxLength"/> characters once whitespace is collapsed.</summary>
    [MaxLength(500, ErrorMessage = "OrgBrandingTaglineTooLongInput")]
    public string? Tagline { get; set; }

    /// <summary>
    /// The sentence under the slogan (DB-03): plain text, at most <see cref="OrgBrandingRules.SubtitleMaxLength"/> characters
    /// once whitespace is collapsed. Left out = unchanged; <c>null</c> or blank = none.
    /// </summary>
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

    /// <summary>
    /// How the host is named on the site (DB-03), at most <see cref="OrgBrandingRules.HostNameMaxLength"/> characters. Left out
    /// = unchanged; <c>null</c> or blank = none.
    /// </summary>
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

    /// <summary>
    /// The phone number to publish on the site (DB-03): digits with an optional leading <c>+</c> and the usual separators,
    /// 6 to 15 digits (<see cref="OrgBrandingRules.NormalizePublicPhone"/>); it is stored without the separators. Whatever is
    /// saved here is readable by anyone who opens the site. Left out = unchanged; <c>null</c> or blank = unpublish.
    /// </summary>
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

    /// <summary>True when the body carries <see cref="Subtitle"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool SubtitleSent { get; private set; }

    /// <summary>True when the body carries <see cref="HostName"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool HostNameSent { get; private set; }

    /// <summary>True when the body carries <see cref="PublicPhone"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool PublicPhoneSent { get; private set; }
}
