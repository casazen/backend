using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A point of interest near a short-stay property (DB-11a): a beach, a place to eat, a shop, a station... with the time it takes to
/// get there and where it lies, for the «Dintorni» section and the local map of the property page. A property with no row has no
/// points of interest, which is every property that existed before the table.
/// </summary>
/// <remarks>
/// <para><b>Privacy.</b> The position of a house is private (§ 11.7 of the direct booking runbook): the public page never shows its
/// address or exact coordinates. So a point of interest is not placed by coordinates, and nothing here is worked out from the
/// exact position of the property: the host states the minutes, the side of the compass and the distance, and the server stores
/// the side as one of eight <see cref="CompassPoint"/> and the distance <b>rounded</b> (<c>PoiDistance.Round</c>: to 100 m below
/// 1 km, 250 m below 5 km, 500 m below 20 km, then 1 km) and never closer than <see cref="MinDistanceMeters"/>. A named place a few metres away on a known
/// side would give the door away, whatever the rounding of the coordinates.</para>
/// <para>Rows follow their property (cascade) and carry its <see cref="OrgId"/> (TN-2).</para>
/// </remarks>
[Table("PropertyPois")]
public class PropertyPoi : ITenantOwned
{
    /// <summary>At most this many points a property can list.</summary>
    public const int MaxPerProperty = 12;

    public const int NameMaxLength = 80;

    /// <summary>Closest a point is stored and shown: nearer is stored as this.</summary>
    public const int MinDistanceMeters = 200;

    /// <summary>Farthest a point is stored and shown (an airport two hours away is still «far»).</summary>
    public const int MaxDistanceMeters = 200_000;

    public const int MinMinutes = 1;

    public const int MaxMinutes = 600;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }

    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Order in the list of the page, 0 first; the host reorders them.</summary>
    public int Position { get; set; }

    public PoiKind Kind { get; set; }

    /// <summary>What the guest reads, in Italian: «Spiaggia di Costa Merlata».</summary>
    [Required, MaxLength(NameMaxLength)]
    public string NameIt { get; set; } = string.Empty;

    /// <summary>The same in English; null = the host wrote none and the page shows <see cref="NameIt"/>.</summary>
    [MaxLength(NameMaxLength)]
    public string? NameEn { get; set; }

    /// <summary>How the <see cref="Minutes"/> are travelled.</summary>
    public PoiTravelMode Mode { get; set; }

    /// <summary>Minutes to get there, <see cref="MinMinutes"/> to <see cref="MaxMinutes"/>.</summary>
    public int Minutes { get; set; }

    /// <summary>Side of the compass the point lies on, seen from the house (never a bearing in degrees).</summary>
    public CompassPoint Direction { get; set; }

    /// <summary>Distance in metres, already rounded when stored, <see cref="MinDistanceMeters"/> to <see cref="MaxDistanceMeters"/>.</summary>
    public int DistanceMeters { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
