using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

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
}
