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

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
