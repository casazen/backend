using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One message of an <see cref="OtaMessageThread"/> (E05/F01/US01/T01, ADR-032): a copy of what the channel manager holds, or a message
/// CasaZen wrote and sent through it (E05/F02/US02).
/// </summary>
/// <remarks>
/// <para><b>Personal data, encrypted.</b> <see cref="Text"/> is free text a guest or a host wrote and can say anything: it is encrypted at
/// rest (<c>EncryptedColumns</c>, purpose <c>Casazen.Message.Content</c>, ADR-032-R3) and the database only holds the Data Protection
/// payload; code reads and writes the clear text through EF. The text, the name of the sender and the address of an attachment are never
/// logged, never put in an event, and go with the guest when it is erased or anonymized (<c>GuestDataEraser</c>).</para>
/// <para><b>Once.</b> A message of the channel manager is keyed by (<see cref="OrgId"/>, <see cref="OctorateMessageId"/>): delivered twice, it
/// is one row. A message CasaZen wrote carries the key of its sender's screen (<see cref="IdempotencyKey"/>, unique in the thread), so a retry
/// of a send that did not seem to arrive is the same message (OCT-10).</para>
/// </remarks>
[Table("OtaMessages")]
public class OtaMessage : ITenantOwned
{
    public const int OctorateMessageIdMaxLength = 64;

    public const int SenderNameMaxLength = 200;

    public const int AttachmentUrlMaxLength = 2000;

    public const int AttachmentContentTypeMaxLength = 100;

    public const int IdempotencyKeyMaxLength = 64;

    public const int RuleCodeMaxLength = 64;

    public const int SentByUserIdMaxLength = 255;

    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="OctorateMessageId"/>) of the messages that have an Octorate id.</summary>
    public const string OrgOctorateMessageIndexName = "UIX_OtaMessages_OrgId_OctorateMessageId";

    /// <summary>Name of the unique index on (<see cref="ThreadId"/>, <see cref="IdempotencyKey"/>) of the messages that have a key.</summary>
    public const string ThreadIdempotencyIndexName = "UIX_OtaMessages_ThreadId_IdempotencyKey";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, the one of its thread.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The thread the message belongs to; the messages go with it. FK, CASCADE.</summary>
    public Guid ThreadId { get; set; }

    /// <summary>The message on Octorate (a number, kept as text); <c>null</c> until Octorate answers for a message CasaZen sent.</summary>
    [MaxLength(OctorateMessageIdMaxLength)]
    public string? OctorateMessageId { get; set; }

    public OtaMessageDirection Direction { get; set; }

    /// <summary>The name of the sender as the channel gives it. Personal data; empty when the message does not say.</summary>
    [MaxLength(SenderNameMaxLength)]
    public string SenderName { get; set; } = string.Empty;

    /// <summary>What was written. <b>Encrypted at rest</b>; empty for a message that holds only an attachment, and once anonymized.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The address of the attachment on the channel manager, as given: no copy of the file is kept. Never returned to a client.</summary>
    [MaxLength(AttachmentUrlMaxLength)]
    public string? AttachmentUrl { get; set; }

    /// <summary>The content type of the attachment: <c>image/jpeg</c>, <c>image/png</c> or <c>application/octet-stream</c>.</summary>
    [MaxLength(AttachmentContentTypeMaxLength)]
    public string? AttachmentContentType { get; set; }

    /// <summary>When the message was sent (UTC).</summary>
    public DateTime SentAt { get; set; }

    /// <summary>When the host side read an inbound message (UTC); <c>null</c> while nobody did.</summary>
    public DateTime? ReadAt { get; set; }

    public OtaMessageDeliveryState DeliveryState { get; set; }

    /// <summary>The key the sender's screen made for this message: the same key twice in a thread is the same message. The unique index lets nulls through.</summary>
    [MaxLength(IdempotencyKeyMaxLength)]
    public string? IdempotencyKey { get; set; }

    public MessageOrigin Origin { get; set; }

    /// <summary>The rule of the assistant that produced an assistant message (E05/F05); <c>null</c> for the others.</summary>
    [MaxLength(RuleCodeMaxLength)]
    public string? RuleCode { get; set; }

    /// <summary>The user who wrote it in CasaZen (<c>User.Id</c>), an opaque id with no foreign key; <c>null</c> for the others.</summary>
    [MaxLength(SentByUserIdMaxLength)]
    public string? SentByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
