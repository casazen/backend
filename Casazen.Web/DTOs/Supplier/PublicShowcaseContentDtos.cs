using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

// What the published content of a supplier adds to its public page (SP-13a). The page of a supplier that published nothing has none of
// these members and reads exactly as it always did. Everything here is the supplier's own published text and the few facts that are
// measured or checked by the platform: never the draft, never a phone number, an e-mail address, a VAT number, a status or an id.

/// <summary>
/// The published content as the page reads it: the members of the contract (<see cref="ShowcaseContentDocument"/>) without the order of the
/// services, which is applied to <c>services</c>. Plain text; the images are references to the supplier's own storage.
/// </summary>
public class PublicShowcaseContentDto
{
    /// <summary><c>acqua</c>, <c>scintilla</c>, <c>fresco</c> or <c>naturale</c>; left out = the style recommended for the trade.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; set; }

    /// <summary><c>moderno</c>, <c>editoriale</c> or <c>classico</c>; left out = the style's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Font { get; set; }

    /// <summary><c>#RRGGBB</c> of the buttons, one white text reads on at 4.5:1 or more; left out = the style's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShowcaseLogo? Logo { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShowcaseCover? Cover { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tagline { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intro { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bio { get; set; }

    public IReadOnlyList<ShowcaseTeamMember> Team { get; set; } = [];

    public IReadOnlyList<string> Values { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShowcaseGuarantee? Guarantee { get; set; }

    public IReadOnlyList<ShowcaseCertification> Certs { get; set; } = [];

    public IReadOnlyList<ShowcaseFaq> Faq { get; set; } = [];

    public IReadOnlyList<ShowcaseWork> Gallery { get; set; } = [];

    /// <summary>"Prima che arriviamo".</summary>
    public IReadOnlyList<string> Prep { get; set; } = [];

    /// <summary>Which blocks of the home are shown (the services and the final actions are always shown).</summary>
    public ShowcaseSections Sections { get; set; } = new();
}

/// <summary>
/// What the platform vouches for, only what is measured or checked (decisions D11, D13, D24): "Verificato" and the jobs done with CasaZen. No
/// rating, no reviews, no insurance, no years of experience.
/// </summary>
public class PublicSupplierTrustDto
{
    /// <summary>"Verificato": an active profile, a VAT number and a Stripe account that can take charges and payouts. The VAT number itself is never shown.</summary>
    public bool Verified { get; set; }

    /// <summary>The jobs done with CasaZen, a true count, only from <see cref="ShowcaseContentLimits.JobsDoneMinToShow"/> of them; left out otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? JobsDone { get; set; }
}

/// <summary>Maps the published content of a showcase onto the public page.</summary>
public static class PublicShowcaseMapper
{
    /// <summary>
    /// Adds the members of the published content to <paramref name="dto"/>: the published content, <c>paused</c>, <c>bookingsOpen</c>,
    /// <c>trust</c>, <c>respondWithinMinutes</c>, <c>acceptsOnlinePayments</c> and <c>privacyNoticeVersion</c>.
    /// </summary>
    public static void Apply(SupplierShowcaseDto dto, PublishedShowcasePage page)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(page);

        dto.Content = ToContent(page.Content);
        dto.Paused = page.Paused;
        dto.BookingsOpen = page.BookingsOpen;
        dto.Trust = new PublicSupplierTrustDto { Verified = page.Verified, JobsDone = page.JobsDone };
        dto.RespondWithinMinutes = page.RespondWithinMinutes;
        dto.AcceptsOnlinePayments = page.AcceptsOnlinePayments;
        dto.PrivacyNoticeVersion = page.PrivacyNoticeVersion;
    }

    /// <summary>The published document without the order of the services (it orders <c>services</c>).</summary>
    public static PublicShowcaseContentDto ToContent(ShowcaseContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new PublicShowcaseContentDto
        {
            Style = document.Style,
            Font = document.Font,
            Color = document.Color,
            Logo = document.Logo,
            Cover = document.Cover,
            Tagline = document.Tagline,
            Intro = document.Intro,
            Bio = document.Bio,
            Team = document.Team,
            Values = document.Values,
            Guarantee = document.Guarantee,
            Certs = document.Certs,
            Faq = document.Faq,
            Gallery = document.Gallery,
            Prep = document.Prep,
            Sections = document.Sections,
        };
    }
}
