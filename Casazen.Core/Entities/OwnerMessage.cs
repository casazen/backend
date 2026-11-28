using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

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

    [MaxLength(UserIdMaxLength)]
    public string? SenderUserId { get; set; }

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
