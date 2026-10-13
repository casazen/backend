using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One notification of the bell of the shell, for one user (UI-12a, behind <c>Features:InAppNotifications</c>): "something
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
    /// <summary>Column length of <see cref="DeliveryKey"/>: the one of <c>PushDelivery.DeliveryKey</c> (a push key is at most 200 characters).</summary>
    public const int DeliveryKeyMaxLength = 200;

    /// <summary>Column length of <see cref="Type"/>: the one of <c>PushDelivery.Type</c>.</summary>
    public const int TypeMaxLength = 64;

    /// <summary>Name of the unique index on (<see cref="DeliveryKey"/>, <see cref="UserId"/>): a 23505 on it means the row already exists.</summary>
    public const string OncePerEventAndUserIndexName = "UIX_InAppNotifications_DeliveryKey_UserId";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org the event belongs to (see the remarks). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The user who is told (<c>User.Id</c>, the Auth0 subject). Foreign key with <c>ON DELETE CASCADE</c>.</summary>
    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Kind of event: a value of <c>PushTypes</c> (e.g. <c>new-booking</c>). The client picks the text from it.</summary>
    [Required, MaxLength(TypeMaxLength)]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The service request the event is about for the <c>service-request-*</c> types, the booking for every other one;
    /// <c>null</c> when the event names none. Not a foreign key: the notification outlives the removal of what it points to.
    /// </summary>
    public Guid? EntityId { get; set; }

    /// <summary>Identity of the event: the delivery key of its push (<c>PushDeliveryKeys</c>). Never shown.</summary>
    [Required, MaxLength(DeliveryKeyMaxLength)]
    public string DeliveryKey { get; set; } = string.Empty;

    /// <summary>UTC instant of the event, whole microseconds.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the user read it (or marked every notification as read); <c>null</c> while unread.</summary>
    public DateTime? ReadAt { get; set; }
}

/// <summary>Limits of the in-app notifications (UI-12a).</summary>
public static class InAppNotificationLimits
{
    /// <summary>
    /// A notification is deleted this many days after it was created, read or not (decision of the task UI-12a: the bell is a
    /// feed of what happened lately, not an archive; the things that need an answer stay on their own screens).
    /// </summary>
    public const int RetentionDays = 90;

    /// <summary>Notifications in a page of the list when the client does not say.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Most notifications in a page of the list.</summary>
    public const int MaxPageSize = 50;
}
