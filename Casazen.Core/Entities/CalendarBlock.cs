using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("CalendarBlocks")]
public class CalendarBlock : ITenantOwned
{
    public const int ExternalUidMaxLength = 500;

    public const int SummaryMaxLength = 500;

    public const int ManualNoteMaxLength = 200;

    public const string OctorateUidIndexName = "UIX_CalendarBlocks_PropertyId_ExternalUid_Octorate";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    public CalendarBlockSource Source { get; set; } = CalendarBlockSource.ICalImport;

    public Guid? FeedId { get; set; }

    [MaxLength(ExternalUidMaxLength)]
    public string? ExternalUid { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    [MaxLength(SummaryMaxLength)]
    public string? Summary { get; set; }

    public CalendarBlockReason? ManualReason { get; set; }

    [MaxLength(3)]
    public string? Currency { get; set; }

    public DateTime? LastSyncedAt { get; set; }

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
