using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One popup alert for one user (ADR-025, NF-08, NF-22): "something needs you now", shown outside the wizard flow with the choices
/// «Do now» and «Remind me later». It is written by <c>IAlertRouter</c> for the alerted user, pushed to the open sessions of that user
/// on the SignalR hub <c>/hubs/notifications</c> (client method <c>alertReceived</c>) and read from <c>GET /api/me/alerts/pending</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No text, no personal data.</b> The row says what kind of alert it is (<see cref="AlertType"/>), which entity it is about
/// (<see cref="EntityType"/> + <see cref="EntityId"/>) and how urgent it is; the client writes the title in the language of the user and
/// reads the detail from the API of the entity (ADR-025-R5). No guest name, no property name, no free text ever lands here: a row is a
/// user id, a kind and a reference.</item>
/// <item><b>Pending</b> = not dismissed and either never snoozed or snoozed until an instant that has passed
/// (<c>DismissedAt IS NULL AND (RemindAt IS NULL OR RemindAt &lt;= now)</c>). An alert raised during the quiet hours of the user is stored at
/// once with <see cref="RemindAt"/> set to the end of the quiet hours, so it is not lost and not shown before.</item>
/// <item><b>Delivery.</b> <see cref="DeliveredAt"/> is when the alert was last pushed to the open sessions of the user: the router sets
/// it when it publishes at once, the job <c>popup-alert-due</c> sets it for the alerts deferred by the quiet hours or snoozed, and a snooze
/// clears it, so a deferred or snoozed alert reaches an open session once when it falls due and not again until it is snoozed again.</item>
/// <item><b>Tenant</b> (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the host org of the alerted user. The rows go with the user
/// and with the org (foreign keys <c>ON DELETE CASCADE</c>), as the in-app notifications do.</item>
/// </list>
/// </remarks>
[Table("PopupAlerts")]
public class PopupAlert : ITenantOwned
{
    public const int UserIdMaxLength = 255;

    public const int AlertTypeMaxLength = 50;

    public const int EntityTypeMaxLength = 64;

    public const string BookingEntityType = "Booking";

    public const string PropertyEntityType = "Property";

    public const string PendingIndexName = "IX_PopupAlerts_UserId_DismissedAt_RemindAt";

    public const string DueIndexName = "IX_PopupAlerts_RemindAt_Due";

    /// <summary>
    /// The legal alerts: the ones that bypass the quiet hours, because a legal deadline does not wait for the morning
    /// (ADR-025-R3). Every other alert raised during the quiet hours of its user is deferred until they end.
    /// </summary>
    public static bool IsLegal(AlertType type) => type is AlertType.AlloggiatiDeadline or AlertType.IstatDeadline;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    public AlertType AlertType { get; set; }

    [Required, MaxLength(EntityTypeMaxLength)]
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public AlertUrgency Urgency { get; set; } = AlertUrgency.Normal;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DismissedAt { get; set; }

    /// <summary>
    /// UTC instant from which the alert is pending: the end of the quiet hours it was raised in, or the end of the snooze the user chose;
    /// <c>null</c> = pending since it was raised.
    /// </summary>
    public DateTime? RemindAt { get; set; }

    /// <summary>
    /// UTC instant of a legal deadline the alert is about (Alloggiati, ISTAT); <c>null</c> when there is none. The user cannot snooze the
    /// alert past it (ADR-025-R10).
    /// </summary>
    public DateTime? Deadline { get; set; }

    /// <summary>
    /// UTC instant the alert was last pushed to the open sessions of the user; <c>null</c> = not pushed yet (or snoozed since). The
    /// push is a hint: the alert is always readable from <c>GET /api/me/alerts/pending</c> once it is pending.
    /// </summary>
    public DateTime? DeliveredAt { get; set; }
}
