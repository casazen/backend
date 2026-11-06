using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One iCal import feed of a property: a property has as many as the host links (Airbnb, Booking.com, ...), each
/// synced on its own and owning its <see cref="CalendarBlock"/>s (PC-11, A2-11). The export link of the property is
/// <see cref="PropertyICalExport"/>.
/// </summary>
[Table("PropertyICalFeeds")]
public class PropertyICalFeed : ITenantOwned
{
    /// <summary>Longest import URL accepted from the host.</summary>
    public const int ImportUrlMaxLength = 2048;

    /// <summary>
    /// Column length of <see cref="ImportUrl"/>: the URL is stored encrypted (Data Protection payload, about 4/3 of
    /// the URL plus a fixed header), so the column is wider than <see cref="ImportUrlMaxLength"/> (A2-20).
    /// </summary>
    public const int StoredImportUrlMaxLength = 4096;

    /// <summary>Longest free-text label of a feed.</summary>
    public const int LabelMaxLength = 60;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    public ICalFeedChannel Channel { get; set; } = ICalFeedChannel.Other;

    /// <summary>Optional host label (e.g. "Booking.com - camera 2"); the UI shows the channel when missing.</summary>
    [MaxLength(LabelMaxLength)]
    public string? Label { get; set; }

    /// <summary>
    /// Import URL, encrypted at rest by the value converter of <c>AppDbContext</c> (A2-20): Airbnb links carry a token
    /// that gives access to the reservations. Never logged, never returned by the API (only a masked form).
    /// </summary>
    [MaxLength(StoredImportUrlMaxLength)]
    public string? ImportUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastImportAt { get; set; }

    public PropertyICalImportStatus? LastImportStatus { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// The Octorate calendar of this feed's channel replaced the iCal import (E02/F02/US01/T03, ADR-002-R11, R14): the property has a confirmed
    /// Octorate mapping that includes the channel, and the first read of the Octorate calendar of that listing succeeded. The batch sync skips a
    /// deprecated feed; the feed and its blocks are kept, and the export is untouched. A feed of channel <see cref="ICalFeedChannel.Other"/> is
    /// never deprecated automatically: Octorate does not know its source.
    /// </summary>
    public bool Deprecated { get; set; }

    /// <summary>When the feed was deprecated (UTC); <c>null</c> while it is not.</summary>
    public DateTime? DeprecatedAt { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
