using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OwnerConversations")]
public class OwnerConversation : ITenantOwned
{
    public const int SubjectMaxLength = 160;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public OwnerConversationTopic Topic { get; set; } = OwnerConversationTopic.General;

    public Guid? PropertyId { get; set; }

    public Guid? OwnerStayRequestId { get; set; }

    public Guid? OwnerDocumentId { get; set; }

    public Guid? OwnerStatementVersionId { get; set; }

    [Required]
    public string Subject { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    public Guid? CreatedByOwnerUserId { get; set; }

    public DateTime LastMessageAt { get; set; }
}
