using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// A generated (or edited) text of an SEO page. Never changed once stored: a new text is a new revision, which waits for
/// an approval before it is published (SE-01).
/// </summary>
[Table("SeoContentRevisions")]
public class SeoContentRevision
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PageId { get; set; }

    public SeoContentPage Page { get; set; } = null!;

    [Required]
    public string BodyHtml { get; set; } = string.Empty;

    public AiModelTier AiModelTier { get; set; } = AiModelTier.Economy;

    public int PromptTokens { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(100)]
    public string SourceDataVersion { get; set; } = string.Empty;

    /// <summary>Whether <see cref="BodyHtml"/> is publishable text or an explicit "content not generated" state.</summary>
    public SeoContentStatus ContentStatus { get; set; } = SeoContentStatus.Generated;

    /// <summary>Version of the prompt that produced the text (<c>SeoContentPrompt.Version</c>); <c>null</c> before SE-01.</summary>
    [MaxLength(40)]
    public string? PromptVersion { get; set; }
}
