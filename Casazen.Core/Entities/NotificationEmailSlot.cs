using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The slot of the rule «at most one email per conversation every ten minutes» (E05/F03, NF-65, <c>GuestMessageRules.NotifyIntervalMinutes</c>):
/// when the last email about a conversation was sent, or is waiting for the end of the quiet hours of a person, for each person who is
/// alerted. One row per user and conversation; no text, no name, no address.
/// </summary>
/// <remarks>
/// <para>The slot is claimed with one conditional statement (<c>HostMessageAlertDelivery</c>): two
/// messages at the same moment queue one email for the person, and an email deferred by the quiet hours holds the slot until the
/// instant it is due, so the messages of the night are one email in the morning and not one each.</para>
/// <para>The row goes with the user and with the org (foreign keys <c>ON DELETE CASCADE</c>); a row not claimed for a week is
/// deleted by the next claim of the same person. <see cref="ThreadKey"/> is a key of the hub of messages (<c>ota:{guid}</c>,
/// <c>direct:{guid}</c>), not a foreign key: a conversation that retention deletes leaves a row that expires by itself.</para>
/// </remarks>
[Table("NotificationEmailSlots")]
public class NotificationEmailSlot : ITenantOwned
{
    /// <summary>Column length of <see cref="ThreadKey"/>.</summary>
    public const int ThreadKeyMaxLength = 64;

    /// <summary>Name of the unique index on <c>(OrgId, UserId, ThreadKey)</c>: a 23505 on it means the slot exists.</summary>
    public const string OncePerUserAndThreadIndexName = "UIX_NotificationEmailSlots_OrgId_UserId_ThreadKey";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the host org of the user. Server-set.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The person who is emailed (<c>User.Id</c>). Foreign key with <c>ON DELETE CASCADE</c>.</summary>
    [Required, MaxLength(PopupAlert.UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The conversation in the hub of messages: <c>ota:{guid}</c> or <c>direct:{guid}</c>.</summary>
    [Required, MaxLength(ThreadKeyMaxLength)]
    public string ThreadKey { get; set; } = string.Empty;

    /// <summary>
    /// UTC instant of the last email about the conversation to the person, or the instant a deferred one is due. The slot is free again
    /// ten minutes after it.
    /// </summary>
    public DateTime LastEmailAt { get; set; }
}
