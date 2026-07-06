using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("CalendarBlocks")]
public class CalendarBlock
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    public CalendarBlockSource Source { get; set; } = CalendarBlockSource.ICalImport;

    [MaxLength(500)]
    public string? ExternalUid { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    [MaxLength(500)]
    public string? Summary { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
