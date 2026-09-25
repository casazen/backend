using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One push message of one event to one device (MO-04, A6-29): the delivery job claims a row per (event key, push token)
/// before sending, so a retry of the job, or the same event queued twice, never sends a device a second message. The
/// row then keeps the Expo ticket until the recurring receipts job reads its receipt. Rows are deleted after a few days
/// (<c>PushReceiptService.RetentionDays</c>). No personal data: no text of the message, no user.
/// </summary>
[Table("PushDeliveries")]
[Index(nameof(DeliveryKey), nameof(PushToken), IsUnique = true)]
[Index(nameof(Status), nameof(SentAt))]
[Index(nameof(CreatedAt))]
public class PushDelivery
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identity of the event (<c>PushDeliveryKeys</c>), e.g. <c>service-request:{id}:Rifiutato</c>.</summary>
    [Required, MaxLength(200)]
    public string DeliveryKey { get; set; } = string.Empty;

    /// <summary>Expo push token the message goes to. Never logged.</summary>
    [Required, MaxLength(512)]
    public string PushToken { get; set; } = string.Empty;

    /// <summary>Device registration the token was read from (no foreign key: the device may be removed since).</summary>
    public Guid? DeviceRegistrationId { get; set; }

    /// <summary>Kind of push (<c>PushTypes</c>), for the logs.</summary>
    [Required, MaxLength(64)]
    public string Type { get; set; } = string.Empty;

    public PushDeliveryStatus Status { get; set; } = PushDeliveryStatus.Pending;

    /// <summary>Id of the Expo push ticket, used to read the receipt.</summary>
    [MaxLength(64)]
    public string? TicketId { get; set; }

    /// <summary>Expo error code of the ticket or receipt (e.g. <c>DeviceNotRegistered</c>), never Expo's message text.</summary>
    [MaxLength(64)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the message was handed to Expo.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>UTC instant of the final outcome (ticket error, receipt, no receipt in time).</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>State of a <see cref="PushDelivery"/> (stored as an integer).</summary>
public enum PushDeliveryStatus
{
    /// <summary>To send: new, or handed back after Expo certainly did not take it (HTTP 429/5xx, no connection).</summary>
    Pending = 0,

    /// <summary>
    /// Handed to Expo without a recorded outcome (timeout, crash of the worker): never sent again, so a device gets at
    /// most one message per event.
    /// </summary>
    Sending = 1,

    /// <summary>Expo returned an ok ticket: the receipt is not read yet.</summary>
    Accepted = 2,

    /// <summary>The receipt is ok: Expo handed the message to FCM or APNs.</summary>
    Delivered = 3,

    /// <summary>Ticket or receipt error, or the request refused by Expo (<see cref="PushDelivery.Error"/>).</summary>
    Failed = 4,

    /// <summary>No receipt within 24 hours of the send (Expo keeps receipts for a day).</summary>
    ReceiptUnavailable = 5,
}
