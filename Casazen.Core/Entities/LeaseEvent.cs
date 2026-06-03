using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("LeaseEvents")]
public class LeaseEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid LeaseContractId { get; set; }

    [ForeignKey(nameof(LeaseContractId))]
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required]
    public LeaseEventType EventType { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public string? Payload { get; set; }
}
