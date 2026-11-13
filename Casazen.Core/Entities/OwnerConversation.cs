using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A conversation of the owner inbox between an agency and one of its owner profiles (E07/F07/US01/T02, AC-NF111-5): general, or about a property, an
/// owner stay, a shared document or a published statement version of that owner profile (exactly the reference its <see cref="Topic"/> names). Its
/// messages are <see cref="OwnerMessage"/> rows, kept as they were written.
/// </summary>
[Table("OwnerConversations")]
public class OwnerConversation : ITenantOwned
{
    public const int SubjectMaxLength = 160;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the agency.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The owner profile the conversation is with (<c>PropertyOwner.Id</c>).</summary>
    public Guid PropertyOwnerId { get; set; }

    public OwnerConversationTopic Topic { get; set; } = OwnerConversationTopic.General;

    /// <summary>The property of a <see cref="OwnerConversationTopic.Property"/> or <see cref="OwnerConversationTopic.OwnerStay"/> conversation.</summary>
    public Guid? PropertyId { get; set; }

    public Guid? OwnerStayRequestId { get; set; }

    public Guid? OwnerDocumentId { get; set; }

    public Guid? OwnerStatementVersionId { get; set; }

    /// <summary>
    /// What the conversation is called, written by a person. <b>Encrypted at rest</b> (<c>EncryptedColumns</c>, purpose <c>Casazen.OwnerPortal.Message</c>, as
    /// the words of its messages): no length on the column, <see cref="SubjectMaxLength"/> is the rule of the service.
    /// </summary>
    [Required]
    public string Subject { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>The person of the agency who opened it (<c>User.Id</c>); <c>null</c> when the owner side did.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    /// <summary>The person of the portal who opened it (<c>OwnerUser.Id</c>); <c>null</c> when the agency did.</summary>
    public Guid? CreatedByOwnerUserId { get; set; }

    /// <summary>When the last message was written: the order of the inbox.</summary>
    public DateTime LastMessageAt { get; set; }
}
