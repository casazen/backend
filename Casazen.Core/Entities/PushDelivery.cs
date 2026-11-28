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

    [Required, MaxLength(200)]
    public string DeliveryKey { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string PushToken { get; set; } = string.Empty;

    /// <summary>Device registration the token was read from (no foreign key: the device may be removed since).</summary>
    public Guid? DeviceRegistrationId { get; set; }

    [Required, MaxLength(64)]
    public string Type { get; set; } = string.Empty;

    public PushDeliveryStatus Status { get; set; } = PushDeliveryStatus.Pending;

    [MaxLength(64)]
    public string? TicketId { get; set; }

    [MaxLength(64)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SentAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}

public enum PushDeliveryStatus
{
    Pending = 0,

    Sending = 1,

    Accepted = 2,

    Delivered = 3,

    Failed = 4,

    ReceiptUnavailable = 5,
}
