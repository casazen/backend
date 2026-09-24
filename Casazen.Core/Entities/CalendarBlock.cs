using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("CalendarBlocks")]
public class CalendarBlock : ITenantOwned
{
    /// <summary>Column length of <see cref="ExternalUid"/>: longer feed UIDs are stored as a hash (PC-10).</summary>
    public const int ExternalUidMaxLength = 500;

    /// <summary>Column length of <see cref="Summary"/>: longer feed summaries are truncated (PC-10).</summary>
    public const int SummaryMaxLength = 500;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    public CalendarBlockSource Source { get; set; } = CalendarBlockSource.ICalImport;

    [MaxLength(ExternalUidMaxLength)]
    public string? ExternalUid { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    [MaxLength(SummaryMaxLength)]
    public string? Summary { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
