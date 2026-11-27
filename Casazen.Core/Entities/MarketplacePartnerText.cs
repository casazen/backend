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
    /// <summary>Name of the unique index on (<see cref="PartnerId"/>, <see cref="Language"/>).</summary>
    public const string PartnerLanguageIndexName = "UIX_MarketplacePartnerTexts_PartnerId_Language";

    /// <summary>Name of the check that keeps <see cref="Language"/> to the two languages of the product.</summary>
    public const string LanguageCheckName = "CK_MarketplacePartnerTexts_Language";

    /// <summary>An empty JSON array, the value of <see cref="FaqJson"/> with no question in it.</summary>
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

    /// <summary>The partner (FK, cascade).</summary>
    public Guid PartnerId { get; set; }

    /// <summary><c>it</c> or <c>en</c>.</summary>
    [Required]
    [MaxLength(LanguageMaxLength)]
    public string Language { get; set; } = Italian;

    /// <summary>The one-line benefit of the card.</summary>
    [Required]
    [MaxLength(BenefitMaxLength)]
    public string Benefit { get; set; } = string.Empty;

    /// <summary>For whom the partner is.</summary>
    [Required]
    [MaxLength(AudienceMaxLength)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>What the partner does.</summary>
    [Required]
    [MaxLength(LongTextMaxLength)]
    public string Description { get; set; } = string.Empty;

    /// <summary>What the host needs to activate it, one requirement per line.</summary>
    [Required]
    [MaxLength(LongTextMaxLength)]
    public string Requirements { get; set; } = string.Empty;

    /// <summary>What it costs, in words; <c>null</c> when the cost is not known.</summary>
    [MaxLength(CostTextMaxLength)]
    public string? CostText { get; set; }

    /// <summary>What data is shared with the partner, one plain sentence per line.</summary>
    [Required]
    [MaxLength(LongTextMaxLength)]
    public string DataShared { get; set; } = string.Empty;

    /// <summary>How to deactivate it.</summary>
    [Required]
    [MaxLength(LongTextMaxLength)]
    public string Deactivation { get; set; } = string.Empty;

    /// <summary>The guide the hand-off wizard shows while the host activates the partner on its own site.</summary>
    [Required]
    [MaxLength(LongTextMaxLength)]
    public string ActivationGuide { get; set; } = string.Empty;

    /// <summary>The questions and answers, a JSON array of <c>{ "q": "...", "a": "..." }</c> (jsonb).</summary>
    [Required]
    public string FaqJson { get; set; } = EmptyJsonArray;
}
