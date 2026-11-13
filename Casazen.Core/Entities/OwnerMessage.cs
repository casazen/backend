using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One message of an <see cref="OwnerConversation"/> (E07/F07/US01/T02, AC-NF111-5): who wrote it, the words and, optionally, one file. Kept as it was written
/// (<see cref="IAppendOnlyRecord"/>): the inbox is the record of what the agency and the owner told each other. The file, like a shared document, is stored
/// only after the scan and is read only through the endpoints of the conversation.
/// </summary>
[Table("OwnerMessages")]
public class OwnerMessage : ITenantOwned, IAppendOnlyRecord
{
    public const int BodyMaxLength = 4000;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid ConversationId { get; set; }

    public OwnerMessageSender Sender { get; set; }

    /// <summary>The person of the agency who wrote it (<c>User.Id</c>).</summary>
    [MaxLength(UserIdMaxLength)]
    public string? SenderUserId { get; set; }

    /// <summary>The person of the portal who wrote it (<c>OwnerUser.Id</c>): the owner or a delegated contact.</summary>
    public Guid? SenderOwnerUserId { get; set; }

    /// <summary>
    /// What was written, plain text. <b>Encrypted at rest</b> (<c>EncryptedColumns</c>, purpose <c>Casazen.OwnerPortal.Message</c>, like the words of the other
    /// conversations): no length on the column, the payload is longer than the text, and <see cref="BodyMaxLength"/> is the rule of the service. Free text a
    /// person wrote can say anything: never searched, never put in an e-mail or a log.
    /// </summary>
    [Required]
    public string Body { get; set; } = string.Empty;

    [MaxLength(OwnerDocumentVersion.StorageKeyMaxLength)]
    public string? AttachmentKey { get; set; }

    /// <summary>The name the download offers (made by <c>OwnerFileRules.SafeFileName</c>). <b>Encrypted at rest</b>, with <see cref="Body"/>: a name can name a person or a case.</summary>
    public string? AttachmentFileName { get; set; }

    [MaxLength(OwnerDocumentVersion.ContentTypeMaxLength)]
    public string? AttachmentContentType { get; set; }

    public long? AttachmentSizeBytes { get; set; }

    [MaxLength(OwnerDocumentVersion.HashLength)]
    public string? AttachmentSha256 { get; set; }

    [MaxLength(OwnerDocumentVersion.EngineMaxLength)]
    public string? AttachmentScanEngine { get; set; }

    public DateTime CreatedAt { get; set; }
}
