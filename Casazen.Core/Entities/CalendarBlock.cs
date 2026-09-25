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

    /// <summary>
    /// Import feed the block comes from (PC-11): each feed replaces only its own blocks, and removing the feed deletes
    /// them (FK cascade). Null for blocks that do not come from a feed (<see cref="CalendarBlockSource.Manual"/>).
    /// <see cref="ExternalUid"/> is unique per feed.
    /// </summary>
    public Guid? FeedId { get; set; }

    [MaxLength(ExternalUidMaxLength)]
    public string? ExternalUid { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    [MaxLength(SummaryMaxLength)]
    public string? Summary { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// The OTA stay the host created from this imported block (CO-21, decision D7). The block stays linked to it and keeps
    /// following its feed; while the stay is not cancelled and has the block's dates, the nights are counted once,
    /// through the stay (<see cref="Services.PropertyOccupancy.BlockTakesNightIn"/>). Null for a block not converted.
    /// Unique: one stay per block.
    /// </summary>
    public Guid? BookingId { get; set; }

    [ForeignKey(nameof(BookingId))]
    public Booking? Booking { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(FeedId))]
    public PropertyICalFeed? Feed { get; set; }

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
