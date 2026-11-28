using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One notification of the bell of the shell, for one user (UI-12a): "something
/// happened to a booking or a service request you follow". It is written by <c>InAppNotificationJob</c>, queued next to every
/// push the backend sends (<c>IPushNotificationService</c>), so the two cannot disagree on who is told. Runbook:
/// <c>docs/runbooks/in-app-notifications.md</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No text, no personal data.</b> The row says <see cref="Type"/> (a value of <c>PushTypes</c>) and which booking or
/// service request it is about (<see cref="EntityId"/>); the client writes the sentence from the type, in the language of the
/// user. No guest name, no property name, no free text ever lands here: a row is only a user id, a kind of event and an id.</item>
/// <item><b>Once per event and user</b>: <c>(DeliveryKey, UserId)</c> is unique. <see cref="DeliveryKey"/> is the key of the
/// push of the same event (<c>PushDeliveryKeys</c>), so a retry of the job, or the same event queued twice, never gives a user a
/// second row.</item>
/// <item><b>Tenant</b> (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the org the event belongs to, the host org of the
/// booking or property, or the supplier org for the supplier's inbox. A user reads only its own rows of the orgs it belongs to
/// (<c>User.OrgId</c>, <c>User.SupplierOrgId</c>): a supplier-only account has no host org, so the host-org query filter would
/// hide every row from it, and the service says the org explicitly instead.</item>
/// <item><b>Erasure and retention.</b> Rows go with the user and with the org (foreign keys <c>ON DELETE CASCADE</c>), and the
/// daily job <c>in-app-notification-retention</c> deletes the ones older than
/// <see cref="InAppNotificationLimits.RetentionDays"/> days, read or not.</item>
/// </list>
/// </remarks>
[Table("InAppNotifications")]
public class InAppNotification : ITenantOwned
{
    public const int DeliveryKeyMaxLength = 200;

    public const int TypeMaxLength = 64;

    public const string OncePerEventAndUserIndexName = "UIX_InAppNotifications_DeliveryKey_UserId";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(TypeMaxLength)]
    public string Type { get; set; } = string.Empty;

    public Guid? EntityId { get; set; }

    [Required, MaxLength(DeliveryKeyMaxLength)]
    public string DeliveryKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReadAt { get; set; }
}

public static class InAppNotificationLimits
{
    public const int RetentionDays = 90;

    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;
}
