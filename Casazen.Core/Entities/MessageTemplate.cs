using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A message template of the host (E05/F01/US01/T01, E05/F02/US02): a name, a language and a body with variables CasaZen replaces when the
/// host uses it. Unique per organization, name and language. No personal data: the body holds the host's own words and placeholders, never
/// a guest's data.
/// </summary>
[Table("MessageTemplates")]
public class MessageTemplate : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="Name"/>, <see cref="Language"/>).</summary>
    public const string OrgNameLanguageIndexName = "UIX_MessageTemplates_OrgId_Name_Language";

    public const int NameMaxLength = 100;

    public const int LanguageMaxLength = 5;

    public const int BodyMaxLength = 2000;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The language of the body, a code such as <c>it</c> or <c>en</c>.</summary>
    [Required]
    [MaxLength(LanguageMaxLength)]
    public string Language { get; set; } = string.Empty;

    [Required]
    [MaxLength(BodyMaxLength)]
    public string Body { get; set; } = string.Empty;

    /// <summary>The template can be used; an inactive one is kept and not offered.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
