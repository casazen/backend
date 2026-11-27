using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A fact or a rule the assistant proposes to learn from a correction or a rejection of one of its drafts (E05/F05/US01/T01, NF-66): «parking is free only at the
/// weekend». <b>Nothing enters the knowledge base without a person's approval</b> (AC-NF66-6): the lesson waits as <see cref="LessonStatus.Proposed"/>, and when a
/// person approves it with a <see cref="Scope"/> it becomes an entry of the knowledge base (<see cref="AppliedEntryId"/>).
/// </summary>
/// <remarks>
/// <see cref="ProposedText"/> is derived from what the host wrote and can hold anything: it is encrypted at rest with the purpose <c>Casazen.Assistant.Content</c>
/// (<c>EncryptedColumns</c>) and never logged; its length (<see cref="ProposedTextMaxLength"/>) is the service's rule, not a column length. The row carries the
/// key of the conversation and the id of the approval item it came from, and no text of the guest.
/// </remarks>
[Table("AssistantLessons")]
public class AssistantLesson : ITenantOwned
{
    public const int ProposedTextMaxLength = 500;

    public const int ThreadKeyMaxLength = 80;

    public const int DecidedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    [Required, MaxLength(ThreadKeyMaxLength)]
    public string ThreadKey { get; set; } = string.Empty;

    public Guid? ApprovalItemId { get; set; }

    public LessonKind Kind { get; set; }

    public KnowledgeTopic Topic { get; set; }

    public string ProposedText { get; set; } = string.Empty;

    public LessonStatus Status { get; set; } = LessonStatus.Proposed;

    public LessonScope? Scope { get; set; }

    [MaxLength(DecidedByUserIdMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime? DecidedAt { get; set; }

    public Guid? AppliedEntryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
