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
    /// <summary>Longest <see cref="ProposedText"/>, in characters of the clear text.</summary>
    public const int ProposedTextMaxLength = 500;

    public const int ThreadKeyMaxLength = 80;

    public const int DecidedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property of the conversation the lesson came from; <c>null</c> when it has none.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>The conversation the lesson came from: <c>ota:{guid}</c> or <c>direct:{guid}</c> (<c>MessageThreadKey</c>).</summary>
    [Required, MaxLength(ThreadKeyMaxLength)]
    public string ThreadKey { get; set; } = string.Empty;

    /// <summary>The item of the approval queue (the draft that was corrected or rejected) the lesson came from; <c>null</c> when there is none. No foreign key.</summary>
    public Guid? ApprovalItemId { get; set; }

    public LessonKind Kind { get; set; }

    public KnowledgeTopic Topic { get; set; }

    /// <summary>What the assistant proposes to learn. <b>Encrypted at rest</b>; 1 to <see cref="ProposedTextMaxLength"/> characters.</summary>
    public string ProposedText { get; set; } = string.Empty;

    public LessonStatus Status { get; set; } = LessonStatus.Proposed;

    /// <summary>Where the approved lesson applies; <c>null</c> until a person decides.</summary>
    public LessonScope? Scope { get; set; }

    /// <summary>The user who approved or rejected it (<c>User.Id</c>), an opaque id with no foreign key.</summary>
    [MaxLength(DecidedByUserIdMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>The entry of the knowledge base the approval created (<see cref="PropertyKnowledgeEntry.LessonId"/> points back); <c>null</c> until then. No foreign key.</summary>
    public Guid? AppliedEntryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
