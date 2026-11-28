using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One entry of the knowledge base of the assistant that a person wrote, or that an approved lesson taught it (E05/F05/US01/T01, NF-66): a question with its
/// answer, a fact or a rule, about a property or, with <see cref="PropertyId"/> null, about the whole organization. What the host already entered in the
/// listing (check-in window, parking, house rules, amenities, directions) is <b>not</b> stored here: it is built from the property on every read, so it never goes
/// stale (<c>ListingKnowledgeBuilder</c>, T02).
/// </summary>
/// <remarks>
/// <para><b>Personal data, encrypted.</b> <see cref="Text"/> is free text of the host and can hold anything (a Wi-Fi password, a note about a guest): it is
/// encrypted at rest with the purpose <c>Casazen.Assistant.Content</c> (<c>EncryptedColumns</c>), so the database only holds the Data Protection payload and
/// code reads and writes the clear text through EF. Its length (<see cref="TextMaxLength"/>) is the service's rule, not a column length: the payload is longer
/// than the text. The text is never logged.</para>
/// <para><b>Reach.</b> An entry of a property is read and changed by whoever reaches that property; an entry of the organization (no property) is changed by
/// the organization's administrators only and applies to every property that has no entry of its own with the same topic and kind.</para>
/// </remarks>
[Table("PropertyKnowledgeEntries")]
public class PropertyKnowledgeEntry : ITenantOwned
{
    public const int TitleMaxLength = 200;

    public const int TextMaxLength = 1000;

    public const int LanguageMaxLength = 8;

    public const int CreatedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public KnowledgeTopic Topic { get; set; }

    public KnowledgeEntryKind Kind { get; set; }

    public KnowledgeSource Source { get; set; } = KnowledgeSource.Custom;

    [MaxLength(TitleMaxLength)]
    public string? Title { get; set; }

    public string Text { get; set; } = string.Empty;

    [MaxLength(LanguageMaxLength)]
    public string? Language { get; set; }

    public Guid? LessonId { get; set; }

    public bool IsActive { get; set; } = true;

    [Required, MaxLength(CreatedByUserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
