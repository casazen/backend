using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("ProcessedStripeEvents")]
public class ProcessedStripeEvent
{
    [Key, MaxLength(255)]
    public string EventId { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string EventType { get; set; } = string.Empty;

    public WebhookSource Source { get; set; }

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
