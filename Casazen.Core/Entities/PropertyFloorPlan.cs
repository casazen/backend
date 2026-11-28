using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One level of the floor plan of a short-stay property (DB-11a): the picture the host uploaded (PNG or JPEG) with a name the guest
/// reads in Italian and, if the host wrote it, in English. The property page of the booking site shows the levels lowest
/// <see cref="Level"/> first. A property with no row has no floor plan, which is every property that existed before the table;
/// nothing here reads into a booking, a price or a refund.
/// </summary>
/// <remarks>
/// The image is an object of the public bucket in the property's own folder (<c>StorageKeys.PropertyFloorPlan</c>) and
/// <see cref="ImageUrl"/> is its absolute URL, like the photos of the gallery. Rows follow their property (cascade) and carry
/// its <see cref="OrgId"/> (TN-2), so the tenant filter and the checks of the host forms apply as to every child row.
/// </remarks>
[Table("PropertyFloorPlans")]
public class PropertyFloorPlan : ITenantOwned
{
    public const int MaxPerProperty = 6;

    public const int NameMaxLength = 60;

    public const int ImageUrlMaxLength = 2000;

    public const int MinAreaSqm = 1;

    public const int MaxAreaSqm = 9999;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }

    public virtual Property Property { get; set; } = null!;

    public Guid OrgId { get; set; }

    public int Level { get; set; }

    public int Position { get; set; }

    [Required, MaxLength(NameMaxLength)]
    public string NameIt { get; set; } = string.Empty;

    [MaxLength(NameMaxLength)]
    public string? NameEn { get; set; }

    [Required, MaxLength(ImageUrlMaxLength)]
    public string ImageUrl { get; set; } = string.Empty;

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }

    public int? AreaSqm { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
