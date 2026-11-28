using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

// What the published content of a supplier adds to its public page (SP-13a). The page of a supplier that published nothing has none of
// these members and reads exactly as it always did. Everything here is the supplier's own published text and the few facts that are
// measured or checked by the platform: never the draft, never a phone number, an e-mail address, a VAT number, a status or an id.

public class PublicShowcaseContentDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Font { get; set; }

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

    public IReadOnlyList<string> Prep { get; set; } = [];

    public ShowcaseSections Sections { get; set; } = new();
}

public class PublicSupplierTrustDto
{
    /// <summary>"Verificato": an active profile, a VAT number and a Stripe account that can take charges and payouts. The VAT number itself is never shown.</summary>
    public bool Verified { get; set; }

    /// <summary>The jobs done with CasaZen, a true count, only from <see cref="ShowcaseContentLimits.JobsDoneMinToShow"/> of them; left out otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? JobsDone { get; set; }
}

public static class PublicShowcaseMapper
{
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
