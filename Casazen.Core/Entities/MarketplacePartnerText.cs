using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// The words a marketplace partner shows to the host in one language (E09/F01/US01, NF-55, NF-59): what the card and the detail card say.
/// Platform data, child of <see cref="MarketplacePartner"/> (deleted with it). One row per partner and language, <c>it</c> or <c>en</c>:
/// the marketplace falls back to the Italian row when the language asked for has none.
/// </summary>
/// <remarks>
/// The bounds other than <see cref="BenefitMaxLength"/> are the task's own, so a row written by hand cannot grow without limit. The cost is
/// shown only when known: <see cref="CostText"/> is <c>null</c> otherwise and nothing is invented in its place. The activation guide is
/// not part of the detail card: it belongs to the hand-off wizard.
/// </remarks>
[Table("MarketplacePartnerTexts")]
public class MarketplacePartnerText
{
    public const string PartnerLanguageIndexName = "UIX_MarketplacePartnerTexts_PartnerId_Language";

    public const string LanguageCheckName = "CK_MarketplacePartnerTexts_Language";

    public const string EmptyJsonArray = "[]";

    public const string Italian = "it";
    public const string English = "en";

    public const int LanguageMaxLength = 2;
    public const int BenefitMaxLength = 200;
    public const int AudienceMaxLength = 500;
    public const int CostTextMaxLength = 300;
    public const int LongTextMaxLength = 4000;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PartnerId { get; set; }

    [Required]
    [MaxLength(LanguageMaxLength)]
    public string Language { get; set; } = Italian;

    [Required]
    [MaxLength(BenefitMaxLength)]
    public string Benefit { get; set; } = string.Empty;

    [Required]
    [MaxLength(AudienceMaxLength)]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MaxLength(LongTextMaxLength)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [MaxLength(LongTextMaxLength)]
    public string Requirements { get; set; } = string.Empty;

    [MaxLength(CostTextMaxLength)]
    public string? CostText { get; set; }

    [Required]
    [MaxLength(LongTextMaxLength)]
    public string DataShared { get; set; } = string.Empty;

    [Required]
    [MaxLength(LongTextMaxLength)]
    public string Deactivation { get; set; } = string.Empty;

    [Required]
    [MaxLength(LongTextMaxLength)]
    public string ActivationGuide { get; set; } = string.Empty;

    [Required]
    public string FaqJson { get; set; } = EmptyJsonArray;
}
